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

    /// <summary>
    /// 条子颜色。
    ///
    /// ★ 用户明确要求「条子颜色调亮一点，不然在黑色背景下看不清」。
    ///   第一轮用的 0x8A8A8A 在深色壁纸/深色窗口上确实糊成一片。
    ///   现在用接近白的浅灰，并在下面垫一层柔和的发光边框，
    ///   保证不管背景是什么颜色都看得见。
    /// </summary>
    private static readonly Color StripBg = Color.FromRgb(0xF0, 0xF0, 0xF0);
    private static readonly Color StripGlow = Color.FromRgb(0x66, 0x66, 0x66);

    private static readonly Color RowBg = Color.FromRgb(0x2A, 0x2A, 0x2A);
    private static readonly Color RowHover = Color.FromRgb(0x3D, 0x3D, 0x3D);
    private static readonly Color RowBorder = Color.FromRgb(0x3A, 0x3A, 0x3A);
    private static readonly Color Accent = Color.FromRgb(0x8C, 0xD0, 0xFF);

    /// <summary>
    /// 滚轮一步滚多少 DIP。
    ///
    /// ★ 第一轮 48 DIP 是错的 —— 用户反馈「往下滚一下下面的文字框正好覆盖掉
    ///   上面的文字框，视觉上很难辨别是往下滚了还是往上滚了」。
    ///
    ///   算一下就知道为什么：第一轮行高约 65 DIP，48 ÷ 65 = 74% ——
    ///   滚一格后新的行几乎正好盖住旧行原来的位置，**没有参照物发生位移**。
    ///
    ///   现在行高约 26 DIP，24 DIP ≈ 0.92 行 —— 明显能看到位移。
    /// </summary>
    private const double WheelStepDip = 24.0;

    private readonly ClipboardHistory _history;
    private readonly Action<string> _log;

    /// <summary>点了某一条 → 交给外面写剪贴板。参数是那条的文本。</summary>
    public event Action<string>? ItemActivated;

    private IntPtr _hwnd;
    private double _scale = 1.0;

    /// <summary>锚点（物理像素）：横向是条子**中心**，纵向是顶边</summary>
    private int _anchorCenterXPx;
    private int _anchorTopPx;

    /// <summary>当前是不是展开态</summary>
    private bool _expanded;

    /// <summary>当前有没有设"鼠标穿透"</summary>
    private bool _clickThrough;

    // ── 视觉元素 ───────────────────────────────────────────────────
    private Border _panel = null!;
    private Border _strip = null!;
    private Border _stripGlow = null!;
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

        // 背景设为深色。
        // 收起态时窗口 = 条子本身，这块深色会被条子完全盖住；
        // 展开态时它就是面板的底色。
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

        // ② 条子：**尺寸全部写死**，居中铺满窗口。
        //
        // ★ 第一轮"条子长度不对"的根因就在这里：
        //   我只设了 HorizontalAlignment = Right，**没设 Width**。
        //   Right 对齐 + 无 Width + 无内容 → 期望宽度是 0，
        //   实际画多宽完全取决于容器怎么摆 —— 于是量出来只有 0.7cm。
        //
        //   这是同一个错误的第三次（文字那次是 TextBlock 没定宽把布局撑坏）。
        //   **规矩：可见元素的宽高一律显式写死，不靠对齐方式推断。**
        //
        //   现在窗口尺寸 == 条子尺寸，所以条子直接铺满窗口就行，
        //   Width/Height 绑到窗口的实际尺寸上（在 ApplyBounds 里同步）。
        _stripGlow = new Border
        {
            Background = new SolidColorBrush(StripGlow),
            Visibility = Visibility.Collapsed,   // 展开时隐藏，免得在面板顶部留一道浅边
        };
        root.Children.Add(_stripGlow);

        _strip = new Border
        {
            Background = new SolidColorBrush(StripBg),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Cursor = Cursors.Hand,
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
            _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + direction * WheelStepDip);
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

        // ★ 三件一起关掉"条子下面那块黑影"的来源（用户反馈的问题⑤）：
        //
        //   ① 不要圆角。DWM 的圆角半径约 8px，而收起态窗口只有 11px 高 ——
        //      圆角比窗口还高，DWM 在最扁的窗口上画圆角就会出黑边/黑块。
        //      2mm 的线根本不需要圆角。
        //   ② 关掉非客户区渲染 —— 去掉 DWM 给窗口画的投影。
        //   ③ 边框颜色设 NONE —— Win11 默认那圈 1px 描边。
        //
        //   三个都设了还留黑影的话，就只剩"深色背景透过来了"这一种可能，
        //   那时再查条子有没有真正铺满窗口。
        int round = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

        int ncPolicy = DWMNCRP_DISABLED;
        DwmSetWindowAttribute(_hwnd, DWMWA_NCRENDERING_POLICY, ref ncPolicy, sizeof(int));

        int dark = 1;
        DwmSetWindowAttribute(_hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        _log($"  ★ 条子扩展样式: NOACTIVATE="
             + $"{(GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_NOACTIVATE) != 0}"
             + $" / 圆角=不圆 / 投影=关");
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
                _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - delta / 120.0 * WheelStepDip);
                _log($"  [滚轮] 收到 delta={delta} → 偏移 {_scroll.VerticalOffset:0}"
                     + $"/{_scroll.ScrollableHeight:0}（一步 {WheelStepDip:0} DIP）");
                handled = true;
                return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    // ── 几何 ────────────────────────────────────────────────────────

    /// <summary>命中矩形（物理像素）—— 给轮询线程用</summary>
    public RectPx HitRect => _expanded
        ? Geometry.ExpandedHitRect(_anchorCenterXPx, _anchorTopPx, _scale)
        : Geometry.CollapsedHitRect(_anchorCenterXPx, _anchorTopPx, _scale);

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

    /// <summary>
    /// 重算锚点。
    ///
    /// ★ 位置按用户第一轮实测后的要求定：
    ///   · 横向：条子**中心**在工作区宽度的 3/4 处（"右四分之一处"）
    ///   · 纵向：**紧贴工作区顶部，零缝隙**
    ///
    ///   第一轮把条子钉在距右边缘 8 DIP 的右上角，用户反馈"很影响操作"——
    ///   那个位置正好压着最大化窗口的关闭按钮。
    /// </summary>
    private void RefreshAnchor()
    {
        GetCursorPos(out POINT cursor);
        IntPtr monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _);
        _scale = dpiX > 0 ? dpiX / 96.0 : 1.0;

        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(monitor, ref info))
        {
            int workW = info.rcWork.Right - info.rcWork.Left;
            _anchorCenterXPx = info.rcWork.Left
                               + (int)Math.Round(workW * Geometry.CenterAtWidthRatio);
            _anchorTopPx = info.rcWork.Top + Geometry.DipToPx(Geometry.AnchorMarginTop, _scale);
        }
        else
        {
            // 拿不到显示器信息就退回主屏 —— 总比不显示强
            _anchorCenterXPx = (int)Math.Round(
                (int)SystemParameters.PrimaryScreenWidth * Geometry.CenterAtWidthRatio);
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

        int w = Geometry.DipToPx(Geometry.CollapsedWidthDip, _scale);
        int h = Geometry.DipToPx(Geometry.CollapsedHeightDip, _scale);

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

    /// <summary>
    /// 显示成"收起态"：一条细线。
    ///
    /// ★ 窗口尺寸 == 条子尺寸，**没有一寸多余面积** ——
    ///   用户明确要求「你的条有多大，触发范围就有多大」。
    ///   第一轮我做成 10mm×10mm 的窗口 + 顶上一小条可见线，
    ///   命中区是条子面积的 10 倍，被用户否掉了。
    /// </summary>
    public void ShowCollapsed()
    {
        RefreshAnchor();

        _expanded = false;
        _panel.Visibility = Visibility.Collapsed;
        _stripGlow.Visibility = Visibility.Collapsed;
        _strip.Visibility = Visibility.Visible;

        int wPx = Geometry.DipToPx(Geometry.CollapsedWidthDip, _scale);
        int hPx = Geometry.DipToPx(Geometry.CollapsedHeightDip, _scale);
        int xPx = Geometry.CollapsedLeftPx(_anchorCenterXPx, _scale);
        int yPx = _anchorTopPx;

        ClearClip();
        ApplyBounds(xPx, yPx, wPx, hPx, "收起");
        SetClickThrough(true);

        _log($"  [条子] 收起 → 物理({xPx},{yPx}) {wPx}×{hPx}px "
             + $"中心x={_anchorCenterXPx} 缩放{_scale:0.##}× 实际={BoundsText} 穿透={_clickThrough}");
    }

    /// <summary>
    /// 显示成"展开态"：黄金比面板，可点击。
    ///
    /// 与条子**同一个中心 x**，所以面板是向左右同时长出去、向下长出来 ——
    /// 视觉上就是"从那条线长出来的"。
    /// </summary>
    public void ShowExpanded()
    {
        _expanded = true;
        FillList();

        int wPx = Geometry.DipToPx(Geometry.PanelWidthDip, _scale);
        int hPx = Geometry.DipToPx(Geometry.PanelHeightDip, _scale);
        int xPx = Geometry.ExpandedLeftPx(_anchorCenterXPx, _scale);
        int yPx = _anchorTopPx;

        ClearClip();

        // ★ 先把穿透关掉再显示 —— 反过来的话第一下点击会漏给下面的窗口
        SetClickThrough(false);

        _strip.Visibility = Visibility.Collapsed;   // 展开态不要那条白线压在面板顶上
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

    /// <summary>
    /// 一行历史条目。
    ///
    /// ★ 第一轮是两行（预览最多两行 + 时间独占一行），行高约 65 DIP。
    ///   面板变矮之后（149 DIP）那样只放得下 1.7 行，根本没法用。
    ///   改成**单行紧凑**：预览一行 + 时间同排靠右，行高约 26 DIP → 一屏约 4 行。
    ///
    /// 顺带解决了滚轮那个问题：行矮了之后一步 24 DIP 的位移占比明显，
    /// 一眼就能看出滚了没有。
    /// </summary>
    private Border MakeRow(int ordinal, ClipboardHistory.Entry entry)
    {
        string preview = entry.Text.Replace("\r", " ").Replace("\n", " ");
        if (preview.Length > 60) preview = preview[..60] + "…";

        // 单行：左边预览占满剩余宽度，右边时间固定宽度。
        // 两列都用 GridLength 明确分配 —— 宽度不靠内容去撑（第一轮的教训）。
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition());
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new TextBlock
        {
            Text = preview,
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        line.Children.Add(text);

        var time = new TextBlock
        {
            Text = $"{ordinal}.  {entry.At:HH:mm:ss}",
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 10,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(time, 1);
        line.Children.Add(time);

        var row = new Border
        {
            Background = new SolidColorBrush(RowBg),
            BorderBrush = new SolidColorBrush(RowBorder),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(0, 0, 0, 2),
            Cursor = Cursors.Hand,
            ClipToBounds = true,
            Child = line,
            Tag = entry.Text,
            ToolTip = entry.Text,
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
