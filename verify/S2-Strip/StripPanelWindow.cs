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
    /// <summary>面板底色（深色）。面板里是白字，深底没法避免。</summary>
    private static readonly Color PanelBg = Color.FromRgb(0x1C, 0x1C, 0x1C);

    /// <summary>
    /// 条子的颜色。
    ///
    /// ★ 试了三轮才想明白：**任何单一颜色都会在某个背景上消失。**
    ///   第一轮 0x8A8A8A（中灰）→ 深色壁纸上看不见
    ///   第二轮 0xF0F0F0（近白）→ 浅色壁纸上看不见   ← 用户的原话
    ///
    ///   正解不是换一个"更好的颜色"，而是**让条子自己带对比**：
    ///   外层一圈深色描边 + 中间一条亮色芯。
    ///   于是它在白底上靠深色描边被看见，在黑底上靠亮色芯被看见。
    ///
    ///   这和 Google/YouTube 的"白底上放白色图标"是同一招数 ——
    ///   他们也是靠一圈阴影/描边让白图标在白底上仍然可见。
    /// </summary>
    /// <summary>条子的亮芯颜色。深色描边见 StripEdge。</summary>
    private static readonly Color StripCore = Color.FromRgb(0xE8, 0xE8, 0xE8);   // 亮芯
    private static readonly Color StripEdge = Color.FromRgb(0x18, 0x18, 0x18);   // 深色描边

    private static readonly Color RowBg = Color.FromRgb(0x2E, 0x2E, 0x2E);
    private static readonly Color RowHover = Color.FromRgb(0x42, 0x4A, 0x56);
    private static readonly Color RowBorder = Color.FromRgb(0x3A, 0x3A, 0x3A);
    private static readonly Color Accent = Color.FromRgb(0x8C, 0xD0, 0xFF);
    private static readonly Color TitleBg = Color.FromRgb(0x25, 0x2A, 0x33);
    private static readonly Color TimeFg = Color.FromRgb(0x9A, 0x9A, 0x9A);
    private static readonly Color HintFg = Color.FromRgb(0xB0, 0xB0, 0xB0);

    /// <summary>被点击的那一条：黄底 + 黄边，一眼看出"我选了哪个"。</summary>
    private static readonly Color SelectedBg = Color.FromRgb(0x4A, 0x3E, 0x1A);
    private static readonly Color SelectedEdge = Color.FromRgb(0xFF, 0xC8, 0x3C);

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

    /// <summary>正文行高（DIP）。条目高度固定为它的两倍 —— 见 MakeRow。</summary>
    private const double RowTextHeightDip = 17.0;

    /// <summary>
    /// 点击后黄框保持多久（毫秒）再撤销。
    ///
    /// ★ 用户第三轮的要求：「点击之后是选中了想粘贴的文字，但窗口依然不关闭，
    ///   等到我的鼠标离开后再关闭」——
    ///   这样选错了还能改，不用重新悬停一次再重来。
    /// </summary>
    private const int SelectedHoldMs = 3000;

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

    /// <summary>当前被点亮黄框的那一条（点击后保留一小会儿，方便确认点对了没）</summary>
    private Border? _selectedRow;
    private DateTime _selectedAt;

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

        // ② 条子：深色底 + 中间一条亮芯，靠"自带对比"在任何背景上都看得见。
        //
        // ★ 第一轮"条子长度不对"的根因在这里：
        //   我只设了 HorizontalAlignment = Right，**没设 Width**。
        //   Right 对齐 + 无 Width + 无内容 → 期望宽度是 0，
        //   实际画多宽完全取决于容器怎么摆 —— 于是量出来只有 0.7cm。
        //
        //   这是同一个错误的第三次（文字那次是 TextBlock 没定宽把布局撑坏）。
        //   **规矩：可见元素的宽高一律显式写死，不靠对齐方式推断。**
        //
        //   现在窗口尺寸 == 条子尺寸，外层 Border 直接铺满窗口，
        //   亮芯用 Padding 缩进去 —— 深色部分就成了描边。
        //
        // ★ 关于"描边要多粗"：第一轮两侧各 0.67mm 时用户量出厚度只有 1.5mm
        //   （目标 2mm）—— 但那次条子长度也是 1.5cm（目标 2cm），
        //   **长宽同比例短 25%**，说明那是全局密度偏差，不是描边吃掉了厚度。
        //   所以描边改为只在**上下各留 1 个物理像素**的视觉描边，
        //   剩下的全部给亮芯 —— 厚度全归可见部分。
        double edgePx = 1.0 / _scale;      // 1 物理像素对应的 DIP
        _strip = new Border
        {
            Background = new SolidColorBrush(StripEdge),   // 深色描边
            Padding = new Thickness(0, edgePx, 0, edgePx),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Cursor = Cursors.Hand,
            Child = new Border
            {
                Background = new SolidColorBrush(StripCore),   // 亮芯
            },
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
    /// 一行历史条目。**高度固定为两行**，内容最多两行，超出打省略号。
    ///
    /// ★ 用户第三轮的要求：「把文字的框框放大一倍，一个框框能存储两行文字，
    ///   就算文字只有一行，也得把框框设置为两行的大小。
    ///   如果文字个数超过两行能存储的大小，再用省略号代替。」
    ///
    ///   所以高度是**写死的两行**（RowTextHeightDip × 2），
    ///   不给内容留伸缩余地 —— 列表看起来才整齐，滚轮位移也才好判断。
    ///
    /// 布局：上半是正文（最多两行），下半是「序号 + 时间」一行小字。
    /// </summary>
    private Border MakeRow(int ordinal, ClipboardHistory.Entry entry)
    {
        // 正文用 TextWrapping.Wrap + 固定高度 + TextTrimming：
        //   WPF 的规矩是 —— 给了 Height 就不再自动截断，
        //   所以要在 Height 之外再给一个 MaxHeight 才会出现省略号。
        //   这里 Height = MaxHeight = 两行，两行放不下就出省略号。
        double twoLines = RowTextHeightDip * 2;

        var text = new TextBlock
        {
            Text = entry.Text.Replace("\r", " ").Replace("\n", " "),
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 12,
            LineHeight = RowTextHeightDip,
            TextWrapping = TextWrapping.Wrap,
            Height = twoLines,
            MaxHeight = twoLines,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var time = new TextBlock
        {
            Text = $"{ordinal}.  {entry.At:HH:mm:ss}",
            Foreground = new SolidColorBrush(TimeFg),
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Bottom,
        };

        var stack = new StackPanel();
        stack.Children.Add(text);
        stack.Children.Add(time);

        var row = new Border
        {
            Background = new SolidColorBrush(RowBg),
            BorderBrush = new SolidColorBrush(RowBorder),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 0, 0, 3),
            Cursor = Cursors.Hand,
            ClipToBounds = true,
            Child = stack,
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
        if (sender is not Border { Tag: string text } row) return;

        _log($"  [点击] 「{(text.Length > 30 ? text[..30] + "…" : text)}」");

        // ★ 先把上一条的黄框撤掉，再点亮这一条 —— 保证同时只有一条是"选中的"
        ClearSelection();
        row.Background = new SolidColorBrush(SelectedBg);
        row.BorderBrush = new SolidColorBrush(SelectedEdge);
        row.BorderThickness = new Thickness(0, 0, 0, 2);
        _selectedRow = row;

        // 黄框保持 SelectedHoldMs 后自动撤销 —— 让用户看得出"刚才点的是哪条"
        _selectedAt = DateTime.Now;

        ItemActivated?.Invoke(text);
    }

    /// <summary>撤掉当前的高亮框</summary>
    private void ClearSelection()
    {
        if (_selectedRow is null) return;
        _selectedRow.Background = new SolidColorBrush(RowBg);
        _selectedRow.BorderBrush = new SolidColorBrush(RowBorder);
        _selectedRow.BorderThickness = new Thickness(0, 0, 0, 1);
        _selectedRow = null;
    }

    /// <summary>
    /// 由外面每 tick 调一次：黄框到期了就撤掉。
    /// **不能用 DispatcherTimer** —— 那会一直唤醒渲染管线（D9 的规矩）。
    /// 挂在这个已有的 100ms 心跳上，反正是免费的。
    /// </summary>
    public void TickSelection()
    {
        if (_selectedRow is null) return;
        if ((DateTime.Now - _selectedAt).TotalMilliseconds >= SelectedHoldMs)
        {
            ClearSelection();
            _log("  [选中标记] 已自动撤销");
        }
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
