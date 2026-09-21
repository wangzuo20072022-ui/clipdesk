using System;
using System.Collections.Generic;

namespace S2Strip;

/// <summary>
/// 折射位移图 —— **液态玻璃的灵魂**。
///
/// ══ 这张图是干什么的 ════════════════════════════════════════════
///
/// 普通毛玻璃：把背景糊掉。
/// 液态玻璃：把背景糊掉，**并且让边缘像透镜一样把背景放大扭曲**。
/// 差别就在这个"边缘扭曲"上 —— 没有它，看上去只是一块雾。
///
/// 做法：算一张和窗口一样大的图，每个像素存一个 `(dx, dy)` 位移量。
/// 贴图的时候不取 `(x, y)` 处的背景，而取 `(x+dx, y+dy)` 处的 ——
/// 边缘的位移指向中心，于是边缘看到的是**被放大的中心区域**，透镜感就出来了。
///
/// ══ 算法出处 ════════════════════════════════════════════════════
///
/// 移植自 GitHub 上的 `liquid-glass-react`（npm 同名包，MIT）。
/// 它的核心是一个跑在 canvas 上的函数，逐像素算圆角矩形的有符号距离场：
///
///     roundedRectSDF(x, y, w, h, r)
///     disp   = smoothStep(0.8, 0, distanceToEdge - 0.15)
///     scaled = smoothStep(0, 1, disp)          // 0..1
///     输出   = 输入坐标 × scaled
///
/// `scaled` 越小 → 采样点越被拉向中心 → 边缘放大越厉害。
/// 中心区域 `scaled ≈ 1` → 原样透过。
///
/// 我做了两处**有意**的改动：
///
///   ① 它把结果编码成一张 8 位图片再交给浏览器的 `feDisplacementMap` 解码。
///      我们不需要过这一道 —— 直接留 `float`，少一次量化误差。
///
///   ② 它把 `displacementScale`（强度）和位移方向存在一起。
///      我拆开了：这里只存**方向**（模长 ≤ 1），强度由调用方乘。
///      这样拖动强度滑块时不用重算整张图 —— 滑起来才是实时的。
///
/// ══ 为什么可以缓存 ══════════════════════════════════════════════
///
/// 这张图**只跟窗口尺寸和圆角有关，和背景内容无关**。
/// 所以同一个尺寸只算一次，之后一直复用。
/// </summary>
internal static class RefractionMap
{
    /// <summary>位移图的缓存键：尺寸 + 圆角</summary>
    private static readonly Dictionary<(int W, int H, int R), float[]> Cache = new();

    /// <summary>
    /// 折射带占**半边长**的比例。
    ///
    /// ★ 这个参数是整个效果里最反直觉的一个，必须解释清楚。
    ///
    ///   有符号距离场算出来的 `d` 在元素**内部处处为负**（中心最负，边界为 0）。
    ///   如果直接拿它去套苹果那条斜坡 `smoothStep(0.8, 0, d - 0.15)`，
    ///   结果是 `d` 永远够不到 0.15 那个门槛 → 位移恒为 0 → **整个折射失效**。
    ///
    ///   （我第一版就是这么写的，算完发现整张图全是 0。原因是我照着
    ///     抄了参数，却没注意参考实现里那个"圆角矩形"是**比元素本身小一圈**的 ——
    ///     它故意把形状缩进去，好让 `d` 有从负走到正的空间。）
    ///
    ///   所以这里显式地把 SDF 形状**内缩**一圈，内缩的量就是折射带的宽度。
    ///   0.25 = 折射带占半边长度的 25%。
    /// </summary>
    public const double DefaultBandFraction = 0.25;

    /// <summary>
    /// 算（或取出缓存的）**方向图**。长度 = 宽×高×2，依次是 dx, dy。
    ///
    /// 模长归一化到 ≤ 1：最大值处恰好是 1。真正的像素位移 = 这个值 × 强度。
    /// </summary>
    public static float[] BuildDirection(int width, int height, double cornerRadiusPx,
                                         double bandFraction = DefaultBandFraction)
    {
        if (width <= 0 || height <= 0) return Array.Empty<float>();

        int r = (int)Math.Round(Math.Clamp(cornerRadiusPx, 0, Math.Min(width, height) / 2.0));
        int band = (int)Math.Round(bandFraction * 1000);
        var key = (width, height, r * 1000 + band);

        if (Cache.TryGetValue(key, out var cached)) return cached;

        float[] map = Compute(width, height, r, bandFraction);
        Cache[key] = map;
        return map;
    }

    /// <summary>缓存了多少个尺寸（日志用）</summary>
    public static int CachedCount => Cache.Count;

    public static void ClearCache() => Cache.Clear();

    /// <summary>
    /// 真正的计算。抽出来是为了让测试能直接验一个具体尺寸，
    /// 不受缓存影响。
    /// </summary>
    public static float[] Compute(int width, int height, double cornerRadiusPx,
                                  double bandFraction = DefaultBandFraction)
    {
        double halfW = width / 2.0;
        double halfH = height / 2.0;

        // 圆角不能超过短边的一半，否则 SDF 会算出负数半径那种怪东西
        double radius = Math.Clamp(cornerRadiusPx, 0, Math.Min(halfW, halfH));

        // ★ 折射带宽度（像素）。SDF 形状要从元素边界**内缩**这么多，
        //   这样 d 才有从负走到正的空间。上限 45% 是为了极小元素不崩。
        double inset = Math.Clamp(bandFraction, 0.02, 0.45) * Math.Min(halfW, halfH);

        // 内缩之后的 SDF 盒子
        double boxW = Math.Max(0.5, halfW - inset);
        double boxH = Math.Max(0.5, halfH - inset);
        double boxR = Math.Max(0, radius - inset);

        var raw = new float[width * height * 2];
        float maxScale = 0f;

        // ── 第一遍：算原始位移，顺便找出最大值（归一化要用）──
        for (int y = 0; y < height; y++)
        {
            double py = y + 0.5 - halfH;

            for (int x = 0; x < width; x++)
            {
                double px = x + 0.5 - halfW;

                double d = RoundedBoxSdf(px, py, boxW, boxH, boxR);

                // 用折射带宽度归一化：d = 0 在带的内边界，d = inset 在元素边界
                double dn = d / inset;

                double disp = SmoothStep(0.8, 0, dn - 0.15);
                double scaled = SmoothStep(0, 1, disp);

                // 输出坐标 = 输入坐标 × scaled  →  位移 = 输入 × (scaled - 1)
                double dx = px * (scaled - 1.0);
                double dy = py * (scaled - 1.0);

                // ★ 最外圈 2px 把位移压回 0。
                //   两个作用：
                //     1. 边缘采样不会越界（越界会被钳位，出现拉伸条纹）
                //     2. 窗口最外一圈显示的是**未处理的原始背景**，
                //        和窗口外的桌面无缝衔接 —— 所以圆角不需要透明通道
                double edgeDistance = Math.Min(Math.Min(x, y),
                                               Math.Min(width - x - 1, height - y - 1));
                double edgeFactor = Math.Min(1.0, edgeDistance / 2.0);

                dx *= edgeFactor;
                dy *= edgeFactor;

                int i = (y * width + x) * 2;
                raw[i] = (float)dx;
                raw[i + 1] = (float)dy;

                float mag = (float)Math.Max(Math.Abs(dx), Math.Abs(dy));
                if (mag > maxScale) maxScale = mag;
            }
        }

        // ── 第二遍：归一化 ──
        if (maxScale <= 0f) return raw;      // 全 0（尺寸太小/圆角太怪），原样返回

        for (int i = 0; i < raw.Length; i++) raw[i] /= maxScale;

        return raw;
    }

    /// <summary>
    /// 圆角遮罩缓存：1 = 完全在圆角矩形内（玻璃区），0 = 在外（原始背景区）。
    ///
    /// ★ 这个遮罩**只跟尺寸和圆角有关**，跟画面内容无关 ——
    ///   但第一版是每帧现算的，720×720 要 66ms。
    ///   算一次缓存起来之后，这一项就归零了。
    /// </summary>
    private static readonly Dictionary<(int W, int H, int R), float[]> MaskCache = new();

    /// <summary>
    /// 取（或算）圆角遮罩。边界 1px 做抗锯齿。
    ///
    /// 用途：窗口是实心的（`AllowsTransparency=false`），画不出真正的透明圆角，
    /// 所以圆角外面要填**未经处理的原始背景** —— 那块像素本来就和桌面一样，
    /// 人眼看上去就是"圆角处透过去了"。这个遮罩就是用来混合这两层的。
    /// </summary>
    public static float[] BuildMask(int width, int height, double cornerRadiusPx)
    {
        if (width <= 0 || height <= 0) return Array.Empty<float>();

        int r = (int)Math.Round(Math.Clamp(cornerRadiusPx, 0, Math.Min(width, height) / 2.0));
        var key = (width, height, r);

        if (MaskCache.TryGetValue(key, out var cached)) return cached;

        float[] mask = ComputeMask(width, height, r);
        MaskCache[key] = mask;
        return mask;
    }

    /// <summary>真正算遮罩。抽出来给测试用（不受缓存影响）。</summary>
    public static float[] ComputeMask(int width, int height, double cornerRadiusPx)
    {
        var mask = new float[width * height];

        double halfW = width / 2.0;
        double halfH = height / 2.0;
        double r = Math.Clamp(cornerRadiusPx, 0, Math.Min(halfW, halfH));

        for (int y = 0; y < height; y++)
        {
            double py = y + 0.5 - halfH;
            int row = y * width;

            for (int x = 0; x < width; x++)
            {
                double px = x + 0.5 - halfW;
                double d = RoundedBoxSdf(px, py, halfW, halfH, r);

                // d < 0 在内部。0.5 - d：d=0 时给 0.5（正好半透明），
                // d=-0.5 给 1，d=+0.5 给 0 → 1px 的抗锯齿过渡带
                double m = 0.5 - d;
                mask[row + x] = (float)(m < 0 ? 0 : (m > 1 ? 1 : m));
            }
        }

        return mask;
    }

    public static void ClearMaskCache() => MaskCache.Clear();

    /// <summary>圆角矩形的**有符号距离场**：负数在内部，正数在外部，0 正好在边界上。
    ///
    /// 这是图形学里的标准写法（Inigo Quilez 那个版本），
    /// 好处是圆角是**解析**算出来的，不会因为尺寸变了就出现锯齿或错位。
    /// </summary>
    public static double RoundedBoxSdf(double px, double py,
                                       double halfW, double halfH, double radius)
    {
        double qx = Math.Abs(px) - halfW + radius;
        double qy = Math.Abs(py) - halfH + radius;

        double ox = qx > 0 ? qx : 0;
        double oy = qy > 0 ? qy : 0;

        return Math.Min(Math.Max(qx, qy), 0.0)
             + Math.Sqrt(ox * ox + oy * oy)
             - radius;
    }

    /// <summary>
    /// 平滑阶跃：t 在 [a,b] 之间时返回 0→1 的平滑过渡，两端之外钳位。
    ///
    /// ★ a 可以**大于** b —— 这时是反向斜坡（b 处返回 1，a 处返回 0）。
    ///   苹果那套参数 `smoothStep(0.8, 0, …)` 用的就是这个反向用法，
    ///   写成"交换参数"就错了。
    /// </summary>
    public static double SmoothStep(double a, double b, double t)
    {
        if (Math.Abs(b - a) < 1e-12) return t < a ? 0 : 1;

        double x = Math.Clamp((t - a) / (b - a), 0, 1);
        return x * x * (3 - 2 * x);
    }
}
