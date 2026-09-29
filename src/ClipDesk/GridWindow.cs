using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using static ClipDesk.NativeMethods;

namespace ClipDesk;

/// <summary>
/// 九宫格 —— 方向手势菜单。
///
/// 行为规格（见 CLAUDE.md 第 11 节「C. 九宫格」）：
///   1. 按住 Alt+V → 在**鼠标当前位置**弹出（鼠标在哪就贴着哪，不跳到屏幕正中）
///   2. 鼠标指针隐藏，进入选择模式
///   3. 以**按下那一刻的鼠标位置**为原点，鼠标朝哪动，哪个方位的格子亮起
///   4. 松开 Alt → 粘贴选中格
///   5. 鼠标没动（中心死区）→ 取消
///
/// 关于"原点"的一个重要修正（来自实测反馈）：
///   最初想用"光标永久锁在中心"，但那需要 ClipCursor（限制光标活动范围），
///   对多显示器 / 高 DPI 是雷区，而且用户移动太快时光标会卡在边界上。
///   现在的做法是**软原点**：窗口出现在鼠标处，光标照常移动，
///   但方向判定始终以"按下那一刻的位置"为基准。
///   体感上等价（因为光标本来就在九宫格中心），却没有 ClipCursor 那些坑。
/// </summary>
internal sealed class GridWindow : Window
{
    private const int Cols = 3;
    private const int Rows = 3;

    /// <summary>格子边长（DIP）。窗口整体尺寸 = 3 × 这个值。</summary>
    private const double CellSize = 160;

    /// <summary>格子之间的缝（DIP）</summary>
    private const double Gap = 3;

    // ★★ 格子本身必须是**完全透明**的。这是"白玻璃"的第五个、也是最上面的来源。
    //
    //   这一行以前是 `FromArgb(0x1E, 0xFF, 0xFF, 0xFF)` —— **12% 不透明的纯白**，
    //   画在每一格玻璃**上面**。九格全铺一层 12% 白膜，
    //   加上 25% 的白色 1px 边框（`NormalBorder`），
    //   整个界面就成了一块**奶白色磨砂玻璃** ——
    //   用户说「不都是一块白色透明玻璃」，指的就是这两层。
    //
    //   参考项目（liquid-glass-react）的元素 background 是 **transparent**：
    //   它一个白色图层都没有，可读性靠**深色**文字阴影撑
    //   （`text-shadow: 0 2px 12px rgba(0,0,0,0.4)`，见下面 label 的 DropShadowEffect）。
    //
    //   ★ 教训：我连着改了四轮"去白雾"（scrim / 内发光 / 每像素增亮 / 粗白边），
    //     唯独漏了这一层 —— 因为它不在材质代码里，它在**布局代码**里，
    //     我当时只在看 GlassParams 和 GlassChrome。
    //     找"哪一层是白的"，得把**整棵视觉树**从上到下过一遍。
    private static readonly Color NormalBg = Color.FromArgb(0x00, 0x00, 0x00, 0x00);

    /// <summary>边框：只留极淡的一条，够看出格子边界就行。</summary>
    private static readonly Color NormalBorder = Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF);

    /// <summary>
    /// 高亮格：极淡的一层底 + **白框**。
    ///
    /// ★ 用户第六轮的口头修正：「选中加白边……以白边为标准」。
    ///   这里原来是蓝边 `0x7AC8FF`。面板那边的选中色也同步改成了白
    ///   （StripPanelWindow.SelectedEdge）—— 两处保持一致。
    /// </summary>
    private static readonly Color ActiveBg = Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF);
    private static readonly Color ActiveBorder = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

    private readonly ClipboardHistory _history;
    private readonly Action<string> _log;
    private readonly GlassParams _glass;

    private readonly Border[,] _grid = new Border[Rows, Cols];
    private readonly TextBlock[,] _labels = new TextBlock[Rows, Cols];
    private readonly string?[,] _content = new string?[Rows, Cols];

    /// <summary>
    /// 九块玻璃的**显示层 + 渲染器** —— 一格一个。
    ///
    /// ★ 这里以前是 `GlassSurface? _surface`（一个实例服务整扇窗口），
    ///   结果是"一个圆角大矩形铺满 720×720" = 用户说的
    ///   「一个大底面板包裹住九个小面板」。
    ///   现在 3×3 各一个，每格抓自己那块的屏、算自己的折射、画自己的圆角。
    /// </summary>
    private GlassSurface[,]? _cells;

    /// <summary>格子的根节点（玻璃层盖在它下面）</summary>
    private Grid? _cellRoot;

    public event Action<string>? CellActivated;

    private IntPtr _hwnd;
    private string _lastShown = "(还没显示过)";

    /// <summary>最近一次玻璃渲染的耗时报告，给日志用</summary>
    public string GlassReport { get; private set; } = "(还没渲染过)";

    /// <summary>
    /// 原点（物理像素）—— 按下 Alt+V 那一刻的鼠标位置，方向判定全程以它为基准。
    ///
    /// ★ 固定不动。方向判定看的永远是"鼠标相对**按下那一点**的位移"，
    ///   不随鼠标移动而漂移。
    /// </summary>
    private int _originX;
    private int _originY;

    /// <summary>
    /// 九宫格窗口**实际的**屏幕左上角（物理像素）。
    ///
    /// ★ 和 <see cref="_originX"/>/<see cref="_originY"/> 不是一回事：
    ///   那是"按下时光标位置"（= 九宫格中心），而窗口贴边时会被钳位，
    ///   两者就差出半个窗口。高光跟鼠标要用**这个**。
    /// </summary>
    private int _windowX, _windowY, _windowSide;

    private int _activeIndex = -1;

    /// <summary>格子边长（物理像素）—— 显示时按 DPI 算</summary>
    private int _cellPx;

    /// <summary>最近一次显示时的玻璃矩形（物理像素）—— 调参重画要用</summary>
    private int _lastX, _lastY, _lastW, _lastH;

    public GridWindow(ClipboardHistory history, Action<string> log, GlassParams glass)
    {
        _history = history;
        _log = log;
        _glass = glass;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;

        // ★★★ 真透明 —— 这是第三轮返工的核心修正 ★★★
        //
        //   前三轮写的是 `AllowsTransparency = false`，因为旧文档里有条硬约束
        //   说"亚克力窗口不能开它"。**那条结论在真机上是错的**，
        //   代价是用户连续三轮看到"黑底"：
        //   `AllowsTransparency = false` 的窗口，客户区背后有一块**不透明的
        //   redirection surface**，内容没画到的地方渲染成黑。
        //
        //   现在窗口是真的透明窗口，玻璃层自己画折射位图。
        AllowsTransparency = true;

        // 自己不再画任何不透明底。这块颜色只作为最后兜底，而且带 alpha。
        Background = new SolidColorBrush(Color.FromArgb(0x00, 0x14, 0x14, 0x18));

        Width = CellSize * Cols;
        Height = CellSize * Rows;

        Content = BuildContent();
        SourceInitialized += OnSourceInitialized;
    }

    // ── 构建 ────────────────────────────────────────────────────────

    private UIElement BuildContent()
    {
        var outer = new Grid();
        _cellRoot = new Grid();

        for (int r = 0; r < Rows; r++)
        {
            _cellRoot.RowDefinitions.Add(new RowDefinition());
            _cellRoot.ColumnDefinitions.Add(new ColumnDefinition());
        }

        // 格子内容宽度 = 格子边长 - 两边缝 - 边框 - 内边距
        double textWidth = CellSize - 2 * Gap - 2 * 1 - 2 * 8;

        for (int r = 0; r < Rows; r++)
        {
            for (int c = 0; c < Cols; c++)
            {
                // ★ 定宽、定高、不换行溢出 —— 三件套缺一不可。
                //
                //   之前"第一列文字消失"的根因就在这里：
                //   TextBlock 在 Grid 单元里**拿不到宽度约束**，
                //   长文本会把所在列撑宽，整个 Grid 超出窗口宽度，
                //   左边那列就被挤到可视区外（或渲染时被裁掉）。
                // ★ 白字 + **深色阴影**。
                //
                //   去掉了白色 scrim 之后，文字是直接压在桌面上的 ——
                //   浅色壁纸上白字会看不清。
                //   参考项目（liquid-glass-react）用的是
                //   `text-shadow: 0 2px 12px rgba(0,0,0,0.4)`，
                //   即**深色阴影**，不是白色底。
                //   ShadowDepth=0 → 是描边式的晕影，不是投影。
                // 中心是取消区：不创建视觉元素，也不创建 GlassSurface。
                if (r == 1 && c == 1) continue;

                var label = new TextBlock
                {
                    Foreground = Brushes.White,
                    FontSize = 13,
                    LineHeight = 19,
                    Width = textWidth,
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(8, 6, 8, 6),
                    Effect = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        Color = Colors.Black,
                        BlurRadius = 6,
                        ShadowDepth = 0,
                        Opacity = 0.85,
                    },
                };

                var border = new Border
                {
                    Margin = new Thickness(Gap),
                    Background = new SolidColorBrush(NormalBg),
                    BorderBrush = new SolidColorBrush(NormalBorder),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    ClipToBounds = true,          // ★ 内容绝不越界
                    Child = label,
                };

                Grid.SetRow(border, r);
                Grid.SetColumn(border, c);
                // ★ 只把格子放进**内容层**（_cellRoot）。
                //   玻璃层是另一棵独立的树，按同样的行列摆一次 ——
                //   这样缝里没有任何图层，露出原样的桌面。
                _cellRoot.Children.Add(border);

                _grid[r, c] = border;
                _labels[r, c] = label;
            }
        }

        // ★★★ 九块**各自独立**的液态玻璃 ═══════════════════════════════
        //
        //   之前是**一整块**：GlassSurface 抓整窗 720×720 的屏，
        //   GlassChrome 把它当成一个圆角矩形画满整窗，
        //   用户看到的就成了「一个大底板包住九个小格子」。
        //
        //   现在每一格建**自己**的 GlassSurface：
        //     自己的抓屏矩形 → 自己的降采样位图 → 自己的折射 → 自己的圆角。
        //   九个实例摆成 3×3，和格子严丝合缝，**缝里什么都不画**。
        //
        //   中心格（1,1）也建 —— 它同样是玻璃，只是没有文字。
        _cells = new GlassSurface[Rows, Cols];

        double cellDip = CellSize - 2 * Gap;          // 每格可见边长（DIP）

        for (int r = 0; r < Rows; r++)
        {
            for (int c = 0; c < Cols; c++)
            {
                // 中心位置是取消区，不能访问空的玻璃实例。
                if (r == 1 && c == 1) continue;

                var surface = new GlassSurface(_glass);
                surface.BindWindow(cellDip, cellDip);

                var layer = surface.Chrome!.Root;
                layer.HorizontalAlignment = HorizontalAlignment.Left;
                layer.VerticalAlignment = VerticalAlignment.Top;
                layer.Width = cellDip;
                layer.Height = cellDip;
                // 按 WPF 的布局规则摆到和对应格子同一个位置
                // （Grid 单元 + Margin(Gap) —— 和 Border 用的是同一套定位）
                Grid.SetRow(layer, r);
                Grid.SetColumn(layer, c);
                layer.Margin = new Thickness(Gap);
                layer.IsHitTestVisible = false;

                _cellRoot.Children.Insert(0, layer);   // 插到最底下

                _cells[r, c] = surface;
            }
        }

        outer.Children.Add(_cellRoot);

        return outer;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        IntPtr exStyle = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE,
            (IntPtr)(exStyle.ToInt64() | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));

        // ★ 让客户区走 DirectComposition 合成，不要 GDI redirection surface。
        //   少了这一步，窗口内容背后永远垫着一块不透明位图 —— 那就是"黑底"。
        if (!ApplyNoRedirectionBitmap())
        {
            _log("  ⚠️ WS_EX_NOREDIRECTIONBITMAP 设置失败，"
                 + "背景可能仍被 redirection surface 遮挡");
        }

        int none = unchecked((int)DWMWA_COLOR_NONE);
        DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref none, sizeof(int));

        // ★ 圆角由 WPF 的矢量裁剪来画（GlassChrome 里做），
        //   所以这里必须关掉 DWM 的圆角 —— 两个圆角叠在一起半径不一致，
        //   角上会露出一圈难看的缝。
        int round = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

        int dark = 1;
        DwmSetWindowAttribute(_hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        _log($"  ★ 九宫格扩展样式: NOACTIVATE="
             + $"{(GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_NOACTIVATE) != 0}"
             + $" / 真透明=True / 折射管线=自绘");
    }

    /// <summary>
    /// 给窗口加上 <c>WS_EX_NOREDIRECTIONBITMAP</c>。
    ///
    /// ★ 这是"真透明"的关键一步，前三轮一直缺它。
    ///
    ///   普通窗口的客户区会先画到一块**不透明的** GDI redirection surface 上，
    ///   再由 DWM 合成。于是"内容没画到的地方"= 那块 surface 的本色 = **黑**。
    ///   九宫格是 720×720 的大窗口、格子之间有 3px 缝、四角还有圆角 ——
    ///   这些地方原来全是那个黑。
    ///
    ///   加上这个样式之后，客户端不再经过 redirection surface，
    ///   由 DirectComposition 直接合成，才能真正透出后面的桌面。
    /// </summary>
    private bool ApplyNoRedirectionBitmap()
    {
        if (_hwnd == IntPtr.Zero) return false;

        IntPtr ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        long value = ex.ToInt64() | WS_EX_NOREDIRECTIONBITMAP;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (IntPtr)value);

        long after = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        return (after & WS_EX_NOREDIRECTIONBITMAP) != 0;
    }

    // ── 预热 / 显隐 ─────────────────────────────────────────────────

    public bool IsShown => _hwnd != IntPtr.Zero && IsWindowVisibleNative(_hwnd);

    public string LastShownPosition => _lastShown;

    public int FilledCellCount { get; private set; }

    /// <summary>一格的边长（DIP）与缝宽（DIP）—— 给"格子 vs 缝"的对比诊断用。</summary>
    public static (double CellDip, double GapDip) CellMetrics => (CellSize, Gap);

    public int RenderedCellCount => GridSelection.SelectableCount;

    public string ActiveCellLabel =>
        _activeIndex < 0 ? "（中心，松手取消）"
                         : GridSelection.Label(_activeIndex) + " "
                           + GridSelection.DirectionName(_activeIndex);

    /// <summary>光标有没有被藏起来 —— 外部日志用它确认</summary>
    public bool CursorHidden => CursorHider.IsHidden;

    /// <summary>
    /// ★ 给自测用的：把九宫格摆到屏幕正中并**保持显示**，返回它的矩形（物理像素）。
    ///
    /// 为什么需要它：用户连报四轮"黑底"，我前三轮都在看日志而不是看画面。
    /// 这个方法让程序自己把窗口摆出来，好让自检**真的截一张图**下来核对
    /// （"格子底下能不能看到桌面"），而不是靠日志里的亮度数字推断。
    ///
    /// 调用方记得调 <see cref="HideGrid"/> 收回去。
    /// </summary>
    public (int X, int Y, int W, int H) ShowForSelfCheck()
    {
        GetMonitorInfoOfPrimary(out int workW, out int workH);

        _cellPx = (int)Math.Round(CellSize * 1.0);
        int sidePx = _cellPx * Cols;

        int xPx = Math.Max(0, workW / 2 - sidePx / 2);
        int yPx = Math.Max(0, workH / 2 - sidePx / 2);

        FillCells();
        ClearActive();

        // ★ 调布局（尺寸变了）→ 抓屏渲染 → 才显示。
        //   顺序不能反：窗口一旦可见，抓到的就是"玻璃盖在自己身上"。
        UpdateLayout();
        RenderGlass(xPx, yPx, sidePx, sidePx);

        SetWindowPos(_hwnd, HWND_TOPMOST, xPx, yPx, sidePx, sidePx,
                     SWP_NOACTIVATE | SWP_SHOWWINDOW);

        // 等一帧真正画出来，否则截到的是空白
        Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        System.Threading.Thread.Sleep(400);

        return (xPx, yPx, sidePx, sidePx);
    }

    private static void GetMonitorInfoOfPrimary(out int width, out int height)
    {
        width = GetSystemMetrics(0);
        height = GetSystemMetrics(1);
    }

    public void Prewarm()
    {
        SetWindowPos(_hwnd, HWND_TOPMOST, -32000, -32000, 0, 0,
                     SWP_NOACTIVATE | SWP_NOSIZE | SWP_SHOWWINDOW);
        FillCells();

        // ★ 预热也要把九块玻璃渲染一遍。
        //   只 Show() 不渲染的话，正式弹出时是**第一帧**才建位的 ——
        //   那一下会把九次抓屏 + 九张位图的首次开销全压在弹出瞬间。
        //   预热的意义就是把这笔一次性的钱先花掉。
        double scale = 1.0;
        int preCell = (int)Math.Round(CellSize * scale);
        int preSide = preCell * Cols;
        RenderGlass(-32000, -32000, preSide, preSide);

        Show();
        UpdateLayout();
        Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        SetWindowPos(_hwnd, HWND_TOPMOST, -32000, -32000, 0, 0,
                     SWP_NOACTIVATE | SWP_NOSIZE);
        ShowWindow(_hwnd, SW_HIDE);
    }

    /// <summary>
    /// 在**鼠标当前位置**弹出 —— 光标正好落在九宫格正中那一格。
    ///
    /// 关于位置的两次反复（别再改了，这里是结论）：
    ///   中间试过"固定在屏幕正中"，两个问题：
    ///     1. 手感 —— 九宫格要脱离光标出现，眼睛得先找到它
    ///     2. ★ **鼠标又藏不住了**：见下
    ///
    /// ★ 位置和「光标藏不藏得住」是有因果的：
    ///   SetSystemCursor 把系统箭头换成透明，但**别的程序在光标进入自己窗口时
    ///   会用自己的光标覆盖它**（记事本里是 I 形，浏览器里是手形）。
    ///   窗口贴着光标 → 光标底下永远是我们自己的九宫格 → 我们是最后说话的人。
    ///   窗口挪到屏幕正中 → 光标常常落在**别的窗口**上 → 那个程序说了算 → 指针重现。
    ///   所以"跟着鼠标走"不只是手感好，它是**光标能藏住的前提**。
    ///
    /// 原点永远是"按下那一刻的光标位置"，与窗口画在哪无关 —— 挪窗口不影响准头。
    /// </summary>
    public void ShowAtCursor()
    {
        GetCursorPos(out POINT cursor);
        IntPtr monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _);
        double scale = dpiX > 0 ? dpiX / 96.0 : 1.0;

        _cellPx = (int)Math.Round(CellSize * scale);
        int sidePx = _cellPx * Cols;
        // 窗口左上角 = 光标位置减去中心那一格的一半 → 光标正好落在中心格正中
        int xPx = cursor.X - sidePx / 2;
        int yPx = cursor.Y - sidePx / 2;

        // 贴边时往回钳一下，别让九宫格跑出屏幕
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(monitor, ref info))
        {
            xPx = Math.Max(info.rcWork.Left, Math.Min(xPx, info.rcWork.Right - sidePx));
            yPx = Math.Max(info.rcWork.Top, Math.Min(yPx, info.rcWork.Bottom - sidePx));
        }

        // 同步 DIP 尺寸给 WPF
        double sideDip = sidePx / scale;
        if (Math.Abs(Width - sideDip) > 1)
        {
            Width = sideDip;
            Height = sideDip;
        }

        FillCells();
        ClearActive();

        UpdateLayout();

        // ★ 顺序铁律：抓屏渲染 → 再显示。
        //   窗口一旦可见，抓到的就是玻璃盖在自己身上的画面，越叠越脏。
        RenderGlass(xPx, yPx, sidePx, sidePx);

        SetWindowPos(_hwnd, HWND_TOPMOST, xPx, yPx, sidePx, sidePx,
                     SWP_NOACTIVATE | SWP_SHOWWINDOW);

        // ★ 原点 = 按下那一刻的光标位置（物理像素）
        //
        // ⚠️ 这是**九宫格中心**（光标落在中心格正中），
        //    **不是窗口左上角** —— 窗口左上角要再减去 sidePx/2。
        //    而且窗口贴边时会被上面那句钳位，实际位置和原点就脱钩了。
        //    所以跟鼠标要用下面记的**实际窗口位置**，不能用 _originX/_originY。
        _originX = cursor.X;
        _originY = cursor.Y;

        // ★★ 记下窗口**实际的**屏幕左上角（物理像素）。
        //    高光跟鼠标要用它 —— 见 UpdateCellHighlights 的注释。
        _windowX = xPx;
        _windowY = yPx;
        _windowSide = sidePx;

        HideCursor();

        _lastShown = $"物理({xPx},{yPx}) 边长{sidePx}px 格子{_cellPx}px 缩放{scale:0.##}× "
                   + $"原点({_originX},{_originY}) 光标={CursorHider.IsHidden}";
    }

    /// <summary>
    /// 渲染**九块**玻璃 —— **必须在窗口显示之前调**。
    ///
    /// 每格抓的是**它自己将要占住的那块屏幕**（`窗口左上角 + 格子偏移`），
    /// 此刻还看得见真正的桌面。窗口一旦可见，抓到的就是玻璃盖在自己身上。
    ///
    /// ★ 为什么必须按格抓、不能整窗抓一次再切九块：
    ///   折射是**按圆角矩形边缘**算的。整窗抓一次的话，位移图只有最外圈
    ///   那一圈有位移，九个格子的"内部边缘"全都没有折射 ——
    ///   看上去就是一块大玻璃上糊了九个方框，而不是九块独立的玻璃。
    /// </summary>
    private void RenderGlass(int xPx, int yPx, int wPx, int hPx)
    {
        _lastX = xPx; _lastY = yPx; _lastW = wPx; _lastH = hPx;

        if (_cells is null) return;

        double scale = (double)wPx / (CellSize * Cols);
        int cellPx = (int)Math.Round(CellSize * scale);
        int gapPx = (int)Math.Round(Gap * scale);
        int sidePx = (int)Math.Round((CellSize - 2 * Gap) * scale);

        var sw = new System.Text.StringBuilder();
        int okCount = 0;
        double totalMs = 0;

        for (int r = 0; r < Rows; r++)
        {
            for (int c = 0; c < Cols; c++)
            {
                if (r == 1 && c == 1) continue;

                var surface = _cells[r, c];
                if (surface is null) continue;

                // 这一格在屏幕上的物理矩形
                int cx = xPx + c * cellPx + gapPx;
                int cy = yPx + r * cellPx + gapPx;

                bool ok = surface.Refresh(cx, cy, sidePx, sidePx);
                totalMs += surface.LastRenderMs;
                if (ok) okCount++;
                else sw.Append($" ({r},{c})失败:{surface.LastBreakdown}");
            }
        }

        var sample = _cells[0, 0];
        GlassReport = okCount == GridSelection.SelectableCount
            ? $"★ 八格独立玻璃：圆角 {_glass.CornerRadius:0.#} ｜ 每格 {sidePx}×{sidePx}px 缝 {gapPx}px ｜ "
              + $"共 {totalMs:0.#}ms（{sample!.LastBreakdown}）"
              + $"｜ 模糊{sample.BlurVerdict}："
              + $"细节 {sample.RawGradient:0.#} → {sample.BlurGradient:0.#}"
              + $"｜ ★alpha {sample.AlphaTrace}"
              + $"｜ {sample.Chrome?.LastAttachedReport}"
            : $"❌ 有 {GridSelection.SelectableCount - okCount} 格渲染失败：{sw}（失败的格保留上一帧，不画黑底）";
    }

    /// <summary>调参面板拖动时调用：按最后一次显示的位置重画。</summary>
    public void RefreshGlassForTuning()
    {
        if (_lastW <= 0) return;
        RenderGlass(_lastX, _lastY, _lastW, _lastH);
    }

    public void HideGrid()
    {
        if (_hwnd == IntPtr.Zero) return;

        ShowCursorBack();
        ShowWindow(_hwnd, SW_HIDE);
    }

    // ── 方向选择 ────────────────────────────────────────────────────

    /// <summary>
    /// 由外部 60Hz 轮询调用：读鼠标位置 → 算方位 → 更新高亮。
    /// 只在**选中格变了**时才碰 UI —— 这是 D9 那条省电规矩。
    ///
    /// ★ 就这么简单，不要再往这里加东西。
    ///   前三轮在这里塞过"锁定""路过""逐格播放"，每一次用户都说变差了。
    ///   用户唯一认可的就是这个：鼠标在哪个方位亮哪格，回中心就取消。
    /// </summary>
    public void PollSelection()
    {
        if (!IsShown)
        {
            // 保险：万一窗口已经不在显示状态，光标绝不能还藏着
            if (CursorHider.IsHidden) CursorHider.Restore();
            return;
        }

        GetCursorPos(out POINT p);
        double dx = p.X - _originX;
        double dy = p.Y - _originY;

        // ── ★ 高光跟随鼠标（每格各转各的）──────────────────────────
        //
        //   **这一步不重渲染**，只改九个 GlassChrome 的渐变属性
        //   （见 GlassChrome.UpdateHighlight 的长注释）。
        //   抓屏 + 真高斯那笔钱每格 ~14ms，每帧都付不起 ——
        //   一旦有人把这里改成 RenderGlass，九宫格会直接卡死。
        UpdateCellHighlights(p);

        int index = GridSelection.Resolve(dx, dy);
        if (index == _activeIndex) return;

        SetActive(index);
    }

    /// <summary>
    /// 让每一格的高光都朝"光标在这个格子的哪个方位"转。
    ///
    /// ══ ★ 偏移量的单位是**百分比**，不是 [-1,1] ═════════════════════
    ///
    /// 参考项目（index.tsx:307-310）是这么算的：
    ///
    ///     x: ((e.clientX - centerX) / rect.width)  * 100
    ///     y: ((e.clientY - centerY) / rect.height) * 100
    ///
    /// 除以的是**整个宽度**再乘 100 —— 所以光标贴到元素右边缘时 x = +50，
    /// 范围是 **[-50, 50]**。
    ///
    /// ⚠️ 我第一版按 [-1,1] 归一化（除以半宽），结果
    ///    `angle = 135 + x * 1.2` 只能转 **±1.2 度** —— 肉眼看不出任何变化。
    ///    实测取证：光标放左/右两侧各截一张，像素**逐点相同**（差值全 0）。
    ///    按百分比换算后实际能转 ±60 度，这才看得出来。
    ///
    /// ⚠️ 九宫格是 `SWP_NOACTIVATE` 且光标被藏起来的，
    ///    **拿不到 WM_MOUSEMOVE** —— 只能靠这个 60Hz 轮询，
    ///    不能改成鼠标事件。
    ///
    /// ⚠️ 这个方法**只改 brush 属性，绝不触发重抓屏 / 重模糊**。
    /// </summary>
    private void UpdateCellHighlights(POINT cursor)
    {
        if (_cells is null || _cellPx <= 0) return;

        double cell = _cellPx;

        for (int r = 0; r < Rows; r++)
        {
            for (int c = 0; c < Cols; c++)
            {
                if (r == 1 && c == 1) continue;

                var chrome = _cells[r, c]?.Chrome;
                if (chrome is null) continue;

                // 这一格的**中心**在屏幕上的物理坐标
                //
                // ⚠️ 必须用 **_windowX/_windowY**（窗口实际左上角），
                //    **不能**用 _originX/_originY —— 那是"按下时光标位置"
                //    （= 九宫格中心），窗口贴边被钳位后两者差半个窗口，
                //    算出来的格子中心会整体偏，高光转的方向全是错的。
                double cx = _windowX + c * cell + cell / 2.0;
                double cy = _windowY + r * cell + cell / 2.0;

                // ★ 除以**整格宽**再乘 100 = 参考项目那套百分比坐标（范围 ±50）
                double ox = (cursor.X - cx) / cell * 100.0;
                double oy = (cursor.Y - cy) / cell * 100.0;

                // 钳到 ±50（正好是"光标贴在格子边缘"）
                ox = Math.Clamp(ox, -50.0, 50.0);
                oy = Math.Clamp(oy, -50.0, 50.0);

                chrome.UpdateHighlight(ox, oy);
            }
        }
    }

    public int CommitSelection() => _activeIndex;

    /// <summary>Esc 取消 —— 清掉高亮（和"拖回中心"等价的一条捷径）</summary>
    public void CancelSelection() => ClearActive();

    public string? GetCellContent(int row, int col)
    {
        if (row < 0 || row >= Rows || col < 0 || col >= Cols) return null;
        return _content[row, col];
    }

    /// <summary>
    /// 当前八格**分别对应哪一条历史**，按填充顺序（01→02→12→…→00）排好。
    ///
    /// 这个日志是给「粘贴后顶置」那条需求用的硬证据：
    ///   粘贴前打印一次、粘贴后再打印一次，两行一对比，
    ///   就能直接看出被粘的那条是不是从中间跑到了第一行（01）。
    /// </summary>
    public string DescribeSlots()
    {
        var items = _history.Items;
        var sb = new System.Text.StringBuilder("格子内容：");

        for (int i = 0; i < GridSelection.SelectableCount; i++)
        {
            var (r, c) = GridSelection.ToCell(i);
            string? text = _content[r, c];

            string name = string.IsNullOrEmpty(text)
                ? "（空）"
                : text.Replace("\r", " ").Replace("\n", " ");
            if (name.Length > 10) name = name[..10] + "…";

            sb.Append($" {GridSelection.Label(i)}={name}");
            if (i < GridSelection.SelectableCount - 1) sb.Append(' ');
        }

        sb.Append($"（历史共 {items.Count} 条）");
        return sb.ToString();
    }

    private void SetActive(int index)
    {
        if (_activeIndex >= 0)
        {
            var (pr, pc) = GridSelection.ToCell(_activeIndex);
            PaintCell(pr, pc, active: false);
        }

        _activeIndex = index;

        if (index >= 0)
        {
            var (r, c) = GridSelection.ToCell(index);
            PaintCell(r, c, active: true);

            _log($"  [选择] {GridSelection.Label(index)} {GridSelection.DirectionName(index)}"
                 + $"  → 「{Preview(_content[r, c])}」");
        }
    }

    private void PaintCell(int r, int c, bool active)
    {
        _grid[r, c].Background = new SolidColorBrush(active ? ActiveBg : NormalBg);
        _grid[r, c].BorderBrush = new SolidColorBrush(active ? ActiveBorder : NormalBorder);
        _grid[r, c].BorderThickness = new Thickness(active ? 3 : 1);
    }

    private void ClearActive()
    {
        for (int r = 0; r < Rows; r++)
        {
            for (int c = 0; c < Cols; c++)
            {
                if (r == 1 && c == 1) continue;
                PaintCell(r, c, active: false);
            }
        }

        _activeIndex = -1;
    }

    // ── 光标 ────────────────────────────────────────────────────────

    /// <summary>
    /// 藏光标。真正的实现在 CursorHider —— 那里用 SetSystemCursor。
    /// 这里只是转发 + 记日志。
    /// </summary>
    private void HideCursor()
    {
        if (CursorHider.IsHidden) return;      // 已经是隐藏状态，别重复设
        _log($"  [光标] {CursorHider.Hide()}");
    }

    private void ShowCursorBack()
    {
        if (!CursorHider.IsHidden) return;
        CursorHider.Restore();
        _log("  [光标] 已还原");
    }

    // ── 内容 ────────────────────────────────────────────────────────

    private static string Preview(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "—";
        string one = s.Replace("\r", " ").Replace("\n", " ");
        return one.Length > 24 ? one[..24] + "…" : one;
    }

    /// <summary>
    /// 把最近 8 条按 FillOrder 填进 8 个可选格。中心格永远留空。
    /// 宽度已经在 BuildContent 里定死了，这里只负责填文字。
    /// </summary>
    public void FillCells()
    {
        var items = _history.Items;
        FilledCellCount = 0;

        for (int r = 0; r < Rows; r++)
        {
            for (int c = 0; c < Cols; c++)
            {
                _content[r, c] = null;
                if (r == 1 && c == 1) continue;
                _labels[r, c].Text = "";
                _labels[r, c].Foreground = Brushes.White;
            }
        }

        for (int i = 0; i < GridSelection.SelectableCount && i < items.Count; i++)
        {
            var (r, c) = GridSelection.ToCell(i);
            var entry = items[i];
            _content[r, c] = entry.Text;

            _labels[r, c].Text = $"{GridSelection.Label(i)}   {entry.At:HH:mm:ss}\n{Preview(entry.Text)}";
            _labels[r, c].Foreground = Brushes.White;
            FilledCellCount++;
        }

        _log($"  ★ 填格完成：{FilledCellCount}/8 有内容，历史共 {items.Count} 条");
    }

    private void OnCellClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: string text }) return;
        HideGrid();
        CellActivated?.Invoke(text);
    }

    // ── P/Invoke 补充 ───────────────────────────────────────────────

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private static bool IsWindowVisibleNative(IntPtr hWnd) => IsWindowVisible(hWnd);
}
