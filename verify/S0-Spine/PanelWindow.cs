using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using static S0Spine.NativeMethods;

namespace S0Spine;

/// <summary>
/// S0 的"面板"。UI 依然很朴素 —— 这是探针不是产品。
///
/// 唯一要紧的是三件事：
///   1. 它带着 WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW 出生
///   2. 用 SetWindowPos + SWP_NOACTIVATE 显示，绝不调用 Activate()
///   3. 点一条 → 写回剪贴板 → SendInput Ctrl+V
///
/// 外观是纯实色、不透明、抗锯齿圆角 —— **故意不用任何模糊材质**。
/// 理由：把"透明度"和"焦点"这两个变量分开测。这一轮只看焦点。
///
/// ⚠️ 这里**不开** AllowsTransparency。开了会给窗口加 WS_EX_LAYERED，
/// 整张位图由 WPF 自己提供 —— 窗口会变成一张自己的画布，
/// 而我们要的圆角是"窗口本身"的圆角。用 SetWindowRgn 才是对的。
/// </summary>
internal sealed class PanelWindow : Window
{
    private readonly ClipboardHistory _history;
    private readonly Action<string> _log;
    private readonly StackPanel _list = new();

    /// <summary>点击条目后交给外面去粘贴</summary>
    public event Action<string>? ItemActivated;

    public PanelWindow(ClipboardHistory history, Action<string> log)
    {
        _history = history;
        _log = log;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;          // 不抢焦点的第一道保险
        Topmost = true;
        AllowsTransparency = false;     // ★ 不开分层窗口 —— 见类注释
        Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F));

        Width = 360;
        Height = 460;

        Content = BuildContent();
        SourceInitialized += OnSourceInitialized;
    }

    private UIElement BuildContent()
    {
        var root = new DockPanel { Margin = new Thickness(12) };

        var header = new TextBlock
        {
            Text = "ClipDesk 探针",
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xE6, 0xFF)),
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 4),
        };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var hint = new TextBlock
        {
            Text = "按住 Alt 显示 · 松开 Alt 收起\n点一条即粘贴回原来的窗口",
            Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            FontSize = 12,
            LineHeight = 18,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        };
        DockPanel.SetDock(hint, Dock.Top);
        root.Children.Add(hint);

        // ★ 横向滚动关掉，并让每个条目自己换行。
        //   上一版第一列文字"消失"，是因为 StackPanel 在 ScrollViewer 里
        //   拿不到宽度约束，横向撑出去被裁掉了。
        root.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _list,
        });

        return root;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _hwnd = hwnd;

        // 第二道保险：扩展样式必须在窗口首次显示前就位
        IntPtr exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        IntPtr wanted = (IntPtr)(exStyle.ToInt64() | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, wanted);

        IntPtr after = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        bool noActivate = (after.ToInt64() & WS_EX_NOACTIVATE) != 0;
        bool toolWindow = (after.ToInt64() & WS_EX_TOOLWINDOW) != 0;

        _log($"  ★ 扩展样式已设置: NOACTIVATE={(noActivate ? "是" : "否")}  TOOLWINDOW={(toolWindow ? "是" : "否")}");

        // Win11 默认那圈 1px 描边去掉
        int none = unchecked((int)DWMWA_COLOR_NONE);
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref none, sizeof(int));

        // 系统给的抗锯齿圆角。这和 SetWindowRgn 是**二选一**的：
        // region 是 1-bit 遮罩，边会锯齿；DWM 圆角是真的抗锯齿。
        int round = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

        // 深色标题栏分支，避免浅色描边和深色底对不上
        int dark = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    private IntPtr _hwnd;

    /// <summary>
    /// 面板是不是还显示着 —— 外部定时器用它兜底，防止标志位和实际状态脱节。
    /// </summary>
    public bool IsPanelVisible => _hwnd != IntPtr.Zero && IsWindowVisibleNative(_hwnd);

    /// <summary>强制收起。任何异常路径都用它，保证不留悬挂窗口。</summary>
    public void ForceHide()
    {
        if (_hwnd == IntPtr.Zero) return;
        ShowWindow(_hwnd, SW_HIDE);
    }

    /// <summary>
    /// 显示面板。用 SetWindowPos 而不是 WPF 的 Show()：
    ///   SetWindowPos + SWP_NOACTIVATE 是系统层面保证"显示但不激活"，
    ///   这是 Q1 的核心动作，也是整个产品「不抢焦点」依赖的那一个调用。
    ///
    /// 位置固定：**鼠标当前所在显示器的物理中心**。
    /// 不跟鼠标走 —— 鼠标在屏幕下方时也不能跑到下方去。
    /// </summary>
    public void ShowNoActivate()
    {
        // ★ 顺序很重要：先把内容填好并布局，再显示。
        // 反过来的话，窗口先以空内容显示，WPF 之后不会再补画一遍 ——
        // 这就是上一版"一个字都没有 + 纯透明"的原因。
        Refresh();
        UpdateLayout();

        var (xPx, yPx, cxPx, cyPx, scale, monitorRect) = ComputeCenter();

        // ★ SetWindowPos 收的是**物理像素**。
        // WPF 的 Left/Top/Width/Height 是 DIP，100% 缩放下数值相同，
        // 125%/150% 下差很多 —— 上一版偏到左上角就是这个原因。
        // 这里带 SWP_NOSIZE，尺寸交给 WPF 自己管，我们只摆位置。
        int shown = 0;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            SetWindowPos(_hwnd, HWND_TOPMOST, xPx, yPx, 0, 0,
                         SWP_NOACTIVATE | SWP_NOSIZE | SWP_SHOWWINDOW);
            shown++;

            // 200ms 内没达到目标位置就再摆一次。
            // 这一层保险是给"从没显示过的窗口"用的：WPF 首帧布局可能
            // 把系统摆的位置覆盖掉。
            Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            if (IsAt(xPx, yPx)) break;
            System.Threading.Thread.Sleep(60);
        }

        _lastShown = $"物理({xPx},{yPx}) 目标{cxPx}×{cyPx} 缩放{scale:0.##}× "
                   + $"工作区[{monitorRect}] 摆放{shown}次";
    }

    /// <summary>窗口现在是不是已经落在目标位置上（容差 4px）</summary>
    private bool IsAt(int xPx, int yPx)
    {
        var r = new RECT();
        if (!GetWindowRect(_hwnd, out r)) return true;   // 拿不到就当成功，避免死循环
        return Math.Abs(r.Left - xPx) <= 4 && Math.Abs(r.Top - yPx) <= 4;
    }

    private string _lastShown = "(还没显示过)";

    public string LastShownPosition => _lastShown;

    /// <summary>列表里现在有多少行 —— 用来证明"真的有内容"，不是空窗口</summary>
    public int RenderedRowCount => _list.Children.Count;

    /// <summary>
    /// 启动时"预热"：让 WPF 把窗口真的走一遍显示流程，再藏起来。
    ///
    /// ★ 这里必须用 WPF 的 Show()，不能用 SetWindowPos 代替。
    ///   原因（这是"全黑没字"的根因）：
    ///   EnsureHandle() 只建了 HWND，WPF 的 Window 从没走过显示流程，
    ///   视觉树没被 Measure/Arrange，渲染管线也没接管 ——
    ///   窗口画得出 Background（那是 HWND 的底色），但画不出 Content。
    ///   所以之前每次都是"一个纯色空框"。
    ///
    ///   Show() 之后 WPF 内部状态就绪，此后的显隐可以只用 SetWindowPos。
    ///
    /// Show() 会不会抢焦点？ShowActivated=false 已经设了，
    /// 而且这一下发生在**程序启动时**，不在热键路径上，不影响 Q1。
    /// </summary>
    public void Prewarm()
    {
        // 先挪到屏幕外，避免预热这一下闪一下
        SetWindowPos(_hwnd, HWND_TOPMOST, -32000, -32000, 0, 0,
                     SWP_NOACTIVATE | SWP_NOSIZE | SWP_SHOWWINDOW);

        Refresh();

        // ★ 走一遍 WPF 的显示流程 —— 这一步不能省
        Show();
        UpdateLayout();

        // 逼渲染管线真的出一帧
        Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        // 藏起来，但**不用** Hide()（那会让 WPF 认为窗口关闭，
        // 下次 SetWindowPos 又会画不出内容）。直接移动 HWND。
        SetWindowPos(_hwnd, HWND_TOPMOST, -32000, -32000, 0, 0,
                     SWP_NOACTIVATE | SWP_NOSIZE);

        ShowWindow(_hwnd, SW_HIDE);
    }

    /// <summary>
    /// 算出面板该落在哪 —— 鼠标所在显示器的工作区正中。
    /// 返回的一律是**物理像素**，因为 SetWindowPos 要的就是物理像素。
    /// </summary>
    private (int X, int Y, int W, int H, double Scale, string Rect) ComputeCenter()
    {
        GetCursorPos(out POINT cursor);
        IntPtr monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);

        GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _);
        double scale = dpiX > 0 ? dpiX / 96.0 : 1.0;

        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            // 拿不到就退回主屏
            int w0 = (int)Math.Round(Width * scale);
            int h0 = (int)Math.Round(Height * scale);
            return ((int)((SystemParameters.PrimaryScreenWidth - Width) / 2),
                    (int)((SystemParameters.PrimaryScreenHeight - Height) / 2),
                    w0, h0, scale, "取不到");
        }

        int workW = info.rcWork.Right - info.rcWork.Left;
        int workH = info.rcWork.Bottom - info.rcWork.Top;

        // DIP → 物理像素
        int wPx = (int)Math.Round(Width * scale);
        int hPx = (int)Math.Round(Height * scale);

        int xPx = info.rcWork.Left + (workW - wPx) / 2;
        int yPx = info.rcWork.Top + (workH - hPx) / 2;

        // 万一算出来是负数（缩放异常 / 工作区异常），钳回工作区内
        xPx = Math.Max(info.rcWork.Left, Math.Min(xPx, info.rcWork.Right - wPx));
        yPx = Math.Max(info.rcWork.Top, Math.Min(yPx, info.rcWork.Bottom - hPx));

        return (xPx, yPx, wPx, hPx, scale,
                $"{info.rcWork.Left},{info.rcWork.Top} {workW}×{workH}");
    }

    public void HidePanel() => ForceHide();

    /// <summary>
    /// 刷新列表。每次显示都调用。
    ///
    /// ★ 之前"没字"的根因就在这里：ShowNoActivate 里调了 Refresh()，
    /// 但_WPF 在窗口**首次**显示前不会真正跑布局 —— 那时 Items 还是空的，
    /// 于是画出一张空画布，之后再刷新也没用（没有触发重绘）。
    /// 现在的做法：显示完之后再用 Dispatcher 刷一次，确保内容真的画上去。
    /// </summary>
    private void Refresh()
    {
        _list.Children.Clear();

        var items = _history.Items;
        if (items.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = "（还没有历史 —— 先在别的窗口复制点什么）",
                Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                FontSize = 14,
                Margin = new Thickness(4),
            });
            return;
        }

        for (int i = 0; i < items.Count; i++)
        {
            var entry = items[i];
            string preview = entry.Text.Replace("\r", " ").Replace("\n", " ");
            if (preview.Length > 60) preview = preview[..60] + "…";

            var row = new Border
            {
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(12, 11, 12, 11),
                Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5A)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = $"{i + 1}.   {preview}",
                    Foreground = Brushes.White,
                    FontSize = 15,
                    TextWrapping = TextWrapping.Wrap,
                },
                Tag = entry.Text,
            };

            row.MouseLeftButtonDown += OnItemClicked;
            _list.Children.Add(row);
        }
    }

    private void OnItemClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: string text }) return;

        // 先藏起来，再把焦点还给原窗口那边 —— 藏完再粘贴，视觉上更干净
        HidePanel();
        ItemActivated?.Invoke(text);
    }

    /// <summary>PanelWindow 是 NOACTIVATE 的，键盘事件收不到，Esc 交给外面轮询处理</summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private static bool IsWindowVisibleNative(IntPtr hWnd) => IsWindowVisible(hWnd);
}