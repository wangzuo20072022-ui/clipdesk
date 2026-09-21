using System;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace S2Strip;

/// <summary>
/// 玻璃表面 —— 把「抓屏 → 模糊 → 折射 → 调色 → 圆角混合」串成一条流水线，
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

    /// <summary>最近一次是否成功。失败时调用方应该退回纯色背景。</summary>
    public bool LastSucceeded { get; private set; }

    public GlassSurface(GlassParams p) => _params = p;

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

        int factor = Math.Clamp(_params.Downsample, 1, 8);

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

            int sw = _grabber.Width;      // 降采样后的宽
            int sh = _grabber.Height;

            // ── ② 模糊 ──
            //    半径按比例缩，否则"降采样"滑块一动，糊的程度会跟着跳，
            //    用户会以为模糊参数坏了。
            var t2 = Stopwatch.StartNew();
            int blurRadius = Math.Max(0, _params.BlurRadius / factor);
            byte[] processed = ImageOps.BoxBlur(raw, sw, sh, blurRadius, _params.BlurPasses);
            double msBlur = t2.Elapsed.TotalMilliseconds;

            // ── ③ 折射（液态玻璃的灵魂；默认关闭）──
            var t3 = Stopwatch.StartNew();
            if (_params.Refraction > 0.5)
            {
                processed = ApplyRefraction(processed, sw, sh);
            }
            double msRefract = t3.Elapsed.TotalMilliseconds;

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
            double msUpload = t6.Elapsed.TotalMilliseconds;

            total.Stop();
            LastRenderMs = total.Elapsed.TotalMilliseconds;
            LastSucceeded = true;

            LastBreakdown = $"抓屏{msGrab:0.#} 模糊{msBlur:0.#} 折射{msRefract:0.#} "
                          + $"调色{msColor:0.#} 底色{msTint:0.#} 上传{msUpload:0.#} "
                          + $"= 共{LastRenderMs:0.#}ms"
                          + $"（{width}×{height} ÷{factor} → {sw}×{sh}，放大交给 GPU）";

            return true;
        }
        catch (Exception ex)
        {
            LastBreakdown = $"渲染异常：{ex.GetType().Name} {ex.Message}";
            return false;
        }
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
    /// 把"半透明玻璃"的效果**烘焙进像素**。
    ///
    /// 窗口是实心的（AllowsTransparency=false），没法真透明，
    /// 所以"透明度"是靠往深色方向混出来的：
    ///     最终 = 背景 × 不透明度 + 暗底 × (1 - 不透明度)
    /// 再叠一层底色、整体压暗。
    ///
    /// 这一步在**降采样后**的图上做 —— 它逐像素但只跟颜色有关，
    /// 跟分辨率无关，所以放在低分辨率上做省 16 倍。
    /// </summary>
    private void ApplyGlassTint(byte[] pixels)
    {
        double opacity = Math.Clamp(_params.BackgroundOpacity, 0, 1);
        double dim = 1 - Math.Clamp(_params.Dim, 0, 1);
        double tintAlpha = Math.Clamp(_params.TintOpacity, 0, 1);

        (byte tb, byte tg, byte tr) = ParseHex(_params.TintColor);

        double addB = 12 * (1 - opacity) + tb * tintAlpha;
        double addG = 12 * (1 - opacity) + tg * tintAlpha;
        double addR = 12 * (1 - opacity) + tr * tintAlpha;

        for (int i = 0; i + 3 < pixels.Length; i += 4)
        {
            pixels[i] = Clamp255((pixels[i] * opacity + addB) * dim);
            pixels[i + 1] = Clamp255((pixels[i + 1] * opacity + addG) * dim);
            pixels[i + 2] = Clamp255((pixels[i + 2] * opacity + addR) * dim);
        }
    }

    /// <summary>
    /// 折射 + 色散。三次重采样，每个颜色通道用略微不同的强度。
    ///
    /// <paramref name="width"/>/<paramref name="height"/> 是**降采样后**的尺寸 ——
    /// 位移图和采样都在这个尺度上做，所以半径也要按比例缩，
    /// 否则"降采样"滑块一动，折射带的宽度就跟着变。
    /// </summary>
    private byte[] ApplyRefraction(byte[] src, int width, int height)
    {
        double scale = (double)width / Math.Max(1, _grabberW);

        float[] map = RefractionMap.BuildDirection(width, height,
                                                   _params.CornerRadius * scale,
                                                   _params.RefractionBand);

        // 第一遍：绿通道（也是底色），带完整位移
        byte[] result = ImageOps.Refract(src, width, height, map,
                                         _params.Refraction, _params.ChromaticAberration, 1);

        if (_params.ChromaticAberration > 0.01)
        {
            // 红、蓝通道各自用不同强度重采样，再填回结果里 ——
            // 三个通道位移不同，边缘就会泛出彩虹。这就是"色散"。
            byte[] red = ImageOps.Refract(src, width, height, map,
                                          _params.Refraction, _params.ChromaticAberration, 0);
            byte[] blue = ImageOps.Refract(src, width, height, map,
                                           _params.Refraction, _params.ChromaticAberration, 2);

            for (int i = 0; i + 3 < result.Length; i += 4)
            {
                result[i + 2] = red[i + 2];       // R
                result[i] = blue[i];              // B
            }
        }

        return result;
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
