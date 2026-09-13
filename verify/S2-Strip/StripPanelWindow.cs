using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using static S2Strip.NativeMethods;

namespace S2Strip;

/// <summary>
/// 右上角条子 + 展开面板 —— **同一个窗口长大**。
///
/// ── 为什么是一个窗口而不是两个 ────────────────────────────────
///   拆成两个窗口的话，鼠标从条子移进面板的瞬间会离开条子的 HWND，
///   触发"离开"—— 但那时候面板还没显示出来，触发源就没了。
///   同一个 HWND 长大，鼠标**从头到尾没离开过我们的窗口**，
///   这类 bug 被结构性地消掉了。
///
/// ── 两个状态 ──────────────────────────────────────────────────
///   收起：窗口 ≈ 10mm × 1mm（真实尺寸），里面画一条细线，鼠标穿透
///   展开：窗口 = 40mm × 64.7mm（黄金比），里面是历史列表，可点击
///
/// ── 坐标锚定 ──────────────────────────────────────────────────
///   窗口的**右上角**钉在锚点上不动，展开时向左下生长。
///   于是条子和面板右对齐，条子位置在两种状态下完全一致 ——
///   视觉上就是"从那一条线往下长出面板"。
///
/// ⚠️ 从 S0 抄来的两条教训（都踩过）：
///   1. SetWindowPos 收**物理像素**，Window.Width/Height 收 **DIP**。混用会
///      既偏移又"每次都缩小"。本类所有几何量都用 _px / _dip 后缀标明。
///   2. Prewarm() **必须真的调一次 WPF Show()** —— EnsureHandle() 只建 HWND，
///      视觉树从没 Measure/Arrange，渲染管线没接管，
///      用 SetWindowPos 显示出来只会得到一个**纯色空框**。
/// </summary>
internal sealed class StripPanelWindow : Window
{
    private static readonly Color PanelBg = Color.FromRgb(0x1C, 0x1C, 0x1C);
    private static readonly Color StripBg = Color.FromRgb(0x8A, 0x8A, 0x8A);
    private static readonly Color RowBg = Color.FromRgb(0x2A, 0x2A, 0x2A);
    private static readonly Color RowHover = Color.FromRgb(0x3D, 0x3D, 0x3D);
    private static readonly Color RowBorder = Color.FromRgb(0x3A, 0x3A, 0x3A);
    private static readonly Color Accent = Color.FromRgb(0x8C, 0xD0, 0xFF);

    private readonly ClipboardHistory _history;
    private readonly Action<string> _log;

    /// <summary>点了某一条 → 交给外面写剪贴板。参数是那条的文本。</summary>
    public event Action<string>? ItemActivated;

    private IntPtr _hwnd;
    private double _scale = 1.0;

    /// <summary>锚点（物理像素）：右上角钉在这里</summary>
    private int _anchorRightPx;
    private int _anchorTopPx;

    /// <summary>当前是不是展开态</summary>
    private bool _expanded;

    /// <summary>当前有没有设"鼠标穿透"</summary>
    private bool _clickThrough;

    // ── 视觉元素 ───────────────────────────────────────────────────
    private Border _panel = null!;
    private Border _strip = null!;
    private ScrollViewer _scroll = null!;
    private StackPanel _list = null!;
    private TextBlock _title = null!;

    public StripPanelWindow(ClipboardHistory history, Action<string> log)
    {
        _history = history;
        _log = log;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;

        // ★ 绝不开 AllowsTransparency。
        //   它会给 HWND 加 WS_EX_LAYERED，整张位图由 WPF 自己提供，
        //   将来换亚克力材质时 DWM 就无处可插了（CLAUDE.md 硬约束 2）。
        AllowsTransparency = false;

        // 背景设为深色 —— 窗口比可见条子大一点点，多出来的部分靠 SetWindowRgn 裁掉
        Background = new SolidColorBrush(PanelBg);

        Content = BuildContent();
        SourceInitialized += OnSourceInitialized;
    }

    // ── 构建 ────────────────────────────────────────────────────────

    private UIElement BuildContent()
    {
        var root = new Grid();

        // ① 面板：铺满窗口。收起时隐藏。
        _panel = new Border
        {
            Background = new SolidColorBrush(PanelBg),
            Visibility = Visibility.Collapsed,
            ClipToBounds = true,          // ★ S0 的教训：内容越界会把布局撑坏
        };
        root.Children.Add(_panel);

        var pane = new Grid();
        pane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 标题栏
        pane.RowDefinitions.Add(new RowDefinition());                              // 列表
        _panel.Child = pane;

        pane.Children.Add(BuildTitleBar());

        _list = new StackPanel();
        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(6, 4, 6, 6),
            Content = _list,
        };
        Grid.SetRow(_scroll, 1);
        pane.Children.Add(_scroll);

        // ② 条子：贴窗口上边缘、右对齐，真实 1mm 高。
        //    它永远画在窗口最上层（后 add 的在上），所以展开态也看得见 ——
        //    看起来就像面板是从这条线长出来的。
        _strip = new Border
        {
            Background = new SolidColorBrush(StripBg),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Height = Geometry.StripHeightDip,
        };
        root.Children.Add(_strip);

        return root;
    }

    private UIElement BuildTitleBar()
    {
        var bar = new Grid
        {
            Background = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x24)),
        };
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _title = new TextBlock
        {
            Foreground = new SolidColorBrush(Accent),
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 12,
            Margin = new Thickness(8, 5, 4, 5),
            Text = "粘贴板",
        };
        bar.Children.Add(_title);

        // ★ 滚动按钮：滚轮如果收不到（WM_MOUSEWHEEL 只发给焦点窗口，
        //   我们是 NOACTIVATE 永远没焦点），这两个按钮就是保底。
        //   见 S2 的 Q5。
        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 2, 4, 2),
        };
        btns.Children.Add(MakeScrollButton("▲", -1));
        btns.Children.Add(MakeScrollButton("▼", +1));
        Grid.SetColumn(btns, 1);
        bar.Children.Add(btns);

        return bar;
    }

    private Button MakeScrollButton(string glyph, int direction)
    {
        var b = new Button
        {
            Content = glyph,
            Width = 20,
            Height = 18,
            FontSize = 9,
            Padding = new Thickness(0),
            Margin = new Thickness(1, 0, 1, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            // 按钮自己处理点击，不让它冒泡到别处
            Focusable = false,
        };
        b.Click += (_, _) =>
        {
            _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + direction * 48);
            _log($"  [滚动按钮] {(direction < 0 ? "▲" : "▼")} → 偏移 {_scroll.VerticalOffset:0}");
        };
        return b;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        IntPtr exStyle = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE,
            (IntPtr)(exStyle.ToInt64() | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));

        // 拦窗口消息：命中测试（穿透）、滚轮、激活（双保险）
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

        int none = unchecked((int)DWMWA_COLOR_NONE);
        DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref none, sizeof(int));

        int round = DWMWCP_ROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

        int dark = 1;
        DwmSetWindowAttribute(_hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        _log($"  ★ 条子扩展样式: NOACTIVATE="
             + $"{(GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_NOACTIVATE) != 0}");
    }

    /// <summary>
    /// 窗口消息钩子。
    ///
    /// 三个消息各有用途：
    ///   WM_NCHITTEST   —— 收起态主动说"这次点击不是给我的"，转给下面的窗口（穿透）
    ///   WM_MOUSEACTIVATE —— 万一系统想激活我们，明确回答"别激活"
    ///   WM_MOUSEWHEEL  —— 滚轮。**很可能收不到**，收到了就是赚的
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                if (_clickThrough)
                {
                    handled = true;
                    return new IntPtr(HTTRANSPARENT);
                }
                break;

            case WM_MOUSEACTIVATE:
                // 不抢焦点。DO NOT activate。
                handled = true;
                return new IntPtr(MA_NOACTIVATE);

            case WM_MOUSEWHEEL:
                int delta = unchecked((short)((long)wParam >> 16));
                _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - delta / 120.0 * 48);
                _log($"  [滚轮] ★ 竟然收到了！delta={delta} → 偏移 {_scroll.VerticalOffset:0}");
                handled = true;
                return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    // ── 几何 ────────────────────────────────────────────────────────

    /// <summary>命中矩形（物理像素）—— 给轮询线程用</summary>
    public RectPx HitRect => _expanded
        ? Geometry.ExpandedHitRect(_anchorRightPx, _anchorTopPx, _scale)
        : Geometry.CollapsedHitRect(_anchorRightPx, _anchorTopPx, _scale);

    public bool IsShown => _hwnd != IntPtr.Zero && IsWindowVisible(_hwnd);

    public bool IsExpanded => _expanded;

    /// <summary>收起态窗口的边界（物理像素）—— 日志用</summary>
    public string BoundsText
    {
        get
        {
            if (_hwnd == IntPtr.Zero) return "(还没建窗口)";
            if (!GetWindowRect(_hwnd, out RECT r)) return "(拿不到)";
            return $"({r.Left},{r.Top}) {r.Width}×{r.Height}px";
        }
    }

    /// <summary>重算锚点：右上角 = 工作区右边 − 边距、上边 + 边距</summary>
    private void RefreshAnchor()
    {
        GetCursorPos(out POINT cursor);
        IntPtr monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _);
        _scale = dpiX > 0 ? dpiX / 96.0 : 1.0;

        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(monitor, ref info))
        {
            _anchorRightPx = info.rcWork.Right - Geometry.DipToPx(Geometry.AnchorMarginRight, _scale);
            _anchorTopPx = info.rcWork.Top + Geometry.DipToPx(Geometry.AnchorMarginTop, _scale);
        }
        else
        {
            // 拿不到显示器信息就退回主屏 —— 总比不显示强
            _anchorRightPx = (int)SystemParameters.PrimaryScreenWidth
                             - Geometry.DipToPx(Geometry.AnchorMarginRight, _scale);
            _anchorTopPx = Geometry.DipToPx(Geometry.AnchorMarginTop, _scale);
        }
    }

    /// <summary>
    /// 摆窗口。**只用 SetWindowPos（物理像素）**，然后按实际结果回写 DIP 属性。
    ///
    /// 为什么要回读实际尺寸：WPF/Windows 有窗口最小尺寸地板，
    /// 我们要求的高度可能被顶大。回读之后才知道真实发生了什么，
    /// 也才能让 WPF 的布局跟现实一致（否则内容按错误尺寸排版）。
    /// </summary>
    private void ApplyBounds(int xPx, int yPx, int wPx, int hPx, string what)
    {
        // 3 次重摆的保险，抄 S0 —— 给"从没显示过的窗口"用：
        // WPF 首帧布局可能把系统摆的位置覆盖掉。
        for (int attempt = 0; attempt < 3; attempt++)
        {
            SetWindowPos(_hwnd, HWND_TOPMOST, xPx, yPx, wPx, hPx,
                         SWP_NOACTIVATE | SWP_SHOWWINDOW);

            Application.Current.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);

            if (GetWindowRect(_hwnd, out RECT r)
                && Math.Abs(r.Left - xPx) <= 4 && Math.Abs(r.Top - yPx) <= 4
                && Math.Abs(r.Width - wPx) <= 2 && Math.Abs(r.Height - hPx) <= 2)
            {
                break;
            }
            System.Threading.Thread.Sleep(60);
        }

        // 回读真实尺寸，回写 DIP 属性
        if (GetWindowRect(_hwnd, out RECT actual))
        {
            double dipW = actual.Width / _scale;
            double dipH = actual.Height / _scale;
            if (Math.Abs(Width - dipW) > 0.01) Width = dipW;
            if (Math.Abs(Height - dipH) > 0.01) Height = dipH;

            bool floorBit = actual.Width > wPx + 2 || actual.Height > hPx + 2;
            if (floorBit)
            {
                _log($"  ★ {what}：窗口被最小尺寸地板顶大了！"
                     + $"要求 {wPx}×{hPx}px，实际 {actual.Width}×{actual.Height}px");
            }
        }
    }

    /// <summary>
    /// 把窗口裁成只显示条子那一块。
    ///
    /// ★ 实测结论：**这里其实用不上，留着当保险。**
    ///
    ///   我原本担心 WPF/Windows 的窗口最小尺寸地板（约 38px）会把 1mm 的窗口顶大，
    ///   所以准备用 SetWindowRgn 把多出来的部分裁掉。
    ///   但第一次实跑打印出来是：
    ///
    ///       条子已摆出：物理 (2491,15) 57×6px
    ///
    ///   —— **6px，一点没被顶大**。SetWindowPos 带 SWP_NOACTIVATE 时
    ///   并不受 WM_GETMINMAXINFO 的 min track size 约束。
    ///
    ///   所以窗口本身就是那条细线，没有多余面积要裁。
    ///   这个函数保留着，是因为一旦将来窗口被迫变大（改样式、换 DPI），
    ///   ApplyBounds 里的地板检测会打日志，那时它就是现成的解法。
    /// </summary>
    private void ClipToStrip(int widthPx, int heightPx)
    {
        IntPtr rgn = CreateRectRgn(0, 0, widthPx, heightPx);
        if (rgn == IntPtr.Zero)
        {
            _log("  ★ CreateRectRgn 失败，条子会露出一块底色");
            return;
        }

        // SetWindowRgn 成功后 region 归系统所有，不能自己 DeleteObject
        if (SetWindowRgn(_hwnd, rgn, true) == 0)
        {
            DeleteObject(rgn);
            _log("  ★ SetWindowRgn 失败，条子会露出一块底色");
        }
    }

    /// <summary>取消裁剪，恢复整窗可见</summary>
    private void ClearClip()
    {
        SetWindowRgn(_hwnd, IntPtr.Zero, true);
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);

    // ── 预热 ────────────────────────────────────────────────────────

    /// <summary>
    /// 启动时走一遍真实的显示流程再藏起来。
    ///
    /// ★ 这里**必须**用 WPF 的 Show()。
    ///   EnsureHandle() 只建了 HWND，WPF 的 Window 从没显示过 ——
    ///   视觉树没 Measure/Arrange，渲染管线没接管。
    ///   那时用 SetWindowPos 显示出来只会得到**一个纯色空框**。
    ///   （S0 为这件事白折腾了两轮，注释留在 PanelWindow.cs 里。）
    /// </summary>
    public void Prewarm()
    {
        RefreshAnchor();

        int w = Geometry.DipToPx(Geometry.StripWidthDip, _scale);
        int h = Geometry.DipToPx(Geometry.StripHeightDip, _scale);

        // 先挪到屏幕外，别让人看见预热这一下
        SetWindowPos(_hwnd, HWND_TOPMOST, -32000, -32000, w, h,
                     SWP_NOACTIVATE | SWP_SHOWWINDOW);

        FillList();

        Show();                    // ★ 走一遍 WPF 的显示流程，不能省
        UpdateLayout();
        Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);

        SetWindowPos(_hwnd, HWND_TOPMOST, -32000, -32000, w, h, SWP_NOACTIVATE);
        ShowWindow(_hwnd, SW_HIDE);
    }

    // ── 收起 / 展开 ─────────────────────────────────────────────────

    /// <summary>显示成"收起态"：一条细线，鼠标穿透</summary>
    public void ShowCollapsed()
    {
        RefreshAnchor();

        _expanded = false;
        _panel.Visibility = Visibility.Collapsed;

        int wPx = Geometry.DipToPx(Geometry.StripWidthDip, _scale);
        int hPx = Geometry.DipToPx(Geometry.StripHeightDip, _scale);
        int xPx = _anchorRightPx - wPx;
        int yPx = _anchorTopPx;

        ClearClip();                       // 先清掉旧 region，避免尺寸变化后被裁错
        ApplyBounds(xPx, yPx, wPx, hPx, "收起");

        // 把可能被地板顶大的部分裁掉，只留那 1mm
        ClipToStrip(wPx, hPx);

        SetClickThrough(true);

        _log($"  [条子] 收起 → 物理({xPx},{yPx}) {wPx}×{hPx}px "
             + $"缩放{_scale:0.##}× 实际={BoundsText} 穿透={_clickThrough}");
    }

    /// <summary>显示成"展开态"：黄金比面板，可点击</summary>
    public void ShowExpanded()
    {
        _expanded = true;
        FillList();

        int wPx = Geometry.DipToPx(Geometry.PanelWidthDip, _scale);
        int hPx = Geometry.DipToPx(Geometry.PanelHeightDip, _scale);
        int xPx = _anchorRightPx - wPx;
        int yPx = _anchorTopPx;

        ClearClip();

        // ★ 先把穿透关掉再显示 —— 反过来的话第一下点击会漏给下面的窗口
        SetClickThrough(false);

        _panel.Visibility = Visibility.Visible;
        _panel.Opacity = 1;

        ApplyBounds(xPx, yPx, wPx, hPx, "展开");
        UpdateLayout();

        // 内容淡入 150ms，避免"啪"地一下出现
        _panel.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(150),
        });

        _log($"  [面板] 展开 → 物理({xPx},{yPx}) {wPx}×{hPx}px "
             + $"缩放{_scale:0.##}× 实际={BoundsText} 条目={_history.Items.Count}");
    }

    public void HideAll()
    {
        if (_hwnd == IntPtr.Zero) return;
        SetClickThrough(true);
        ShowWindow(_hwnd, SW_HIDE);
    }

    /// <summary>
    /// 鼠标穿透开关。
    ///
    /// 双管齐下：
    ///   ① WS_EX_TRANSPARENT 样式位（简单，但单独用有效果不确定的历史）
    ///   ② WndProc 里 WM_NCHITTEST → HTTRANSPARENT（文档明确，可靠性高）
    ///
    /// 两条都上，窗口实际用的是哪条我们分不清，但**只要有一条生效就够了**。
    /// 日志里把两个状态都打出来，将来定位问题有据可查。
    /// </summary>
    private void SetClickThrough(bool on)
    {
        if (_hwnd == IntPtr.Zero) return;
        if (_clickThrough == on) return;

        IntPtr ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        long v = ex.ToInt64();
        if (on) v |= WS_EX_TRANSPARENT;
        else v &= ~(long)WS_EX_TRANSPARENT;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (IntPtr)v);

        _clickThrough = on;
    }

    // ── 内容 ────────────────────────────────────────────────────────

    /// <summary>
    /// 填历史列表。每次展开都重新填。
    ///
    /// 宽度必须**定死**（S0 的教训）：TextBlock 在容器里拿不到宽度约束时，
    /// 长文本会把布局撑宽，最后表现为"文字跑到窗口外面"。
    /// 这里用 TextWrapping + ClipToBounds 双保险。
    /// </summary>
    public void FillList()
    {
        _list.Children.Clear();

        var items = _history.Items;
        if (items.Count == 0)
        {
            _title.Text = "粘贴板";
            _list.Children.Add(new TextBlock
            {
                Text = "（还没有历史 —— 先在别的窗口复制点什么）",
                Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0)),
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4),
            });
            return;
        }

        _title.Text = $"粘贴板 · {items.Count} 条";

        for (int i = 0; i < items.Count; i++)
        {
            _list.Children.Add(MakeRow(i + 1, items[i]));
        }
    }

    private Border MakeRow(int ordinal, ClipboardHistory.Entry entry)
    {
        string preview = entry.Text.Replace("\r", " ").Replace("\n", " ");
        if (preview.Length > 80) preview = preview[..80] + "…";

        var stack = new StackPanel();

        stack.Children.Add(new TextBlock
        {
            Text = preview,
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 12,
            LineHeight = 16,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 34,          // 最多两行，再长就截断
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        stack.Children.Add(new TextBlock
        {
            Text = $"{ordinal}.  {entry.At:HH:mm:ss}",
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)),
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 10,
            Margin = new Thickness(0, 3, 0, 0),
        });

        var row = new Border
        {
            Background = new SolidColorBrush(RowBg),
            BorderBrush = new SolidColorBrush(RowBorder),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 3),
            Cursor = Cursors.Hand,
            ClipToBounds = true,
            Child = stack,
            Tag = entry.Text,
            ToolTip = preview,
        };

        row.MouseEnter += (_, _) => row.Background = new SolidColorBrush(RowHover);
        row.MouseLeave += (_, _) => row.Background = new SolidColorBrush(RowBg);
        row.MouseLeftButtonDown += OnRowClicked;

        return row;
    }

    private void OnRowClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: string text }) return;
        _log($"  [点击] 「{(text.Length > 30 ? text[..30] + "…" : text)}」");
        ItemActivated?.Invoke(text);
    }

    /// <summary>
    /// 把前 N 条的内容拼成一行，给"顶置前 / 顶置后"对比用。
    ///
    /// 这是「点过的条目应该变成最新」这条需求的硬证据：
    /// 两行一对比就能看出被点的那条有没有跑到第一位，
    /// 不用去数窗口上的小字。
    /// </summary>
    public string DescribeTop(int count)
    {
        var items = _history.Items;
        if (items.Count == 0) return "列表：空的";

        var sb = new System.Text.StringBuilder("列表：");
        int n = Math.Min(count, items.Count);
        for (int i = 0; i < n; i++)
        {
            string t = items[i].Text.Replace("\r", " ").Replace("\n", " ");
            if (t.Length > 12) t = t[..12] + "…";
            sb.Append($" [{i + 1}]{t}");
        }
        if (items.Count > n) sb.Append($" …共{items.Count}条");
        return sb.ToString();
    }
}
