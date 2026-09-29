using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using static ClipDesk.NativeMethods;

namespace ClipDesk;

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
    /// <summary>
    /// 面板的**半透明**底色（不再是不透明深灰）。
    ///
    /// ★ 第三轮修正：这里原来是不透明的 `#1C1C1C`，配 `AllowsTransparency=false`
    ///   的实心窗口 —— 两者叠起来就是用户说的"黑底"。
    ///   现在窗口是真透明的，这层只是一个带 alpha 的着色，
    ///   桌面会从底下透上来。
    ///
    ///   面板里是白字，所以这层不能太淡，否则文字对比度不够。
    ///   0xB8 ≈ 72% 不透明度，是"既看得见桌面、又读得清文字"的折中。
    /// </summary>
    private static readonly Color PanelBg = Color.FromArgb(0xB8, 0x1C, 0x1C, 0x22);
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
    ///
    /// ★ 上了玻璃材质之后（第五轮）：亮芯改为**半透明**，
    ///   让底下抓来的背景透上来，看上去像一条真的玻璃棱 ——
    ///   而不是一根刷了白漆的塑料条。
    ///   描边也稍微收了透明度，免得在浅色壁纸上黑得像一道划痕。
    /// </summary>
    private static readonly Color StripCore = Color.FromArgb(0x68, 0xE8, 0xE8, 0xE8);   // 亮芯
    private static readonly Color StripEdge = Color.FromArgb(0xD0, 0x18, 0x18, 0x18);   // 深色描边（已不再使用，保留常量备查）

    /// <summary>
    /// 条子的圆角半径（DIP）。
    ///
    /// ★ 必须**单独定**，不能让玻璃的 `CornerRadius = 10` 直接套下来：
    ///   条子只有 ~11 DIP 高（150% 缩放时约 15 物理像素），
    ///   半径 10 会把两端削成半圆 —— 那就不是"一条线"了。
    ///   取 3 = "看得出是圆角，但整体仍是一条细线"。
    /// </summary>
    private const double StripCoreRadius = 3.0;

    // ★★ 全部改成**全透明**（除下划线）。
    //
    //   原来是 ARGB(0x38,…) = 38% 不透明的深灰。在"系统亚克力"时代这是必要的：
    //   亚克力自己不压暗，行底得自己给文字挣对比度。
    //
    //   现在底下是**自绘玻璃**（抓屏 → 真高斯），38% 深灰压在它上面
    //   等于把玻璃盖住 —— 用户要的"和八宫格一样的材质"就看不见了。
    //   所以行底彻底透明，文字直接浮在玻璃上（八宫格就是这么做的，
    //   可读性靠 DropShadowEffect 的深色晕影撑，参考项目亦然）。
    //
    //   ★ 下划线（RowBorder）保留 —— 用户明确要的「保持下划线」。
    private static readonly Color RowBg = Color.FromArgb(0x00, 0x00, 0x00, 0x00);

    /// <summary>鼠标划过：只留一层**极淡**的白，够看出"鼠标在这行"即可（不能盖住玻璃）。</summary>
    private static readonly Color RowHover = Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF);

    /// <summary>行下划线：比原来的 0x66 灰淡一档，免得在玻璃上显得脏。</summary>
    private static readonly Color RowBorder = Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF);

    /// <summary>
    /// 每行下方「序号 + 时间」小字的颜色。
    ///
    /// ★ 用户明确要求**保持灰色不动** —— 所以主文本加了深色晕影，
    ///   这一行不加（见 <see cref="RowTextShadow"/> 的注释）。
    /// </summary>
    private static readonly Color TimeFg = Color.FromRgb(0xC0, 0xC0, 0xC0);

    /// <summary>
    /// ★ 文字用的**深色晕影** —— 和八宫格**完全同一套参数**
    /// （见 `GridWindow.BuildContent` 里 label 的 Effect）。
    ///
    /// ══ 为什么需要它（用户的原话）════════════════════════════════════
    ///
    ///   「粘贴板上的字体在白色背景下特别不清楚，而九宫格却没有这样的问题。
    ///     我看了看差距，九宫格你在字体后面加了黑色的阴影。」
    ///
    ///   根因：自绘玻璃透出来的桌面**亮暗不可控**。浅色壁纸上白字就糊了。
    ///   参考项目（liquid-glass-react）也是这么做的 ——
    ///   `text-shadow: 0 2px 12px rgba(0,0,0,0.4)`，用**深色阴影**挣对比度，
    ///   **不是**在文字底下垫一层白膜（那正是前几轮"白雾"的来源）。
    ///
    ///   `ShadowDepth = 0` 是关键：那让它是**环绕的描边式晕影**，不是投影，
    ///   所以四面八方都压暗一点，字在亮底上就浮出来了，且不会偏移。
    ///
    /// ⚠️ 这是**静态共享实例** —— WPF 的 Effect 可以跨元素共享，
    ///    但一旦被冻结（Freeze）就不能改。这里没人冻它，所以安全。
    /// </summary>
    private static readonly System.Windows.Media.Effects.DropShadowEffect RowTextShadow = new()
    {
        Color = Colors.Black,
        BlurRadius = 6,
        ShadowDepth = 0,
        Opacity = 0.85,
    };

    /// <summary>
    /// 被点击的那一条：**白框**。
    ///
    /// ★ 用户第六轮的口头修正：「可不可以做成选中加白边？……以白边为标准」。
    ///   原来这里是黄底 + 黄边（SelectedBg / SelectedEdge），已作废。
    ///   八宫格那边的选中色也同步改成了白（GridWindow.ActiveBorder）。
    ///
    /// ★ 白框要**四边都画**（BorderThickness 从 (0,0,0,2) 改成 1,1,1,1），
    ///   否则"白边"只是一条加粗的下划线，和普通的行分不出来。
    /// </summary>
    private static readonly Color SelectedEdge = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

    /// <summary>
    /// 选中时的底：**只留极淡的一层**，真正起作用的是白框。
    /// 不再用黄色（用户否掉了），也不能用不透明色（会盖住玻璃）。
    /// </summary>
    private static readonly Color SelectedBg = Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF);

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
    private readonly GlassParams _glass;

    /// <summary>
    /// ★★ 自绘玻璃 —— 和八宫格**同一套管线**（抓屏 → 真高斯 → 调色 → 上传）。
    ///
    /// ══ 为什么从"系统亚克力"换回自绘 ══════════════════════════════════
    ///
    ///   前四轮的自绘玻璃是**坏的**（`RenderGlass` 抓屏 → 3 遍盒式 → 压暗，
    ///   糊出来的位图永远是"一块中低亮度的灰"），用户连报四轮"黑底"，
    ///   所以我把它整段废掉、改用系统亚克力，并把 `RenderGlass` 留成空方法。
    ///
    ///   现在自绘管线本身已经修好了（真高斯、σ 语义对齐、`ForceOpaque` 保证
    ///   不透明、八宫格上用户验收通过），**而且用户明确要求条子和粘贴板
    ///   要和八宫格一样的材质** —— 所以换回来。
    ///
    /// ⚠️ 亚克力必须**一起关掉**（`OnSourceInitialized` 里的 `TryEnableBackdrop`），
    ///    否则是"亚克力 + 自绘玻璃"两层叠着，又是一次玻璃叠玻璃。
    /// </summary>
    private GlassSurface? _surface;

    /// <summary>玻璃图层（八宫格那套：圆角裁剪 + 位图笔刷 + 矢量高光）</summary>
    private GlassChrome? _chrome;

    /// <summary>
    /// 玻璃的**宿主**容器 —— `Chrome.Root` 挂在这里，尺寸变化重建时整棵换掉。
    /// 见 <see cref="RenderGlass"/> 里的换树逻辑。
    /// </summary>
    private Grid _glassHost = null!;

    /// <summary>材质状态说明，给日志用</summary>
    public string GlassReport { get; private set; } = "(还没渲染过)";

    /// <summary>条子/面板的容器（玻璃层垫在它下面）</summary>
    private Grid? _panes;

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

    public StripPanelWindow(ClipboardHistory history, Action<string> log, GlassParams glass)
    {
        _history = history;
        _log = log;
        _glass = glass;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;

        // ★★★ 真透明 —— 和九宫格同一条修正（第三轮返工）★★★
        //
        //   硬约束 2 原来写的是"亚克力窗口绝不用 AllowsTransparency"，
        //   理由是它会给 HWND 加 WS_EX_LAYERED，而 DWM 材质插在非分层窗口背后。
        //
        //   这条在真机上**是错的**，代价是用户连续三轮看到黑底：
        //   `AllowsTransparency = false` 的窗口，客户区背后有一块不透明的
        //   redirection surface，内容没画到的地方就是**黑**。
        //   面板有圆角、条子只有 15px 高 —— 露出来的地方全是那个黑。
        //
        //   而"分层窗口不能用亚克力"也是反的：
        //   `SetWindowCompositionAttribute` + `ACCENT_ENABLE_ACRYLICBLURBEHIND`
        //   历史上正是配合 `WS_EX_LAYERED` 效果最好。
        AllowsTransparency = true;

        // 自己不再画不透明底。兜底色也必须带 alpha —— 就算退化，也得是透的。
        Background = new SolidColorBrush(Color.FromArgb(0x00, 0x1C, 0x1C, 0x1C));

        Content = BuildContent();
        SourceInitialized += OnSourceInitialized;
    }

    // ── 构建 ────────────────────────────────────────────────────────

    private UIElement BuildContent()
    {
        var outer = new Grid();
        var root = new Grid();
        _panes = root;

        // ① 面板：铺满窗口。收起时隐藏。
        _panel = new Border
        {
            // ★★ 现在**不画任何底色** —— 模糊由自绘玻璃（_surface）提供。
            //
            //   这里曾经是 72% 不透明的深灰 `PanelBg`，那是"系统亚克力"时代的折中：
            //   亚克力自己不压暗，只能靠这层深灰让白字读得清。
            //   现在底下换成和八宫格同一套自绘玻璃（抓屏 → 真高斯 → 上传），
            //   再压 72% 深灰就等于**把玻璃糊掉**，白做。
            //
            //   所以：底色全透明，需要压暗时用 GlassParams.ScrimOpacity（默认 0）。
            //   和 RowBg 改透明是同一件事的两个位置 —— 见那里的注释。
            Background = Brushes.Transparent,
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

            // ★★ 右侧滚动条换成"窄、透明轨道、柔白圆角滑块、无小箭头"。
            //
            //   原来的 WPF 默认样式在自绘玻璃上是一整条**灰色**（约 17px 宽，
            //   两端还带两个灰色小箭头）—— 和这一轮"去灰去黑"的目标相反。
            //
            //   ⚠️ **必须自定义 ControlTemplate**：只改 Width 和颜色是改不掉
            //      WPF 默认模板里那两个灰箭头的（它们是模板里的 RepeatButton）。
            //
            //   ⚠️ 模板里那四个 Token **必须留着**（即使按钮被 Collapsed）——
            //      Track 的翻页/逐行行为靠它们绑定命令，
            //      漏掉会让 PageUp/PageDown 失效。
            //
            //   滑块宽度 7（用户选的"窄条 6–8px"），柔白 0xCCFFFFFF（80% 不透明）。
            Resources = BuildScrollBarStyles(),
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
            // ★★ 深色描边改成**玻璃上方的一圈细描边**，而不是原来那层深色底。
            //
            //   原来：外层 Border 用深色 + Padding 缩出亮芯 —— 那是**不透明的深色面**，
            //         在自绘玻璃上面会把玻璃盖住，所以必须去掉。
            //   现在：Border 本身透明，只用 `BorderThickness` 画 1 物理像素的**圆角描边**。
            //
            //   ★ 为什么还要描边：`StripCore` 是**半透明**的亮芯，
            //     在浅色壁纸上会消失 —— 用户为这件事返工过两轮
            //     （「任何单一颜色都会在某个背景上消失」）。
            //     一圈深色描边是"白底上仍可见"的唯一保证。
            //
            //   ⚠️ WPF Border 的圆角边框只把**外圈**画圆，内圈是直的 ——
            //      1 物理像素、半径 3 的情况下肉眼看不出来，可以接受。
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(StripEdge),
            BorderThickness = new Thickness(edgePx),
            CornerRadius = new CornerRadius(StripCoreRadius),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Cursor = Cursors.Hand,
            Child = new Border
            {
                Background = new SolidColorBrush(StripCore),   // 亮芯
                CornerRadius = new CornerRadius(StripCoreRadius),
            },
        };
        root.Children.Add(_strip);

        // ★ 玻璃层垫在最底下（先加 = 在下层）。
        //
        //   ★★ 第六轮：这里从"只画着色的 GlassChrome"换成了**完整的 GlassSurface**
        //      （八宫格那套：自己抓屏、真高斯、上传位图）。见 _surface 的注释。
        //
        //   ⚠️ 为什么玻璃外面还要套一个**宿主 Grid**（_glassHost）——
        //      条子和面板尺寸不同，`BindWindow` 在尺寸变化时会**重建** GlassChrome，
        //      而重建出来的 `Chrome.Root` 是一棵**全新的**视觉树。
        //      如果直接把 `Chrome.Root` 塞进窗口，重建之后窗口里挂的还是**旧的那棵**
        //      （旧树没有位图 → 界面上什么都看不到）。
        //      所以固定挂宿主，每次渲染后把当前这棵换进去。
        _glassHost = new Grid
        {
            Background = Brushes.Transparent,
            IsHitTestVisible = false,      // 玻璃永远不吃鼠标
        };

        _surface = new GlassSurface(_glass);
        _surface.BindWindow(Geometry.PanelWidthDip, Geometry.PanelHeightDip);
        _chrome = _surface.Chrome;
        if (_chrome is not null) _glassHost.Children.Add(_chrome.Root);

        outer.Children.Add(_glassHost);
        outer.Children.Add(root);

        return outer;
    }

    /// <summary>
    /// 面板**专属**的滚动条样式 —— 窄、透明轨道、柔白圆角滑块、无小箭头。
    ///
    /// ══ 为什么非写模板不可 ═══════════════════════════════════════════
    ///
    ///   WPF 默认的 `ScrollBar` 模板里，两端各挂着一个灰色的 `RepeatButton`
    ///   （就是用户截图里那两个小箭头）。**只设 Width 和 Background 是改不掉它们的** ——
    ///   它们是模板内部生成的元素，必须换掉整个 `ControlTemplate`。
    ///
    /// ══ ⚠️ 那四个命令 Token 不能漏 ═══════════════════════════════════
    ///
    ///   `Track` 把翻页/逐行行为**挂在** RepeatButton 的
    ///   `PageUp/PageDown/LineUp/LineDown` 命令上。
    ///   按钮虽然 `Visibility=Collapsed`（用户看不到），但**命令绑定必须留着** ——
    ///   漏掉的话按 PageUp/PageDown、以及 Track 内部的分页逻辑都会失效。
    ///   （Collapsed 只是不画，不解除绑定，所以这里正好。）
    ///
    /// ══ 挂在哪 ═══════════════════════════════════════════════════════
    ///
    ///   挂在 `_scroll.Resources` 上 —— **只影响这个 ScrollViewer 内部的滚动条**，
    ///   不污染全局（调参窗口等地方的滚动条不该被牵连）。
    ///   隐式样式（只有 TargetType、没有 x:Key）会自动命中作用域内的所有 ScrollBar。
    /// </summary>
    /// <summary>
    /// 返回滚动条样式，供产品内滚动区域复用。
    /// </summary>
    private static System.Windows.ResourceDictionary BuildScrollBarStyles()
    {
        // 用 XAML 字符串而不是 FrameworkElementFactory：
        // 模板有嵌套（Track → Thumb → 内层模板），用工厂类拼可读性会崩掉。
        const string xaml = """
            <ResourceDictionary
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

              <Style TargetType="ScrollBar">
                <!-- 窄条：用户选的 6–8px，取 7 -->
                <Setter Property="Width" Value="7"/>
                <Setter Property="Background" Value="Transparent"/>
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="ScrollBar">
                      <!-- 轨道透明 → 露出下面的玻璃，不画任何灰底 -->
                      <Grid Background="Transparent">
                        <Track x:Name="PART_Track" IsDirectionReversed="True">
                          <Track.DecreaseRepeatButton>
                            <RepeatButton Command="ScrollBar.PageUpCommand"
                                          Visibility="Collapsed" Focusable="False"/>
                          </Track.DecreaseRepeatButton>

                          <Track.Thumb>
                            <Thumb MinHeight="24">
                              <Thumb.Template>
                                <ControlTemplate TargetType="Thumb">
                                  <!-- 柔白 80%（0xCC）的圆角滑块 -->
                                  <Border Background="#CCFFFFFF" CornerRadius="3.5"/>
                                </ControlTemplate>
                              </Thumb.Template>
                            </Thumb>
                          </Track.Thumb>

                          <Track.IncreaseRepeatButton>
                            <RepeatButton Command="ScrollBar.PageDownCommand"
                                          Visibility="Collapsed" Focusable="False"/>
                          </Track.IncreaseRepeatButton>
                        </Track>
                      </Grid>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
              </Style>

            </ResourceDictionary>
            """;

        return (System.Windows.ResourceDictionary)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    private UIElement BuildTitleBar()
    {
        // 内容层（标题 + 右对齐按钮区）
        var bar = new Grid
        {
            Background = Brushes.Transparent,
        };

        // ★ 标题栏现在只有一列 —— 「▲▼」两个滚动按钮已经删掉。
        //   理由：① 它们是黑色方块，在玻璃上很丑，和"去灰去黑"的目标相反；
        //        ② 功能上和右侧滚动条重复；
        //        ③ 滚轮实测收得到（WndProc 的 WM_MOUSEWHEEL），滑块也能拖。
        bar.ColumnDefinitions.Add(new ColumnDefinition());

        _title = new TextBlock
        {
            // ★ 从淡蓝（原来的 Accent 0x8CD0FF）改成**纯白**
            //   —— 用户要求「粘贴板三个字改为白色」，且计数那半截也一起变白。
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 12,
            Margin = new Thickness(8, 5, 4, 5),
            Text = "粘贴板",

            // ★ 和八宫格**完全相同**的一套深色晕影（GridWindow.cs 里 label 的那个）。
            //   ShadowDepth = 0 → 是描边式的晕影，不是投影，所以不会糊成一片。
            Effect = RowTextShadow,
        };
        bar.Children.Add(_title);

        // ★★ 标题栏与列表之间那道**白色分割线**（用户要求）。
        //
        //   为什么要套一层 Border：`Grid` 继承自 `Panel`，**没有**
        //   `BorderBrush` / `BorderThickness` —— 画不了线。`Border` 才能。
        //
        //   ══ 三个刻意的选择 ═══════════════════════════════════════════
        //
        //   ① **横跨整个面板宽度**（不给左右缩进）。
        //      文字框那道的分割线挂在 `_scroll` 上，而 `_scroll` 有
        //      `Padding(6,4,6,6)` → 它左右各缩进 6 DIP。
        //      这一道挂在**标题栏这一层**（`_scroll` 外面），所以天然通到两边，
        //      正好形成"上面这条是分区块、下面那些是分条目"的层次。
        //
        //   ② **1.5 DIP**，比文字框那道的 1 DIP 粗一档（用户："稍微粗一点"）。
        //      1.5 DIP 在 150% 缩放下约 2.25 物理像素，WPF 会做亚像素抗锯齿，
        //      所以看起来是"更实的一道"，不是硬邦邦的两像素。
        //
        //   ③ **颜色复用 `RowBorder`**（#40FFFFFF，25% 不透明白）。
        //      用户明确说"和文字框分割线同色"。
        //      共用一个常量 = 以后改一处，两处一起变。
        //
        //   ★ 不要在 Border 上加 Margin/Padding：用户要求"先不动间距"。
        //     线就落在标题文字下方那 5 DIP 处，紧贴列表区上沿。
        var framed = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(RowBorder),
            BorderThickness = new Thickness(0, 0, 0, 1.5),
            Child = bar,
        };

        return framed;
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

        // ★ 真透明的关键一步（和九宫格同一处修正）。
        //   少了它，客户区背后永远垫着一块不透明位图 —— 那就是"黑底"。
        IntPtr ex2 = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE,
            (IntPtr)(ex2.ToInt64() | WS_EX_NOREDIRECTIONBITMAP));

        // ★★ 亚克力**不启用** —— 自绘玻璃已经接管背景。
        //
        //   原来是 `TryEnableBackdrop(_hwnd, out string how)`。
        //   留着它就是"系统亚克力 + 自绘玻璃"两层模糊叠在一起，
        //   正是用户明确不要的那件事（他原话：「不需要叠加」）。
        //
        //   ⚠️ `TryEnableBackdrop` 本身**故意保留**（NativeMethods 里没删）：
        //      万一自绘玻璃这条路又出问题，它是现成的退路，
        //      只需把下面这行取消注释即可。
        // bool backdrop = TryEnableBackdrop(_hwnd, out string how);

        int dark = 1;
        DwmSetWindowAttribute(_hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        _log($"  ★ 条子扩展样式: NOACTIVATE="
             + $"{(GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_NOACTIVATE) != 0}"
             + $" / 圆角=不圆 / 真透明=True");
        _log("  ★ 条子背景材质：自绘玻璃（抓屏 → 真高斯 → 上传）"
             + "，系统亚克力**未启用**（避免两层模糊叠加）");
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

        // ★ 抓屏必须在 ApplyBounds（= SetWindowPos 显示窗口）**之前**。
        //   条子是常驻显示的，RenderGlass 内部会把自己排除出抓屏。
        //   （圆角裁剪/描边也由它按当前尺寸重设，见 RenderGlass）
        RenderGlass(xPx, yPx, wPx, hPx, force: false);

        ApplyBounds(xPx, yPx, wPx, hPx, "收起");
        SetClickThrough(true);

        _log($"  [条子] 收起 → 物理({xPx},{yPx}) {wPx}×{hPx}px "
             + $"中心x={_anchorCenterXPx} 缩放{_scale:0.##}× 实际={BoundsText} 穿透={_clickThrough}");
        _log($"  [条子·玻璃] {GlassReport}");
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

        // ★ 面板尺寸变了：玻璃的圆角裁剪和背景抓屏都要按新尺寸重来。
        //   两者都必须发生在 ApplyBounds 之前（见 RenderGlass 的注释）。
        RenderGlass(xPx, yPx, wPx, hPx, force: true);

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
        // ★ 面板玻璃的取证行 —— 和条子一样必须打出来，
        //   否则"面板到底有没有渲染玻璃"只能靠猜（这一轮就是靠它发现的）。
        _log($"  [面板·玻璃] {GlassReport}");
    }

    /// <summary>
    /// 抓一次背景 + 渲染玻璃，然后贴到图层上。
    ///
    /// ★ **必须在 SetWindowPos 显示窗口之前调** ——
    ///   窗口一旦可见，抓到的就是"玻璃盖在自己身上"的画面，越叠越脏。
    ///
    /// sheet = true 时给条子用：条子**常驻显示**，所以抓屏时要把自己
    /// 排除掉（SetWindowDisplayAffinity），否则同样会糊到自己。
    ///
    /// <paramref name="force"/> = false 时只在**前台窗口变了**才真的重抓 ——
    /// 抓屏有 ~4ms 的固定开销，静止期间没必要反复抓。
    /// </summary>
    /// ══ ★ 第六轮：这个方法**重新开始干活了** ═══════════════════════════
    ///
    ///   第四轮我把它清空成 no-op，原因是"抓屏糊出来的位图就是用户说的黑底"。
    ///   那个判断**当时是对的** —— 当时它调的是「3 遍盒式 + 压暗」，
    ///   糊出来的必然是一块均匀的灰。
    ///
    ///   现在换成八宫格那套已经验收过的管线（真高斯、ForceOpaque、σ 对齐），
    ///   用户又明确要求"条子和粘贴板用同样的材质"，所以它重新上线。
    ///
    /// ══ ★ 条子必须"抓屏时把自己排除掉" ═══════════════════════════════
    ///
    ///   八宫格没这个问题：它**只在弹出时渲染一次，而且渲染在窗口显示之前**，
    ///   那时窗口里还是空的。
    ///
    ///   条子**常驻显示** —— 重抓时窗口里已经是我们自己上一帧的玻璃。
    ///   不排除的话抓到的就是自己，一帧一帧自我叠加（"越叠越脏"）。
    ///   所以抓之前 `WDA_EXCLUDEFROMCAPTURE`，抓完**立刻** `WDA_NONE`。
    ///
    ///   ⚠️ 必须成对：这个标志挂着不摘，用户截图取证时会**唯独看不到这个窗口**。
    ///
    /// <paramref name="needExcludeSelf"/> = true 时（条子）才做上面这对操作；
    /// 面板是展开时才渲染的，同样要排除 —— 所以两个路径都传 true。
    ///
    /// <paramref name="force"/> = false 时，**只有前台窗口变了**才真的重抓 ——
    /// 抓屏有 ~4ms 的固定开销，静止期间一次都不该抓。
    /// </summary>
    private void RenderGlass(int xPx, int yPx, int wPx, int hPx, bool force)
    {
        if (_surface is null) return;
        if (wPx <= 0 || hPx <= 0) return;

        // ── 不强制时：前台窗口没变就不重抓 ──
        IntPtr foreground = GetForegroundWindow();
        if (!force && foreground == _lastGlassForeground && wPx == _lastGlassW && hPx == _lastGlassH)
        {
            return;
        }

        // ── 尺寸变了：先重新绑定（会重建抓屏器/位图/玻璃图层）──
        double dipW = wPx / _scale;
        double dipH = hPx / _scale;

        // ★ 条子太矮，圆角得单独定 —— 必须在 BindWindow 之前设。
        //   高度小于两倍默认圆角时用一个小值，否则两端会被削成半圆。
        bool isStrip = hPx < Geometry.DipToPx(Geometry.PanelHeightDip, _scale) / 2;
        _surface.CornerRadiusOverride = isStrip ? StripCoreRadius : null;

        var chromeBefore = _surface.Chrome;
        _surface.BindWindow(dipW, dipH);

        // ★★ BindWindow 尺寸变了会**重建** GlassChrome —— 挂进窗口的那棵树得换新的。
        //   不换的话：窗口里挂的还是旧树，旧树没有位图 → 画面上什么都看不见。
        //   （这是"玻璃层是个宿主 Grid"这个设计存在的唯一理由，见 BuildContent）
        if (!ReferenceEquals(chromeBefore, _surface.Chrome))
        {
            _chrome = _surface.Chrome;
            _glassHost.Children.Clear();
            if (_chrome is not null) _glassHost.Children.Add(_chrome.Root);
        }

        // ── 抓屏时把自己排除掉（见方法注释的警告）──
        bool excluded = ScreenCapture.SetCaptureVisibility(_hwnd, exclude: true);
        if (!excluded)
        {
            _log("  ⚠️ 条子抓屏前的自我排除失败（老系统不支持 WDA_EXCLUDEFROMCAPTURE）"
                 + " → 玻璃里可能会糊到自己的上一帧");
        }

        try
        {
            bool ok = _surface.Refresh(xPx, yPx, wPx, hPx);
            GlassReport = ok
                ? $"★ 自绘玻璃 {wPx}×{hPx}px（{_surface.LastBreakdown}）"
                  + $"｜ {_surface.Chrome?.LastAttachedReport}"
                : $"❌ 渲染失败：{_surface.LastBreakdown}";
        }
        finally
        {
            // ★ 无论成败都要摘掉 —— 摘不掉的话用户的截图里会少这个窗口
            if (excluded && !ScreenCapture.SetCaptureVisibility(_hwnd, exclude: false))
            {
                _log("  ⚠️ 抓屏排除**没能恢复** —— 截图里会看不到条子/面板（重启即可恢复）");
            }
        }

        _lastGlassForeground = foreground;
        _lastGlassW = wPx;
        _lastGlassH = hPx;
    }

    /// <summary>上一次渲染玻璃时的前台窗口 —— 用来判断"要不要重抓"。</summary>
    private IntPtr _lastGlassForeground;
    private int _lastGlassW, _lastGlassH;

    /// <summary>
    /// 调参面板拖动时调用：按当前窗口实际矩形重画玻璃。
    ///
    /// 这是一个**只给调参用的公开入口**，正常展开/收起仍然走各自的
    /// ShowCollapsed / ShowExpanded 路径。坐标和尺寸都是物理像素。
    /// </summary>
    public void RefreshGlassForTuning(RectPx bounds)
    {
        RenderGlass(bounds.X, bounds.Y, bounds.W, bounds.H, force: true);
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

            // ★ 深色晕影 —— 见 RowTextShadow 的注释。
            //   这是用户明确要求的那一条：「九宫格你在字体后面加了黑色的阴影，
            //   我希望这个也应用在粘贴板上」。
            Effect = RowTextShadow,
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

        // ★ 先把上一条的白框撤掉，再点亮这一条 —— 保证同时只有一条是"选中的"
        ClearSelection();
        row.Background = new SolidColorBrush(SelectedBg);
        row.BorderBrush = new SolidColorBrush(SelectedEdge);
        row.BorderThickness = new Thickness(1);      // ★ 四边，白框
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
        _selectedRow = null;    }

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
