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
///   混用会让窗口既偏移又"每次按都缩小"（S0 踩过）。
///   所以这里所有对外的方法都**显式写明**返回的是 DIP 还是 px。
/// </summary>
internal sealed class Geometry
{
    /// <summary>1 毫米 = 多少 DIP。96 DPI 下 1 英寸 = 25.4mm = 96 DIP。</summary>
    private const double DipPerMm = 96.0 / 25.4;      // ≈ 3.779528

    /// <summary>黄金比</summary>
    private const double Golden = 1.6180339887;

    // ══ 用户的硬性尺寸要求（第一轮实测后定稿）════════════════════
    //
    // ★ 第一轮是 10mm × 1mm / 40mm，用户量出来只有 0.7cm 左右、且太薄。
    //   于是长度和厚度**都翻倍** → 20mm × 2mm。
    //   面板嫌小，宽 ×1.6 → 64mm。
    //
    // ★ 关于"量出来只有 0.7cm"要多说一句（这是"真实毫米"自带的坑）：
    //   Windows **只知道显示设置里的缩放比例，不知道显示器的真实物理尺寸**。
    //   如果缩放和面板真实密度对不上（很常见），按 DPI 算出来的毫米
    //   就**不是**真实毫米。所以留了 MmCalibration 标定系数。

    /// <summary>
    /// 毫米标定系数。
    ///
    /// ── 为什么需要它（这是"真实毫米"自带的坑）──────────────────
    ///
    ///   **Windows 只知道你在"显示设置"里选的缩放比例，不知道你的显示器有多大。**
    ///   本机：2560×1440 物理像素、缩放 150% → Windows 认为 144 DPI。
    ///   如果这块屏真实是 27 英寸，实际密度约 109 PPI。
    ///   于是 Windows 眼里的"1cm"（56.7px）**在真实世界里是 1.32cm**。
    ///
    ///   两者对不上时，按 DPI 算出来的毫米就**不是**真实毫米。
    ///
    /// ── 当前值怎么来的 ────────────────────────────────────────
    ///
    ///   第一轮代码的 StripWidthMm = 20（要 2cm），用户拿尺子量出 **1.5cm**。
    ///   长和宽都是同一个比例 1.5 ÷ 2 = 0.75 —— 印证了是全局的密度偏差，
    ///   不是某一处算错。
    ///
    ///       新系数 = 旧系数 × 目标 ÷ 实测 = 1.0 × 2 ÷ 1.5 = 1.3333
    ///
    ///   注意：这个系数是**全局**的，条子和面板一起放大 ——
    ///   因为它是物理测量的修正，只对条子生效在逻辑上说不通。
    ///
    /// ── 以后怎么调 ────────────────────────────────────────────
    ///
    ///   拿尺子量条子长边，量到 L 厘米（目标 2 厘米）：
    ///       新系数 = 当前系数 × 2 ÷ L
    /// </summary>
    public const double MmCalibration = 1.3333;

    /// <summary>可见条子的长（真实 20mm = 2cm）</summary>
    public const double StripWidthMm = 20.0;

    /// <summary>可见条子的宽（真实 2mm）</summary>
    public const double StripHeightMm = 2.0;

    /// <summary>面板宽（真实 64mm = 6.4cm）</summary>
    public const double PanelWidthMm = 64.0;

    /// <summary>
    /// 面板高 = 宽 ÷ 黄金比（真实 39.55mm）。
    ///
    /// 注意是**除**不是乘：用户要的是"竖着的长方形"。
    /// 黄金比竖长方形传统上指「高 : 宽 = 1 : 1.618」，即宽 = 1.618 × 高，
    /// 所以高 = 宽 ÷ 1.618。
    /// </summary>
    public static double PanelHeightMm => PanelWidthMm / Golden;

    // ══ 位置（用户的硬性要求）════════════════════════════════════
    //
    // ★ 第一轮把条子钉在右上角（距右边缘 8 DIP），用户反馈"很影响操作"——
    //   那个位置正好压着最大化窗口的关闭按钮。
    //
    //   改后：
    //     · 横向：条子**中心**在屏幕宽度的 3/4 处（即"右四分之一处"）
    //     · 纵向：**紧贴工作区顶部，零缝隙**

    /// <summary>横向锚点：条子中心在屏幕宽度的这个比例处</summary>
    public const double CenterAtWidthRatio = 0.75;

    /// <summary>纵向锚点：距工作区上边多少 DIP。0 = 紧贴顶部，零缝隙。</summary>
    public const double AnchorMarginTop = 0.0;

    // ══ 窗口尺寸 ══════════════════════════════════════════════════
    //
    // ★ 第一轮的大错误：**窗口尺寸 ≠ 条子尺寸**。
    //
    //   我第一轮把窗口做成 10mm × 10mm，可见细线只占顶上一小条，
    //   命中判定用整个窗口 —— 于是**命中区是条子面积的 10 倍**。
    //   用户明确否掉了：「你的条有多大，触发范围就有多大」。
    //
    //   所以现在：窗口就是条子本身，没有一寸多余面积。

    /// <summary>收起态窗口宽（DIP）= 条子长</summary>
    public static double CollapsedWidthDip => StripWidthMm * DipPerMm * MmCalibration;

    /// <summary>收起态窗口高（DIP）= 条子厚度。150% 缩放下约 11.3px。</summary>
    public static double CollapsedHeightDip => StripHeightMm * DipPerMm * MmCalibration;

    /// <summary>可见细线的宽（DIP）。真实 20mm。</summary>
    public static double StripWidthDip => CollapsedWidthDip;

    /// <summary>可见细线的高（DIP）。真实 2mm。</summary>
    public static double StripHeightDip => CollapsedHeightDip;

    /// <summary>展开态面板宽（DIP）</summary>
    public static double PanelWidthDip => PanelWidthMm * DipPerMm * MmCalibration;

    /// <summary>展开态面板高（DIP）</summary>
    public static double PanelHeightDip => PanelHeightMm * DipPerMm * MmCalibration;

    // ══ 命中判定 ══════════════════════════════════════════════════

    /// <summary>
    /// 收起态的命中矩形（物理像素）—— **就是条子本身，一点都不外扩**。
    ///
    /// 用户明确要求「你的条有多大，触发范围就有多大」。
    /// 所以这里没有 padding、没有隐形热区，窗口矩形 == 命中矩形。
    /// </summary>
    public static RectPx CollapsedHitRect(int centerXPx, int anchorTopPx, double scale)
    {
        int w = (int)Math.Round(CollapsedWidthDip * scale);
        int h = (int)Math.Round(CollapsedHeightDip * scale);
        return new RectPx(centerXPx - w / 2, anchorTopPx, w, h);
    }

    /// <summary>展开态的命中矩形（物理像素）：整个面板，与条子同一个中心</summary>
    public static RectPx ExpandedHitRect(int centerXPx, int anchorTopPx, double scale)
    {
        int w = (int)Math.Round(PanelWidthDip * scale);
        int h = (int)Math.Round(PanelHeightDip * scale);
        return new RectPx(centerXPx - w / 2, anchorTopPx, w, h);
    }

    /// <summary>收起态窗口左上角 x（物理像素）—— 以中心反推</summary>
    public static int CollapsedLeftPx(int centerXPx, double scale)
        => centerXPx - (int)Math.Round(CollapsedWidthDip * scale) / 2;

    /// <summary>展开态窗口左上角 x（物理像素）—— 以中心反推</summary>
    public static int ExpandedLeftPx(int centerXPx, double scale)
        => centerXPx - (int)Math.Round(PanelWidthDip * scale) / 2;

    // ══ 换算工具 ══════════════════════════════════════════════════

    /// <summary>毫米 → DIP。与缩放无关。</summary>
    public static double MmToDip(double mm) => mm * DipPerMm * MmCalibration;

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
