using System;

namespace S2Strip;

/// <summary>
/// 全部尺寸的唯一换算处。**别的地方不许再出现毫米、DIP、像素的互算。**
///
/// ── 单位链（唯一的记忆点）────────────────────────────────────
///
///     DIP    = 毫米 × 96 ÷ 25.4 = 毫米 × 3.77953      ← 不含 DPI，与缩放无关
///     物理px = DIP × (dpi ÷ 96)                        ← 缩放只在这一步进来
///
///   为什么 DIP 那一步与缩放无关：1 DIP ≡ 1/96 英寸，是**物理长度单位**。
///   "1 毫米等于多少 DIP"纯粹是长度换算，跟屏幕缩放没有关系。
///   只有把 DIP 变成实际像素时，才需要知道 96 还是 144。
///
/// ── S0 用一次 bug 换来的铁律 ────────────────────────────────
///
///   ★ SetWindowPos 收的是**物理像素**
///   ★ Window.Width/Height/Left/Top 收的是 **DIP**
///   混用会让窗口既偏移又"每次按都缩小"（S0 踩过，见 PanelWindow.cs 的注释）。
///   所以这里所有对外的方法都**显式写明**返回的是 DIP 还是 px，后缀 _dip / _px。
/// </summary>
internal sealed class Geometry
{
    /// <summary>1 毫米 = 多少 DIP。96 DPI 下 1 英寸 = 25.4mm = 96 DIP。</summary>
    private const double DipPerMm = 96.0 / 25.4;      // ≈ 3.779528

    /// <summary>黄金比</summary>
    private const double Golden = 1.6180339887;

    // ── 用户要求的真实尺寸（毫米）────────────────────────────────
    //
    // 用户原话：「触发条就 1cm 长，0.1cm 宽」→ 10mm × 1mm
    //           「小面板……最好是黄金比，宽 4cm 左右」→ 40mm 宽
    //
    // ★ 注意：这些是**真实世界的毫米**，不是像素数。
    //   我一开始把 1mm 理解成"头发丝那么细"，那是错的 ——
    //   头发直径只有 0.05~0.1mm，1mm 是它的 10~20 倍。
    //   更接近的参照物是**信用卡的厚度（0.76mm）**。

    /// <summary>可见条子的长（真实 10mm = 1cm）</summary>
    public const double StripWidthMm = 10.0;

    /// <summary>可见条子的宽（真实 1mm）</summary>
    public const double StripHeightMm = 1.0;

    /// <summary>面板宽（真实 40mm = 4cm）</summary>
    public const double PanelWidthMm = 40.0;

    /// <summary>面板高 = 宽 × 黄金比（真实 64.7mm）</summary>
    public static double PanelHeightMm => PanelWidthMm * Golden;

    // ── 位置（DIP）──────────────────────────────────────────────

    /// <summary>条子右缘距工作区右边多少 DIP。用户说"往中间偏一点"。</summary>
    public const double AnchorMarginRight = 8.0;

    /// <summary>条子顶边距工作区上边多少 DIP —— 这就是"往中间偏一点"</summary>
    public const double AnchorMarginTop = 10.0;

    // ── 窗口尺寸（DIP）──────────────────────────────────────────
    //
    // ★ 为什么收起态的窗口**不是** 1mm 高：
    //
    //   WPF/Windows 有一个**系统级最小窗口尺寸地板**（WM_GETMINMAXINFO），
    //   大约 38 物理像素高。1mm 在 150% 缩放下只有 5.67px —— 会被顶到 38px，
    //   也就是说**窗口本身做不成 1mm 高**。
    //
    //   解法：窗口只是个**容器**，真正 1mm 的是**画在里面的那条线**。
    //   容器做成 10mm × 10mm（远高于地板），线贴它上边缘画。
    //   多出来的 9mm 是透明的、鼠标穿透的，视觉上根本不存在。
    //
    //   顺带的好处：鼠标落进这 10mm × 10mm 的圈里就算"接近"，
    //   不用去瞄那 1mm 的线。

    /// <summary>收起态窗口边长（DIP）= 条子长，正方形，绕开窗口尺寸地板</summary>
    public static double CollapsedSideDip => StripWidthMm * DipPerMm;

    /// <summary>展开态面板宽（DIP）</summary>
    public static double PanelWidthDip => PanelWidthMm * DipPerMm;

    /// <summary>展开态面板高（DIP）</summary>
    public static double PanelHeightDip => PanelHeightMm * DipPerMm;

    /// <summary>可见条子的长（DIP）= 真实 10mm</summary>
    public static double StripWidthDip => StripWidthMm * DipPerMm;

    /// <summary>可见细线的高度（DIP）。真实 1mm。
    /// 150% 缩放下 = 5.67 物理像素，取整渲染成 5~6px。</summary>
    public static double StripHeightDip => StripHeightMm * DipPerMm;

    // ── 命中判定 ────────────────────────────────────────────────

    /// <summary>
    /// 收起态的命中矩形（物理像素）：**就是整个 10mm × 10mm 窗口**。
    ///
    /// 为什么用整个窗口而不是那条 1mm 的线：线太细（5.67px），
    /// 鼠标很难精准压上去。窗口的其余部分是透明的，
    /// 让"接近"的判定范围等于窗口范围，手感好得多。
    /// </summary>
    public static RectPx CollapsedHitRect(int anchorRightPx, int anchorTopPx, double scale)
    {
        int w = (int)Math.Round(CollapsedSideDip * scale);
        return new RectPx(anchorRightPx - w, anchorTopPx, w, w);
    }

    /// <summary>展开态的命中矩形（物理像素）：整个面板</summary>
    public static RectPx ExpandedHitRect(int anchorRightPx, int anchorTopPx, double scale)
    {
        int w = (int)Math.Round(PanelWidthDip * scale);
        int h = (int)Math.Round(PanelHeightDip * scale);
        return new RectPx(anchorRightPx - w, anchorTopPx, w, h);
    }

    // ── 换算工具 ────────────────────────────────────────────────

    /// <summary>毫米 → DIP。与缩放无关。</summary>
    public static double MmToDip(double mm) => mm * DipPerMm;

    /// <summary>DIP → 物理像素。缩放在这一步进来。</summary>
    public static int DipToPx(double dip, double scale) => (int)Math.Round(dip * scale);

    /// <summary>物理像素 → DIP</summary>
    public static double PxToDip(int px, double scale) => px / scale;

    /// <summary>
    /// 把一段话写成人看的尺寸说明（日志用）。
    /// 三个单位一起打出来，出现偏差时一眼能看出是哪一步算错了。
    /// </summary>
    public static string Describe(string name, double mm, double scale)
    {
        double dip = MmToDip(mm);
        double px = dip * scale;
        return $"{name}: {mm:0.###}mm → {dip:0.###} DIP → {px:0.###} px（{scale:0.##}×）";
    }
}

/// <summary>
/// 一个物理像素矩形。刻意不用 WPF 的 Rect —— 那是 DIP 语义的，
/// 混进来正是 S0 那个 bug 的成因。类型上分开，就没法写错。
/// </summary>
internal readonly struct RectPx
{
    public readonly int X;
    public readonly int Y;
    public readonly int W;
    public readonly int H;

    public RectPx(int x, int y, int w, int h)
    {
        X = x;
        Y = y;
        W = w;
        H = h;
    }

    public int Right => X + W;
    public int Bottom => Y + H;

    /// <summary>点在不在矩形里（左闭右开，避免边界同时属于两个矩形）</summary>
    public bool Contains(int px, int py) =>
        px >= X && px < Right && py >= Y && py < Bottom;

    /// <summary>两个矩形有没有重叠 —— 用来判断"鼠标是不是从条子移到面板上"</summary>
    public bool Intersects(in RectPx other) =>
        X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;

    public override string ToString() => $"({X},{Y} {W}×{H})";
}
