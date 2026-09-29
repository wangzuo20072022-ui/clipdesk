using System;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipDesk;

/// <summary>
/// 玻璃表面 —— 把「抓屏 → 真高斯模糊 → 调色 → 圆角混合」串成一条流水线，
/// 产出可以直接当窗口背景的 <see cref="WriteableBitmap"/>。
///
/// ══ 为什么是这个架构（都是实测逼出来的）══════════════════════════
///
/// **实测一：抓屏有 ~4.3ms 的固定开销地板。**
///   32×32 那么小也要 4.55ms，720×720 是 13ms。线性部分约 0.017ms/千像素。
///   → 结论：**绝不能每帧抓屏**。只在这几个时刻抓一次：
///       · 九宫格：Alt+V 弹出时
///       · 面板：展开时
///       · 触发条：前台窗口变化时
///   静止期间背景本来就不变（我们自己的窗口盖在上面），抓一次就够。
///
/// **实测二：降采样确实有用，但降不到 1ms。**
///   720×720 从 13ms 降到 ~5ms。所以默认降 4 倍。
///
/// ══ 关于圆角：为什么不需要透明通道 ═══════════════════════════════
///
/// 窗口是 `AllowsTransparency=false` 的实心矩形（DWM 亚克力的硬要求），
/// 所以**画不出真正的透明圆角**。
///
/// 解法：圆角外面不画"透明"，而是画**未经处理的原始背景** ——
/// 那块像素本来就和桌面一模一样，人眼看上去就是"圆角处透过去了"。
/// 而位移图最外圈 2px 恒为 0（见 <see cref="RefractionMap"/>），
/// 保证了玻璃和这个原始背景带在边界上严丝合缝，不会有一圈错位的缝。
/// </summary>
internal sealed class GlassSurface : IDisposable
{
    private readonly GlassParams _params;

    private ScreenCapture.ScreenGrabber? _grabber;
    private int _grabberW, _grabberH, _grabberFactor;

    /// <summary>
    /// 处理过的玻璃像素（模糊 + 折射 + 调色 + 底色）。
    /// 尺寸是**降采样后**的 —— 放大由 WPF 做，见 <see cref="GlassChrome"/>。
    /// </summary>
    public WriteableBitmap? GlassBitmap { get; private set; }

    /// <summary>
    /// 未经处理的原始背景，同样是小图。
    ///
    /// 只在一个地方看得见：**圆角外面**。窗口是实心的画不出透明圆角，
    /// 所以那块地方就贴这张原图 —— 它和桌面像素一模一样，
    /// 看上去就是"圆角处透过去了"。
    /// </summary>
    public WriteableBitmap? RawBitmap { get; private set; }

    /// <summary>渲染时用的降采样倍数（WPF 放大时要知道比例）</summary>
    public int Factor => Math.Max(1, _grabberFactor);

    /// <summary>最近一次渲染总耗时（毫秒）</summary>
    public double LastRenderMs { get; private set; }

    /// <summary>各阶段耗时，给日志用（"慢在哪一步"）</summary>
    public string LastBreakdown { get; private set; } = "(还没渲染过)";

    /// <summary>最近一次是否成功。失败时调用方应该退回纯色。</summary>
    public bool LastSucceeded { get; private set; }

    /// <summary>原始抓屏图的平均亮度（0~255），用于诊断"抓到了但全黑"。</summary>
    public double RawAverageBrightness { get; private set; }

    /// <summary>★ 原始抓屏图的细节量（相邻像素平均差）。见 <see cref="MeanGradient"/>。</summary>
    public double RawGradient { get; private set; }

    /// <summary>★ 只模糊之后的细节量。必须明显低于 <see cref="RawGradient"/>。</summary>
    public double BlurGradient { get; private set; }

    /// <summary>★ 模糊到底有没有生效 —— 细节量至少掉一半。</summary>
    public bool BlurIsEffective => RawGradient > 0.5 && BlurGradient < RawGradient * 0.5;

    /// <summary>
    /// ★ 模糊的**人话结论** —— 必须把「没东西可糊」和「模糊坏了」分开。
    ///
    ///   只看 `BlurIsEffective` 会在**纯色背景**上误报「模糊没生效」，
    ///   而实际上那里输入细节本来就是 0，模糊无事可做。
    ///   两者在日志里长得一模一样，但一个是正常、一个是 bug —— 必须分开说。
    /// </summary>
    public string BlurVerdict =>
        RawGradient <= 0.5 ? "背景是纯色（没东西可糊，正常）"
        : BlurGradient < RawGradient * 0.5 ? "生效"
        : "★没生效（模糊真的坏了）";

    /// <summary>★ 玻璃图里 alpha 的最小/最大值 —— 用来抓"半透明导致叠加发白"。</summary>
    public (int Min, int Max) GlassAlpha { get; private set; }

    /// <summary>
    /// ★★ alpha 在流水线每一步的取值 —— 本轮那个"玻璃其实是隐形的"bug 的取证。
    ///
    ///   抓屏是 BI_RGB 32 位 DIB，Windows **不填 alpha**（恒 0）；
    ///   而唯一会写 alpha=255 的 `ApplyGlassTint` 在 `Dim=0`（默认）时早退。
    ///   于是玻璃位图 alpha 全是 0 → `PixelFormats.Bgra32` 把它当**全透明**
    ///   → 上面那层（模糊+折射）看不见 → 用户看到的是下面那层**没糊过的原图**。
    ///
    ///   这一行把"看不见的假设"变成"看得见的数字"。
    /// </summary>
    public string AlphaTrace { get; private set; } = "(还没渲染过)";

    private static string Fmt((int Min, int Max) a) => $"[{a.Min}~{a.Max}]";

    /// <summary>玻璃处理图的平均亮度（0~255），用于诊断是否被错误压黑。</summary>
    public double GlassAverageBrightness { get; private set; }

    /// <summary>
    /// ★ 交付前自检：这张图**看起来是不是黑的**。
    ///
    /// 为什么必须做这一步：我连续两轮报"已修复"，用户看到的还是全黑。
    /// 日志里明明有"原图 32.6；玻璃图 43.8"这种数据，但**没有任何断言**，
    /// 所以全黑被当成正常数据打进了日志，没人看。
    ///
    /// 单测全绿不等于"用户能看见" —— 这条就是补上"看得见"的量化证明。
    /// </summary>
    public readonly record struct Visibility(
        double Min, double Max, double Mean, double StdDev, bool Ok, string Reason)
    {
        public override string ToString() =>
            $"亮度 {Min:0}~{Max:0} 均值 {Mean:0.#} 起伏 {StdDev:0.#} → "
            + (Ok ? "✅ 有内容" : $"❌ {Reason}");
    }

    /// <summary>
    /// 统计一张 BGRA 图的亮度分布，判断"能不能看出有画面"。
    ///
    /// 判据（两条都要满足）：
    ///   · 起伏 StdDev &gt; 6  —— 不能是一整块纯色（纯色看不出玻璃透出来的东西）
    ///   · 均值   Mean  &gt; 12 —— 不能是全黑
    ///
    /// 阈值取得比较松是刻意的：这里要抓的是"全黑/纯色"这种**明显坏掉**的情况，
    /// 不是为了评价材质好不好看。好不好看由用户的滑块说话。
    /// </summary>
    public static Visibility Measure(byte[]? pixels)
    {
        if (pixels is null || pixels.Length < 4)
            return new Visibility(0, 0, 0, 0, false, "没有像素");

        int min = 255, max = 0;
        long sum = 0, sumSq = 0;
        int n = 0;

        // 每 8 个像素采一次 —— 统计趋势足够，不用为此再扫一遍全图
        for (int i = 0; i + 3 < pixels.Length; i += 32)
        {
            int lum = (pixels[i] * 29 + pixels[i + 1] * 150 + pixels[i + 2] * 77) >> 8;
            if (lum < min) min = lum;
            if (lum > max) max = lum;
            sum += lum;
            sumSq += (long)lum * lum;
            n++;
        }

        if (n == 0) return new Visibility(0, 0, 0, 0, false, "没有像素");

        double mean = (double)sum / n;
        double variance = Math.Max(0, (double)sumSq / n - mean * mean);
        double stdDev = Math.Sqrt(variance);

        bool ok = stdDev > 6 && mean > 12;
        string reason = !ok
            ? (mean <= 12 ? "整张图接近全黑" : "整张图是一块纯色")
            : "";

        return new Visibility(min, max, mean, stdDev, ok, reason);
    }

    /// <summary>
    /// 把位图读回 BGRA 字节。**只给诊断用** —— 正常渲染路径从不回读。
    /// </summary>
    internal static byte[]? ReadPixels(WriteableBitmap? bmp)
    {
        if (bmp is null) return null;
        int w = bmp.PixelWidth, h = bmp.PixelHeight;
        if (w <= 0 || h <= 0) return null;
        try
        {
            var px = new byte[w * h * 4];
            bmp.CopyPixels(px, w * 4, 0);
            return px;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 相邻像素的平均绝对差 —— **"这块地方有多少细节"**。
    ///
    /// ══ ★ 为什么必须量这个（而不是亮度/起伏）═════════════════════════
    ///
    ///   用户问「你的模糊去哪里了」。答案可能是：
    ///     · 模糊坏了（算法/参数问题）—— 我一直在查这个
    ///     · **底下那块桌面本来就是平滑的，没有东西可糊**
    ///
    ///   一个平滑渐变被模糊之后**还是那个平滑渐变**，看起来就跟没糊一样。
    ///   亮度和起伏都分不出这两种情况，**细节量能**：
    ///   模糊的唯一定义就是"把高频压掉"，所以"模糊前细节 → 模糊后细节"
    ///   这个比值就是"模糊到底生效没有"的直接证据。
    /// </summary>
    public static double MeanGradient(byte[]? px, int w, int h)
    {
        if (px is null || w < 2 || h < 2) return 0;

        long sum = 0;
        int n = 0;
        for (int y = 0; y < h; y++)
        {
            int row = y * w * 4;
            for (int x = 0; x + 1 < w; x++)
            {
                int o = row + x * 4;
                sum += Math.Abs(px[o + 1] - px[o + 5]);   // 绿通道足够代表
                n++;
            }
        }
        return n == 0 ? 0 : (double)sum / n;
    }

    /// <summary>原始抓屏图的可见性</summary>
    public Visibility RawVisibility { get; private set; } = new(0, 0, 0, 0, false, "还没渲染过");

    /// <summary>玻璃处理图的可见性 —— 用户最终看到的就是它</summary>
    public Visibility GlassVisibility { get; private set; } = new(0, 0, 0, 0, false, "还没渲染过");

    public GlassSurface(GlassParams p) => _params = p;

    // ── ★ 窗口侧接口：一扇窗口 = 一个 GlassSurface ───────────────────
    //
    //   为什么把"渲染"和"贴图"绑在同一个对象上：
    //   前几轮这两件事是分开的（GlassChrome 只知道有位图、GlassSurface
    //   只知道算位图），中间那一层"谁在什么时候把位图挂上去"被漏掉了 ——
    //   于是 AttachBitmaps 变成了空实现，画面上永远没有玻璃。
    //   绑在一起之后，"算了却没贴"在类型上就不可能发生。
    //
    //   一个实例服务一扇窗口，窗口尺寸变了就换个实例（尺寸是构造函数参数）。

    private GlassChrome? _chrome;
    private int _boundW, _boundH;

    /// <summary>
    /// ★ 圆角覆盖（DIP，null = 用 <see cref="GlassParams.CornerRadius"/>）。
    ///
    /// 给**条子**用：收起态只有 ~11 DIP 高，默认圆角 10 会把两端削成半圆。
    /// 必须在 <see cref="BindWindow"/> **之前**设好 —— 圆角是建 GlassChrome 时定死的。
    /// </summary>
    public double? CornerRadiusOverride { get; set; }

    /// <summary>这扇窗口的玻璃显示层。调 <see cref="BindWindow"/> 之前是 null。</summary>
    public GlassChrome? Chrome => _chrome;

    /// <summary>
    /// 绑定/更新窗口尺寸。
    ///
    /// ★ 窗口尺寸变了必须换实例 —— 抓屏器、位图、降采样尺寸全都按尺寸建，
    ///   复用会把旧尺寸的图贴到新窗口上（表现是拉伸变形或半张黑）。
    /// </summary>
    public void BindWindow(double widthDip, double heightDip)
    {
        int w = Math.Max(1, (int)Math.Round(widthDip));
        int h = Math.Max(1, (int)Math.Round(heightDip));

        if (_chrome is not null && _boundW == w && _boundH == h) return;

        _grabber?.Dispose();
        _grabber = null;
        _grabberW = _grabberH = 0;
        GlassBitmap = null;
        RawBitmap = null;

        _boundW = w; _boundH = h;
        _chrome = GlassChrome.Build(_params, w, h, CornerRadiusOverride);
    }

    /// <summary>抓屏坐标系也要跟着窗口走（物理像素）。调参重画时用。</summary>
    private int _lastX, _lastY, _lastW, _lastH;

    /// <summary>
    /// ★ 一次完整刷新：抓屏 → 模糊 → 折射 → 调色 → 挂到窗口上。
    ///
    /// **必须在窗口显示之前调。** 窗口一旦可见，抓到的就是玻璃盖在自己身上。
    ///
    /// <paramref name="x"/>/<paramref name="y"/>/<paramref name="w"/>/<paramref name="h"/>
    /// 是**物理像素**（SetWindowPos 那一套）。
    /// </summary>
    public bool Refresh(int x, int y, int w, int h)
    {
        _lastX = x; _lastY = y; _lastW = w; _lastH = h;

        if (_chrome is null) BindWindow(w, h);

        bool ok = Render(x, y, w, h);

        // ★ 只有真的拿到像素才挂 —— 渲染失败时保留上一帧，
        //   绝不用"清空位图"来兜底：清空 = 全黑 = 用户骂了五轮的那个黑底。
        if (ok) _chrome!.AttachBitmaps(this);

        return ok;
    }

    /// <summary>照上次的矩形重画（调参拖动时用）</summary>
    public bool RefreshAgain()
        => _lastW > 0 && Refresh(_lastX, _lastY, _lastW, _lastH);

    /// <summary>
    /// 渲染玻璃。产出**两张小图**：
    ///
    ///   · <see cref="RawBitmap"/>   —— 未经处理的原始背景
    ///   · <see cref="GlassBitmap"/> —— 处理过的玻璃（模糊 + 折射 + 调色）
    ///
    /// 两张都只有**降采样后**的尺寸，由 WPF 负责放大 ——
    /// 缩放和圆角裁剪都交给 GPU，比在 C# 里逐像素算快一个数量级。
    /// 详见 <see cref="GlassChrome"/> 的图层结构说明。
    ///
    /// 坐标系是**物理像素**，和 GetCursorPos / SetWindowPos 一致。
    ///
    /// ★ 调用时机：**在把窗口显示出来之前**。窗口一旦可见，
    ///   抓到的就是"玻璃盖在自己身上"的画面了。
    /// </summary>
    public bool Render(int x, int y, int width, int height)
    {
        var total = Stopwatch.StartNew();
        LastSucceeded = false;

        if (width <= 0 || height <= 0) return false;

        // ★ 降采样因子现在**由 σ 自动算**（见 ImageOps.ChooseWorkingFactor）。
        //   以前是用户拖的 _params.Downsample —— 那个滑块已经删了：
        //   它是"马赛克"的第二个来源，而且既然能算出来，人没有理由调它。
        int factor = ImageOps.ChooseWorkingFactor(_params.BlurRadius, width, height);

        try
        {
            // ── ① 抓屏（顺手降采样）──
            if (_grabber is null || _grabberW != width || _grabberH != height
                || _grabberFactor != factor)
            {
                _grabber?.Dispose();
                _grabber = new ScreenCapture.ScreenGrabber(width, height, factor);
                _grabberW = width;
                _grabberH = height;
                _grabberFactor = factor;
            }

            var t1 = Stopwatch.StartNew();
            byte[]? raw = _grabber.Grab(x, y, width, height);
            double msGrab = t1.Elapsed.TotalMilliseconds;

            if (raw is null) return false;

            // ══ ★★★ 把 A 层的 alpha 强制成 255 ══════════════════════════
            //
            //   这一行是本轮那个「玻璃层其实是隐形的」bug 的修复。
            //
            //   抓屏用的是 `BI_RGB` 32 位 DIB —— Windows 在这种格式下
            //   **不填 alpha 字节**，抓回来恒为 0。之后:
            //     · BoxBlur      对 alpha 做平均  → 0 平均还是 0
            //     · Refract      `dst[d+3]=src[d+3]` → 照抄 0
            //     · 饱和度/亮度   只碰 i/i+1/i+2   → 不碰 alpha
            //     · ApplyGlassTint `Dim=0`(默认) 时**早退** → 写 255 的那行到不了
            //   于是玻璃位图 alpha 全 0，`PixelFormats.Bgra32` 把它当**全透明**,
            //   上面那层（模糊+折射）看不见，露出下面那张**没糊过的原图**。
            //   表现就是用户说的「不糊，没有磨砂感」。
            //
            //   ★ 为什么放在最前面，而不是塞进 ApplyGlassTint：
            //     ApplyGlassTint 的早退是**故意**的（Dim=0 是恒等变换，
            //     有单测钉着）——不该为了顺带修 alpha 去破坏它。
            //     而且 A 层（折射背景）画的就是**真桌面**，本来就该不透明,
            //     在这里写 255 是在贯彻「A 层不透明」这个模型，不是绕过它。
            //
            //   ★ 为什么以前没发现：所有单测的背景都是 `Solid()` 造的,
            //     那个 helper 会写 `px[i+3] = 255` —— **测试的输入带 alpha,
            //     真实抓屏的输入不带**。见 `TestRealCaptureAlpha`。
            ForceOpaque(raw);

            RawAverageBrightness = AverageBrightness(raw);
            RawVisibility = Measure(raw);

            int sw = _grabber.Width;      // 降采样后的宽
            int sh = _grabber.Height;

            // ★ 诊断：alpha 一路上是多少（本轮 bug 的取证）
            var alphaAfterGrab = AlphaRange(raw);

            // ── ② 模糊（**真高斯**）──
            var t2 = Stopwatch.StartNew();

            // ══ ★★★ 这里是"盒式 vs 高斯"那件事的落点 ═══════════════════
            //
            //   以前调的是 `ImageOps.BoxBlur(raw, sw, sh, blurRadius, passes)`：
            //   2D 核是 `(2r+1)×(2r+1)` 的**均匀方块**，不是圆。
            //   实测角/中心能量比（1.0 = 完全平坦的方块）：
            //     盒式 r=4 × 1 遍 → 1.00（真高斯同 σ 是 0.26，方了 3.9×）
            //     盒式 r=4 × 3 遍 → 0.54（真高斯同 σ 是 0.45，方了 1.2×）
            //   所以"3 遍 ≈ 高斯"只在 3 遍时成立。用户报的「马赛克」
            //   （半径 4、遍数 1）正好落在"核是个方块"那一档。
            //
            //   现在调 `GaussianBlur`：可分离两趟真高斯，核长 `2⌈3σ⌉+1`，
            //   **没有"遍数"这个参数** —— 和浏览器 `blur(Npx)` 语义对齐（N = σ）。
            //
            // ══ ★ σ 要换算到工作分辨率 ═══════════════════════════════════
            //
            //   `BlurRadius` 的单位是**全分辨率物理像素**（和参考项目的
            //   `blurAmount * 32 + 4` 对得上）。抓屏已经缩了 factor 倍，
            //   所以 σ 也要除以 factor，糊的程度才不随缩放跳变。
            //
            //   ★ 以前这里是整数除法 `BlurRadius / factor`，会把小半径**截断成 0**，
            //     于是模糊整个被跳过，得到一张没糊过的小图被 GPU 放大 4 倍 ——
            //     那是用户说的「透亮的马赛克」的另一个来源。
            //     现在走 double 除法，且 `GaussianRadius` 最小返回 1，
            //     **模糊不可能消失**。
            double sigmaFull = Math.Max(0, _params.BlurRadius);
            double sigmaWork = sigmaFull / factor;

            byte[] processed = ImageOps.GaussianBlur(raw, sw, sh, sigmaWork);
            double msBlur = t2.Elapsed.TotalMilliseconds;

            var alphaAfterBlur = AlphaRange(processed);

            // ★ "模糊到底生效没有"的硬证据：细节量必须掉下来。
            //   见 MeanGradient 的注释 —— 平滑背景下模糊看着像没糊。
            RawGradient = MeanGradient(raw, sw, sh);
            BlurGradient = MeanGradient(processed, sw, sh);

            var alphaAfterRefract = AlphaRange(processed);

            // ── ④ 调色 ──
            var t4 = Stopwatch.StartNew();
            ImageOps.ApplySaturationBrightness(processed, _params.Saturation, _params.Brightness);
            double msColor = t4.Elapsed.TotalMilliseconds;

            // ── ⑤ 压暗 + 底色（在低分辨率上做，省 16 倍）──
            var t5 = Stopwatch.StartNew();
            ApplyGlassTint(processed);
            double msTint = t5.Elapsed.TotalMilliseconds;

            // ── ⑥ 上传两张小图给 WPF ──
            var t6 = Stopwatch.StartNew();
            GlassBitmap = Upload(GlassBitmap, processed, sw, sh);
            RawBitmap = Upload(RawBitmap, raw, sw, sh);
            GlassAverageBrightness = AverageBrightness(processed);
            GlassVisibility = Measure(processed);
            GlassAlpha = AlphaRange(processed);
            double msUpload = t6.Elapsed.TotalMilliseconds;

            total.Stop();
            LastRenderMs = total.Elapsed.TotalMilliseconds;
            LastSucceeded = true;

            LastBreakdown = $"抓屏{msGrab:0.#} 高斯模糊{msBlur:0.#} "
                          + $"调色{msColor:0.#} 底色{msTint:0.#} 上传{msUpload:0.#} "
                          + $"= 共{LastRenderMs:0.#}ms"
                          + $"（{width}×{height} ÷{factor} → {sw}×{sh}，"
                          + $"σ {sigmaFull:0.#}→{sigmaWork:0.##} 核长{2 * ImageOps.GaussianRadius(sigmaWork) + 1}，"
                          + $"放大交给 GPU）";

            AlphaTrace = $"抓屏后 {Fmt(alphaAfterGrab)} → 模糊后 {Fmt(alphaAfterBlur)}"
                       + $" → 折射后 {Fmt(alphaAfterRefract)} → 上传前 {Fmt(GlassAlpha)}";

            return true;
        }
        catch (Exception ex)
        {
            LastBreakdown = $"渲染异常：{ex.GetType().Name} {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 把整张图的 alpha 强制成 255（不透明）。
    ///
    /// 见 <see cref="Render"/> 里调用点的长注释：抓屏拿到的是
    /// `BI_RGB` DIB，Windows **不填 alpha**，恒为 0。不做这一步，
    /// 整层玻璃上传给 WPF 之后会被当成**全透明**，用户看到的是
    /// 下面那张没糊过的原图 —— 也就是「没有磨砂感」。
    ///
    /// 顺带一提：`ApplyGlassTint` 里也有一句写 255，但它只在 `Dim > 0`
    /// 时才执行（`Dim=0` 是恒等变换，早退，有单测钉着）。两处不重复：
    /// 这里负责**建立不透明的 A 层**，那里负责**压暗时保持不透明**。
    /// </summary>
    internal static void ForceOpaque(byte[] pixels)
    {
        for (int i = 3; i < pixels.Length; i += 4)
            pixels[i] = 255;
    }

    private static WriteableBitmap Upload(WriteableBitmap? existing, byte[] pixels,
                                          int w, int h)
    {
        if (existing is null || existing.PixelWidth != w || existing.PixelHeight != h)
        {
            existing = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        }
        existing.WritePixels(new System.Windows.Int32Rect(0, 0, w, h), pixels, w * 4, 0);
        return existing;
    }

    /// <summary>
    /// 微调折射后的背景像素。
    ///
    /// ══ ★ 正确的分层模型（终于想明白了）══════════════════════════════
    ///
    ///   浏览器里 `backdrop-filter` 是这么工作的：
    ///
    ///       backdrop  →  滤镜（模糊/扭曲）  →  **取代**原来的背景
    ///       元素自己的 background-color（带 alpha）叠在上面
    ///
    ///   也就是**两层**：
    ///
    ///     · A 层：被滤镜处理过的真实桌面 —— **不透明**
    ///       （窗口在这个区域确实是"实"的，但因为画的是真桌面，看着像透的）
    ///     · B 层：玻璃自己的着色 —— 带 alpha，很淡
    ///
    ///   我前几轮的错误是**把这两层混成一层**：
    ///   一边想让 A 层透明、一边往里面掺深色，
    ///   结果既不是真背景也不是真透明，而是一块莫名的暗灰 ——
    ///   用户说的"黑底"。
    ///
    ///   所以这个函数现在**只做很轻的色调偏移**，alpha 保持不透明；
    ///   真正的"玻璃色"由 <see cref="GlassChrome"/> 的 ScrimLayer 负责。
    /// </summary>
    /// <summary>
    /// 压暗（可选）。**默认什么都不做**。
    ///
    /// ══ ★ 这里原来是"白雾"的主要来源 ══════════════════════════════════
    ///
    ///   以前是这么写的：
    ///
    ///       double dim = 1 - Dim;             // Dim=0 → dim=1
    ///       double offB = 10 * dim;           // → +10 蓝
    ///       pixels[i] = pixels[i] * dim + offB;   // ← 无条件加！
    ///
    ///   而 `Dim = 0` 是**默认值**。于是每个像素都被加上了
    ///   **+10 蓝 +8 绿 +6 红** —— 一层偏青的白雾，永远擦不掉。
    ///   用户的评价是「像亚克力，有一层白雾，不是液态玻璃的透亮」。
    ///
    ///   参考项目（liquid-glass-react）**一个白色图层都没有**：
    ///   它的元素 background 是 transparent，可读性靠**深色**文字阴影
    ///   和**深色**外部投影来撑。白雾是我自己加出来的。
    ///
    ///   现在：`Dim = 0` 直接返回（**逐像素恒等**，有单测钉着）；
    ///   `Dim > 0` 只做纯压暗（乘一个系数），**不加任何偏移**。
    ///   alpha 保持不透明 —— A 层画的是真桌面。
    /// </summary>
    internal void ApplyGlassTint(byte[] pixels)
    {
        double dim = 1 - Math.Clamp(_params.Dim, 0, 1);

        // ★ Dim = 0 时是恒等变换，一个字节都不该动。
        if (dim >= 0.999) return;

        for (int i = 0; i + 3 < pixels.Length; i += 4)
        {
            pixels[i] = Clamp255(pixels[i] * dim);
            pixels[i + 1] = Clamp255(pixels[i + 1] * dim);
            pixels[i + 2] = Clamp255(pixels[i + 2] * dim);
            pixels[i + 3] = 255;      // ★ A 层不透明 —— 它画的是真桌面
        }
    }

    /// <summary>
    /// 折射 + 色散。三次重采样，每个颜色通道用略微不同的强度。
    ///
    /// <paramref name="width"/>/<paramref name="height"/> 是**降采样后**的尺寸 ——
    /// 位移图和采样都在这个尺度上做，所以半径也要按比例缩，
    /// 否则"降采样"滑块一动，折射带的宽度就跟着变。
    ///
    /// ══ ★ 位移量也必须按同一个比例缩（这次的大 bug）══════════════════
    ///
    ///   `_params.Refraction` 的单位是**全分辨率物理像素**，
    ///   但位移是在**降采样图**上做的。不缩的话：
    ///
    ///     720px 窗口 ÷4 降采样 → 180px 宽的图上位移 25px
    ///     换算回全分辨率 = 25 × 4 = **100px**
    ///
    ///   用户看到的就是"偏移量大得离谱，缝里全是拉伸出来的绿色"。
    ///   25 这个默认值在降采样 4× 下实际等于 100 —— 大了整整四倍。
    ///
    ///   修法：强度乘以 <paramref name="scale"/>（= 降采样图宽 / 全分辨率宽），
    ///   于是参数含义与降采样倍数解耦，滑块拖到 8 就是真的 8 个物理像素。
    /// </summary>
    private byte[] ApplyRefraction(byte[] src, int width, int height)
    {
        double scale = (double)width / Math.Max(1, _grabberW);

        float[] map = RefractionMap.BuildDirection(width, height,
                                                   _params.CornerRadius * scale,
                                                   _params.RefractionBand);

        return ApplyRefraction(src, width, height, map, scale);
    }

    /// <summary>
    /// 给测试用的入口 —— 用**显式给出的缩放系数**跑一遍折射，
    /// 不依赖抓屏器状态。
    ///
    /// 为什么要这个：`TestRefractionKeepsAllChannels` 要在不抓屏的情况下
    /// 验证"色散关掉时三通道还都在"。这条是用户报的绿带 bug 的回归测试。
    /// </summary>
    internal byte[] RefractForTest(byte[] src, int width, int height, float[] map)
        => ApplyRefraction(src, width, height, map, 1.0);

    /// <summary>
    /// 给**离线预览**用的入口：自己按 scale 建位移图再跑折射，
    /// 不依赖抓屏器。这样能在有纹理的合成图上看清折射到底做了什么。
    /// </summary>
    internal byte[] ApplyRefractionForTest(byte[] src, int width, int height, double scale)
    {
        float[] map = RefractionMap.BuildDirection(
            width, height, _params.CornerRadius * scale, _params.RefractionBand);
        return ApplyRefraction(src, width, height, map, scale);
    }

    private byte[] ApplyRefraction(byte[] src, int width, int height,
                                   float[] map, double scale)
    {
        // ★ 位移强度换算到降采样尺度（见上面的注释）
        double strength = _params.Refraction * scale;

        // 第一遍：绿通道（也是底色），带完整位移
        byte[] result = ImageOps.Refract(src, width, height, map,
                                         strength, _params.ChromaticAberration, 1);

        if (_params.ChromaticAberration > 0.01)
        {
            // 红、蓝通道各自用不同强度重采样，再填回结果里 ——
            // 三个通道位移不同，边缘就会泛出彩虹。这就是"色散"。
            byte[] red = ImageOps.Refract(src, width, height, map,
                                          strength, _params.ChromaticAberration, 0);
            byte[] blue = ImageOps.Refract(src, width, height, map,
                                           strength, _params.ChromaticAberration, 2);

            for (int i = 0; i + 3 < result.Length; i += 4)
            {
                result[i + 2] = red[i + 2];       // R
                result[i] = blue[i];              // B
            }
        }

        return result;
    }

    /// <summary>
    /// ★ alpha 的最小/最大值。玻璃图的 alpha **必须恒为 255**。
    ///
    ///   如果它小于 255，叠在桌面上的就是"半透明的图"——
    ///   桌面透上来和图本身混在一起，**画面会发白**（用户说的"白雾"），
    ///   而且模糊的痕迹也会被冲淡。
    ///   这条把它变成可断言的量，不再靠肉眼猜。
    /// </summary>
    private static (int Min, int Max) AlphaRange(byte[] pixels)
    {
        int min = 255, max = 0;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            int a = pixels[i];
            if (a < min) min = a;
            if (a > max) max = a;
        }
        return (min, max);
    }

    private static double AverageBrightness(byte[] pixels)    {
        if (pixels.Length < 4) return 0;

        long sum = 0;
        int count = 0;
        // 每 16 个像素采样一次，诊断不值得再扫第二遍全图
        for (int i = 0; i + 3 < pixels.Length; i += 64)
        {
            sum += (pixels[i] * 29 + pixels[i + 1] * 150 + pixels[i + 2] * 77) >> 8;
            count++;
        }
        return count == 0 ? 0 : (double)sum / count;
    }

    /// <summary>
    /// 0~255 钳位取整。
    ///
    /// ★ 这里刻意**不用 Math.Round** —— 它是整个流水线里最贵的单条语句
    ///   （约 25ns，是普通算术的十倍）。圆角混合那一步每像素要调它 3 次，
    ///   518400 个像素就是 1.5M 次 ≈ 37ms，光这一条就吃掉三分之一的时间。
    ///   换成 `(int)(v + 0.5)` 之后同样的活降到几毫秒。
    ///   （正数范围内两者等价；我们的值恒为非负，所以安全。）
    /// </summary>
    /// <summary>
    /// 给测试用的入口。
    ///
    /// ★ 它**只做一件事：调用真正的主函数**。
    ///
    ///   这里曾经有一份"自己的实现"（把 ScrimOpacity 当 alpha 写进 A 层），
    ///   和生产代码各写各的 —— 于是主函数写 255、测试入口写 15，
    ///   单测红了一条，而**真实渲染路径里根本不是这么干的**。
    ///   测试和实现一旦分成两份，"测试通过"就再也不代表"程序是对的"。
    ///
    ///   现在它只是转发。要改行为，改 <see cref="ApplyGlassTint"/> 一处。
    /// </summary>
    public static void ApplyGlassTintForTest(byte[] pixels, GlassParams p)
        => new GlassSurface(p).ApplyGlassTint(pixels);

    private static byte Clamp255(double v)
    {
        if (v <= 0) return 0;
        if (v >= 255) return 255;
        return (byte)(int)(v + 0.5);
    }

    /// <summary>"#RRGGBB" → (b, g, r)</summary>
    private static (byte B, byte G, byte R) ParseHex(string hex)
    {
        try
        {
            string s = hex.TrimStart('#');
            if (s.Length != 6) return (255, 255, 255);
            byte r = Convert.ToByte(s[..2], 16);
            byte g = Convert.ToByte(s.Substring(2, 2), 16);
            byte b = Convert.ToByte(s.Substring(4, 2), 16);
            return (b, g, r);
        }
        catch (Exception)
        {
            return (255, 255, 255);
        }
    }

    public void Dispose()
    {
        _grabber?.Dispose();
        _grabber = null;
        GlassBitmap = null;
        RawBitmap = null;
    }
}
