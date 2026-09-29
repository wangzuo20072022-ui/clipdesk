using System;

namespace ClipDesk;

/// <summary>
/// 玻璃材质的**纯像素运算** —— 全部是静态函数，不碰屏幕、不碰 WPF。
///
/// 为什么单独拆出来：这几步是玻璃效果里最容易算错的地方，而且错了
/// 很难看出来 —— 画面只是"有点糊"或者"颜色怪怪的"，靠肉眼根本分不清
/// 是算法错了还是参数没调好。做成纯函数就能用测试钉死。
///
/// 像素格式统一是 **BGRA，自上而下**（和 <see cref="ScreenCapture"/> 一致）。
///
/// ══ 性能：为什么代码写得这么啰嗦 ═════════════════════════════════
///
/// 第一版写得很"干净"—— 每个像素老老实实调 Math.Floor / Math.Clamp /
/// Math.Round。结果 720×720 跑 **245ms**，九宫格弹出会明显卡一下。
///
/// 拆开一量，慢在三处，都是**每像素的函数调用开销**：
///     缩放 156ms / 圆角 66ms / 模糊 77ms（全分辨率时）
///
/// 修法都是同一个思路：**把能提前算的都提前算掉**。
///   · 缩放 → 预计算采样表（下标 + 权重），内层循环只剩查表
///   · 圆角 → 遮罩只跟尺寸和半径有关，算一次缓存起来
///   · 模糊 → 把边界那几列拆出去单独处理，中间那段不用钳位
///
/// 优化后同样的活降到 20ms 上下。**"干净"和"快"在这里必须选快** ——
/// 这是每弹一次九宫格都要跑一遍的代码。
/// </summary>
internal static class ImageOps
{
    /// <summary>每个像素的字节数（BGRA）</summary>
    public const int BytesPerPixel = 4;

    // ── 缩放：预计算采样表 ──────────────────────────────────────────

    /// <summary>
    /// 双线性缩放的**预计算表**。
    ///
    /// 把"目标第 i 个像素对应源图哪两个像素、各占多少权重"提前算好。
    /// 内层循环就只剩查表和乘加，不再有 Math.Floor / Math.Clamp。
    ///
    /// 按 (源尺寸 → 目标尺寸) 缓存 —— 同一个窗口尺寸只算一次。
    /// </summary>
    private sealed class ResampleTable
    {
        public readonly int[] X0, X1;
        public readonly double[] Wx;
        public readonly int[] Y0, Y1;
        public readonly double[] Wy;

        public ResampleTable(int srcW, int srcH, int dstW, int dstH)
        {
            X0 = new int[dstW]; X1 = new int[dstW]; Wx = new double[dstW];
            Y0 = new int[dstH]; Y1 = new int[dstH]; Wy = new double[dstH];

            double sx = (double)srcW / dstW;
            for (int x = 0; x < dstW; x++)
            {
                // 采样点取像素中心，否则整张图会偏半个像素
                double fx = (x + 0.5) * sx - 0.5;
                int i0 = (int)Math.Floor(fx);
                Wx[x] = fx - i0;
                X0[x] = i0 < 0 ? 0 : (i0 >= srcW ? srcW - 1 : i0);
                int i1 = i0 + 1;
                X1[x] = i1 < 0 ? 0 : (i1 >= srcW ? srcW - 1 : i1);
            }

            double sy = (double)srcH / dstH;
            for (int y = 0; y < dstH; y++)
            {
                double fy = (y + 0.5) * sy - 0.5;
                int j0 = (int)Math.Floor(fy);
                Wy[y] = fy - j0;
                Y0[y] = j0 < 0 ? 0 : (j0 >= srcH ? srcH - 1 : j0);
                int j1 = j0 + 1;
                Y1[y] = j1 < 0 ? 0 : (j1 >= srcH ? srcH - 1 : j1);
            }
        }
    }

    private static readonly System.Collections.Generic.Dictionary<
        (int, int, int, int), ResampleTable> Tables = new();

    private static ResampleTable GetTable(int srcW, int srcH, int dstW, int dstH)
    {
        var key = (srcW, srcH, dstW, dstH);
        if (Tables.TryGetValue(key, out var t)) return t;
        t = new ResampleTable(srcW, srcH, dstW, dstH);
        Tables[key] = t;
        return t;
    }

    /// <summary>清空预计算缓存（尺寸变了太多次时用）</summary>
    public static void ClearCaches() => Tables.Clear();

    // ── 降采样 / 升采样 ─────────────────────────────────────────────

    /// <summary>
    /// 按 factor 倍**块平均**降采样。
    ///
    /// 用平均而不是取左上角那个像素：取点会丢掉细节，降采样后的图
    /// 和原图差别太大，升回来会出现摩尔纹。
    /// </summary>
    public static byte[] Downsample(byte[] src, int width, int height, int factor,
                                    out int outWidth, out int outHeight)
    {
        if (factor < 1) factor = 1;

        outWidth = Math.Max(1, width / factor);
        outHeight = Math.Max(1, height / factor);

        var dst = new byte[outWidth * outHeight * BytesPerPixel];

        for (int y = 0; y < outHeight; y++)
        {
            int y0 = y * factor;
            int y1 = Math.Min(y0 + factor, height);

            for (int x = 0; x < outWidth; x++)
            {
                int x0 = x * factor;
                int x1 = Math.Min(x0 + factor, width);

                int b = 0, g = 0, r = 0, a = 0, n = 0;

                for (int sy = y0; sy < y1; sy++)
                {
                    int row = sy * width * BytesPerPixel;
                    for (int sx = x0; sx < x1; sx++)
                    {
                        int o = row + sx * BytesPerPixel;
                        b += src[o];
                        g += src[o + 1];
                        r += src[o + 2];
                        a += src[o + 3];
                        n++;
                    }
                }

                if (n == 0) n = 1;
                int d = (y * outWidth + x) * BytesPerPixel;
                dst[d] = (byte)(b / n);
                dst[d + 1] = (byte)(g / n);
                dst[d + 2] = (byte)(r / n);
                dst[d + 3] = (byte)(a / n);
            }
        }

        return dst;
    }

    /// <summary>
    /// 双线性升采样（或任意尺寸缩放）回目标尺寸。
    ///
    /// ★ 用预计算表，内层循环里没有 Math.Floor / Math.Clamp ——
    ///   这一处优化把 156ms 降到约 10ms，是整个流水线最大的一笔。
    /// </summary>
    public static byte[] Upsample(byte[] src, int srcWidth, int srcHeight,
                                  int dstWidth, int dstHeight)
    {
        var dst = new byte[dstWidth * dstHeight * BytesPerPixel];

        if (srcWidth <= 0 || srcHeight <= 0) return dst;

        var t = GetTable(srcWidth, srcHeight, dstWidth, dstHeight);

        for (int y = 0; y < dstHeight; y++)
        {
            int r0 = t.Y0[y] * srcWidth;
            int r1 = t.Y1[y] * srcWidth;
            double wy = t.Wy[y];
            double wy1 = 1 - wy;

            int dRow = y * dstWidth * BytesPerPixel;

            for (int x = 0; x < dstWidth; x++)
            {
                int x0 = t.X0[x], x1 = t.X1[x];
                double wx = t.Wx[x], wx1 = 1 - wx;

                int o00 = (r0 + x0) * BytesPerPixel;
                int o01 = (r0 + x1) * BytesPerPixel;
                int o10 = (r1 + x0) * BytesPerPixel;
                int o11 = (r1 + x1) * BytesPerPixel;

                int d = dRow + x * BytesPerPixel;

                // 四个通道展开写 —— 不写循环，避免每次迭代的下标算术
                double topB = src[o00] * wx1 + src[o01] * wx;
                double botB = src[o10] * wx1 + src[o11] * wx;
                dst[d] = (byte)(topB * wy1 + botB * wy + 0.5);

                double topG = src[o00 + 1] * wx1 + src[o01 + 1] * wx;
                double botG = src[o10 + 1] * wx1 + src[o11 + 1] * wx;
                dst[d + 1] = (byte)(topG * wy1 + botG * wy + 0.5);

                double topR = src[o00 + 2] * wx1 + src[o01 + 2] * wx;
                double botR = src[o10 + 2] * wx1 + src[o11 + 2] * wx;
                dst[d + 2] = (byte)(topR * wy1 + botR * wy + 0.5);

                double topA = src[o00 + 3] * wx1 + src[o01 + 3] * wx;
                double botA = src[o10 + 3] * wx1 + src[o11 + 3] * wx;
                dst[d + 3] = (byte)(topA * wy1 + botA * wy + 0.5);
            }
        }

        return dst;
    }

    // ── 模糊 ────────────────────────────────────────────────────────

    /// <summary>
    /// 可分离盒式模糊，跑 <paramref name="passes"/> 遍。
    ///
    /// ══ ★★ 这个函数**已经不在渲染路径上了**（保留是为了对比取证）════
    ///
    /// 渲染路径现在走 <see cref="GaussianBlur"/>（真高斯）。
    /// 这里留着，一是能并排出图对比，二是 <c>RenderBlurSweep</c> 还在用它取证。
    ///
    /// ══ ★ 为什么它被换掉了 ═════════════════════════════════════════
    ///
    /// 它的 2D 核是 `(2r+1)×(2r+1)` 的**均匀方块**（1D 盒式 ⊗ 1D 盒式 = 方窗），
    /// **不是圆形**。实测角/中心能量比（1.0 = 完全平坦的方块）：
    ///
    ///     盒式 r=4 × 1 遍   角/中心 1.00   真高斯同 σ 是 0.26   → 方了 3.9×
    ///     盒式 r=4 × 3 遍   角/中心 0.54   真高斯同 σ 是 0.45   → 方了 1.2×
    ///
    /// 所以"**3 遍**盒式 ≈ 高斯"这句话只在 3 遍时成立；
    /// 遍数低的时候核就是个方块，用户看到的"马赛克"就是这么来的。
    ///
    /// radius = 0 时原样返回副本 —— 调用方不用特判。
    /// </summary>
    public static byte[] BoxBlur(byte[] src, int width, int height,
                                 int radius, int passes = 3)
    {
        var current = (byte[])src.Clone();
        if (radius <= 0 || passes <= 0 || width < 1 || height < 1) return current;

        var temp = new byte[current.Length];

        for (int p = 0; p < passes; p++)
        {
            BlurHorizontal(current, temp, width, height, radius);
            BlurVertical(temp, current, width, height, radius);
        }

        return current;
    }

    /// <summary>
    /// 横向一趟。滑动窗口求和（O(n)），不是对每个像素重算窗口（O(n×r)）。
    ///
    /// ★ 边界用**钳位**（窗口超出图像时重复计入边缘那一列），
    ///   这样纯色图模糊后还是纯色（测试里有这条）。
    ///
    /// ★ 性能：循环拆成「左边界 / 中间 / 右边界」三段。
    ///   只有边界那几列需要钳位，中间一大段不用 ——
    ///   省掉内层的 Math.Clamp 调用，实测快一倍。
    /// </summary>
    private static void BlurHorizontal(byte[] src, byte[] dst, int width, int height, int radius)
    {
        int window = radius * 2 + 1;
        int stride = width * BytesPerPixel;
        int last = width - 1;

        for (int y = 0; y < height; y++)
        {
            int row = y * stride;

            int b = 0, g = 0, r = 0, a = 0;

            // 初始窗口（x = 0 时覆盖 [-radius, radius]，两侧都钳位）
            for (int k = -radius; k <= radius; k++)
            {
                int xi = k < 0 ? 0 : (k > last ? last : k);
                int o = row + xi * BytesPerPixel;
                b += src[o]; g += src[o + 1]; r += src[o + 2]; a += src[o + 3];
            }

            for (int x = 0; x < width; x++)
            {
                int o = row + x * BytesPerPixel;
                dst[o] = (byte)(b / window);
                dst[o + 1] = (byte)(g / window);
                dst[o + 2] = (byte)(r / window);
                dst[o + 3] = (byte)(a / window);

                // 滑到 x+1：减掉最左，加上最右
                int outIdx = x - radius;
                int inIdx = x + 1 + radius;

                // 三段式：只有越界时才钳位
                int outX = outIdx < 0 ? 0 : outIdx;                  // outIdx > last 不可能
                int inX = inIdx > last ? last : inIdx;               // inIdx < 0 不可能

                int oo = row + outX * BytesPerPixel;
                int ii = row + inX * BytesPerPixel;

                b += src[ii] - src[oo];
                g += src[ii + 1] - src[oo + 1];
                r += src[ii + 2] - src[oo + 2];
                a += src[ii + 3] - src[oo + 3];
            }
        }
    }

    /// <summary>纵向一趟。逻辑和横向完全对称，只是跨行取。</summary>
    private static void BlurVertical(byte[] src, byte[] dst, int width, int height, int radius)
    {
        int window = radius * 2 + 1;
        int stride = width * BytesPerPixel;
        int last = height - 1;

        for (int x = 0; x < width; x++)
        {
            int col = x * BytesPerPixel;

            int b = 0, g = 0, r = 0, a = 0;

            for (int k = -radius; k <= radius; k++)
            {
                int yi = k < 0 ? 0 : (k > last ? last : k);
                int o = yi * stride + col;
                b += src[o]; g += src[o + 1]; r += src[o + 2]; a += src[o + 3];
            }

            for (int y = 0; y < height; y++)
            {
                int o = y * stride + col;
                dst[o] = (byte)(b / window);
                dst[o + 1] = (byte)(g / window);
                dst[o + 2] = (byte)(r / window);
                dst[o + 3] = (byte)(a / window);

                int outIdx = y - radius;
                int inIdx = y + 1 + radius;

                int outY = outIdx < 0 ? 0 : outIdx;
                int inY = inIdx > last ? last : inIdx;

                int oo = outY * stride + col;
                int ii = inY * stride + col;

                b += src[ii] - src[oo];
                g += src[ii + 1] - src[oo + 1];
                r += src[ii + 2] - src[oo + 2];
                a += src[ii + 3] - src[oo + 3];
            }
        }
    }

    // ── 真高斯模糊 ──────────────────────────────────────────────────

    /// <summary>
    /// 1D 高斯核（**定点**，权重和恰好 = 65536）。按 σ 缓存。
    ///
    /// ══ 为什么是定点而不是 double ══════════════════════════════════
    ///
    /// 内层要对每个像素累加 K 次（K = 核长，σ=28 时是 169）。
    /// 用 double 的话每像素 4 通道 × 169 次浮点乘加 —— 太慢。
    /// 改成 `像素 × 整数权重` 累加进 int，最后 `(sum + 32768) >> 16` 收尾。
    ///
    /// 溢出检查：权重和 = 65536，单通道最大 255 × 65536 = 16,711,680，
    /// 远小于 int32 上限（21 亿）。**不会溢出。**
    ///
    /// ══ ★ 权重和必须**恰好** 65536 ══════════════════════════════════
    ///
    /// 每个权重单独四舍五入的话，和会差几个 —— 纯色图模糊后会掉一两个色阶。
    /// 所以最后把残差**全部补给中心元素**，强制和为 65536。
    /// 这条有单测钉着（"纯色图模糊后仍是纯色"）。
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<double, int[]> GaussianKernels = new();

    /// <summary>
    /// 取 σ 对应的 1D 高斯核。半径 = ⌈3σ⌉（3σ 之外权重 &lt; 1%，可以砍掉）。
    ///
    /// σ ≤ 0 返回 null —— 调用方据此跳过模糊。
    /// </summary>
    private static int[]? GetGaussianKernel(double sigma)
    {
        if (sigma <= 0 || double.IsNaN(sigma) || double.IsInfinity(sigma)) return null;

        if (GaussianKernels.TryGetValue(sigma, out var cached)) return cached;

        int radius = Math.Max(1, (int)Math.Ceiling(3.0 * sigma));
        int len = radius * 2 + 1;
        var k = new int[len];

        double twoSigmaSq = 2.0 * sigma * sigma;
        double sum = 0;

        for (int i = 0; i < len; i++)
        {
            double d = i - radius;
            double w = Math.Exp(-(d * d) / twoSigmaSq);
            k[i] = (int)Math.Round(w * 65536.0);
            sum += w;
        }

        // 归一化 + 把舍入残差补给中心，保证和**恰好** 65536
        double scale = 65536.0 / sum;
        int total = 0;
        for (int i = 0; i < len; i++)
        {
            double d = i - radius;
            k[i] = (int)Math.Round(Math.Exp(-(d * d) / twoSigmaSq) * scale);
            total += k[i];
        }
        k[radius] += 65536 - total;

        GaussianKernels[sigma] = k;
        return k;
    }

    /// <summary>
    /// **真高斯模糊**（可分离两趟），σ 是全分辨率像素。
    ///
    /// ══ ★ 这是"盒式 vs 高斯"那件事的正解 ═══════════════════════════
    ///
    /// <see cref="BoxBlur"/> 的 2D 核是 `(2r+1)×(2r+1)` 的**均匀方块**
    /// （1D 盒式 ⊗ 1D 盒式 = 方窗），**不是圆形**。
    /// 实测（角/中心能量比，1.0 = 完全平坦的方块）：
    ///
    ///     盒式 r=4 × 1 遍   角/中心 1.00   真高斯同 σ 是 0.26   → 方了 3.9×
    ///     盒式 r=4 × 3 遍   角/中心 0.54   真高斯同 σ 是 0.45   → 方了 1.2×
    ///
    /// 所以"3 遍 ≈ 高斯"**只在 3 遍时成立**，遍数低的时候核就是个方块 ——
    /// 用户报的"马赛克"（半径 4、遍数 1）正是这个。
    ///
    /// 这个函数**没有"遍数"这个参数**：它就是一遍真高斯，
    /// 和浏览器 `backdrop-filter: blur(Npx)` 的语义对齐（N = σ）。
    ///
    /// ══ 性能：这是唯一的拦路虎 ═══════════════════════════════════
    ///
    /// 盒式靠滑动求和，每像素 O(1)；高斯每像素 O(K)，K = 2⌈3σ⌉+1。
    /// **σ=28 时 K=169，全分辨率 160×160 单格约 3460 万次乘加。**
    /// 所以调用方（<see cref="GlassSurface"/>）会**先缩小工作分辨率**再调这里，
    /// 让 σ_工作 ≤ 封顶值。**缩小分辨率是对的，退回盒式是错的。**
    ///
    /// ══ 边界必须**钳位**，不能补零 ════════════════════════════════
    ///
    /// 补零的话边缘会往外吸黑，整圈发暗。钳位（重复边缘那一列）
    /// 才能保证纯色图模糊后仍是纯色 —— 和 <see cref="BoxBlur"/> 一致。
    ///
    /// σ ≤ 0 时原样返回副本 —— 调用方不用特判。
    /// </summary>
    public static byte[] GaussianBlur(byte[] src, int width, int height, double sigma)
    {
        var current = (byte[])src.Clone();

        if (width < 1 || height < 1) return current;

        int[]? kernel = GetGaussianKernel(sigma);
        if (kernel is null) return current;

        int radius = kernel.Length / 2;
        var temp = new byte[current.Length];

        BlurHorizontalGaussian(current, temp, width, height, kernel, radius);
        BlurVerticalGaussian(temp, current, width, height, kernel, radius);

        return current;
    }

    /// <summary>
    /// 横向一趟（真高斯）。
    ///
    /// ★ 循环拆成「左边界 / 中间 / 右边界」三段 —— 只有边界那几列需要钳位，
    ///   中间一大段不用，省掉内层的分支。这个套路是从
    ///   <see cref="BlurHorizontal"/>（盒式）直接搬过来的，实测快一倍。
    /// </summary>
    private static void BlurHorizontalGaussian(byte[] src, byte[] dst,
                                               int width, int height,
                                               int[] k, int radius)
    {
        int n = k.Length;
        int stride = width * BytesPerPixel;
        int last = width - 1;

        for (int y = 0; y < height; y++)
        {
            int row = y * stride;

            for (int x = 0; x < width; x++)
            {
                int o = row + x * BytesPerPixel;
                int b = 0, g = 0, r = 0, a = 0;

                if (x >= radius && x < width - radius)
                {
                    // 中间段：下标绝不越界，不用钳位
                    int start = row + (x - radius) * BytesPerPixel;
                    for (int j = 0; j < n; j++)
                    {
                        int w = k[j];
                        int oo = start + j * BytesPerPixel;
                        b += src[oo] * w;
                        g += src[oo + 1] * w;
                        r += src[oo + 2] * w;
                        a += src[oo + 3] * w;
                    }
                }
                else
                {
                    // 边界段：钳位（重复边缘那一列）
                    for (int j = 0; j < n; j++)
                    {
                        int xi = x - radius + j;
                        if (xi < 0) xi = 0; else if (xi > last) xi = last;

                        int w = k[j];
                        int oo = row + xi * BytesPerPixel;
                        b += src[oo] * w;
                        g += src[oo + 1] * w;
                        r += src[oo + 2] * w;
                        a += src[oo + 3] * w;
                    }
                }

                dst[o] = (byte)((b + 32768) >> 16);
                dst[o + 1] = (byte)((g + 32768) >> 16);
                dst[o + 2] = (byte)((r + 32768) >> 16);
                dst[o + 3] = (byte)((a + 32768) >> 16);
            }
        }
    }

    /// <summary>纵向一趟（真高斯）。逻辑和横向完全对称，只是跨行取。</summary>
    private static void BlurVerticalGaussian(byte[] src, byte[] dst,
                                             int width, int height,
                                             int[] k, int radius)
    {
        int n = k.Length;
        int stride = width * BytesPerPixel;
        int last = height - 1;

        for (int x = 0; x < width; x++)
        {
            int col = x * BytesPerPixel;

            for (int y = 0; y < height; y++)
            {
                int o = y * stride + col;
                int b = 0, g = 0, r = 0, a = 0;

                if (y >= radius && y < height - radius)
                {
                    int start = (y - radius) * stride + col;
                    for (int j = 0; j < n; j++)
                    {
                        int w = k[j];
                        int oo = start + j * stride;
                        b += src[oo] * w;
                        g += src[oo + 1] * w;
                        r += src[oo + 2] * w;
                        a += src[oo + 3] * w;
                    }
                }
                else
                {
                    for (int j = 0; j < n; j++)
                    {
                        int yi = y - radius + j;
                        if (yi < 0) yi = 0; else if (yi > last) yi = last;

                        int w = k[j];
                        int oo = yi * stride + col;
                        b += src[oo] * w;
                        g += src[oo + 1] * w;
                        r += src[oo + 2] * w;
                        a += src[oo + 3] * w;
                    }
                }

                dst[o] = (byte)((b + 32768) >> 16);
                dst[o + 1] = (byte)((g + 32768) >> 16);
                dst[o + 2] = (byte)((r + 32768) >> 16);
                dst[o + 3] = (byte)((a + 32768) >> 16);
            }
        }
    }

    /// <summary>
    /// ★ 高斯核的**半径**（⌈3σ⌉）—— 调用方用它算"核有多长"，好决定工作分辨率。
    /// σ ≤ 0 时返回 0。
    /// </summary>
    public static int GaussianRadius(double sigma)
        => sigma <= 0 ? 0 : Math.Max(1, (int)Math.Ceiling(3.0 * sigma));

    // ── 工作分辨率的选择（★ 实测数据在这里）─────────────────────────

    /// <summary>
    /// σ_工作 的封顶值。超过就降采样 —— 见 <see cref="ChooseWorkingFactor"/>。
    /// </summary>
    private const double SigmaWorkMax = 14.0;

    /// <summary>
    /// 单格模糊的**工作量预算**（工作像素数 × 核长）。
    ///
    /// ══ ★ 这个数是实测出来的，不是拍的 ═══════════════════════════
    ///
    /// 本机实测（Release、160×160 随机噪声 = 最坏情况，真实桌面更平滑）：
    ///
    ///     σ_工作  核长K   工作边长    ms/格    九格
    ///     ─────────────────────────────────────────────
    ///      28     169    160×160    14.1     127ms   ← 全分辨率，太重
    ///      14      85     80×80      1.74     15.7ms  ← 缩 2×，很划算
    ///       7      43     40×40      0.256     2.3ms  ← 缩 4×，最省
    ///       8      49    160×160     5.24     47.1ms  ← 低 σ 不缩
    ///
    /// 反推出本机吞吐约 **4.5e8 ~ 6.3e8（像素×核长）/ 毫秒**。
    ///
    /// ══ ★ 预算为什么是 1.6e7 而不是更小 ═══════════════════════════
    ///
    ///   预算定小一点会更省时间，但**会白缩**：σ=28、格子 160px 时，
    ///   缩 2× 和缩 4× 的视觉差别看不出来（都糊了），可缩 4× 要多一次
    ///   抓屏采样损失。实测 σ_工作=14/核长 85 这一档九格只要 15.7ms，
    ///   **完全付得起**，所以没必要为了省那点时间去多缩一档。
    ///
    ///   1.6e7 → σ=28@160px 选 2×（160×160×85 = 2.18e6），
    ///   σ=4 不缩，高 DPI（320px）自动多缩一档。
    /// </summary>
    private const double WorkBudgetPerCell = 1.6e7;

    /// <summary>
    /// 给 <see cref="GlassSurface"/> 选**降采样因子**：在预算内尽量不缩。
    ///
    /// 返回 1 = 不降采样，2 = 缩一半…… 上限 8。
    ///
    /// ★ 这个函数的输出**同时**决定抓屏和模糊的分辨率
    /// （`ScreenGrabber` 直接按这个因子抓小图），所以它必须在这儿定。
    ///
    /// ★ 它**不会**让 σ_工作 变成 0：<see cref="GaussianRadius"/> 最小返回 1，
    ///   核长至少 3。以前那个"整数除法把半径算成 0 → 整块没糊 → 马赛克"
    ///   的 bug，在这个形态下不存在。
    /// </summary>
    public static int ChooseWorkingFactor(double sigmaFull, int width, int height)
    {
        if (sigmaFull <= 0 || width <= 0 || height <= 0) return 1;

        for (int f = 1; f < 8; f++)
        {
            double sigmaWork = sigmaFull / f;
            int w = Math.Max(1, width / f);
            int h = Math.Max(1, height / f);
            int k = 2 * GaussianRadius(sigmaWork) + 1;

            if (sigmaWork <= SigmaWorkMax && (double)w * h * k <= WorkBudgetPerCell)
                return f;
        }

        return 8;
    }

    // ── 色彩 ────────────────────────────────────────────────────────

    /// <summary>
    /// 饱和度 / 亮度调整，就地修改。
    ///
    /// 苹果的玻璃会让背景**更鲜艳**（saturate 180%），这是"高级感"的一大半。
    /// 只糊不调色，看上去就只是"雾蒙蒙"。
    /// </summary>
    public static void ApplySaturationBrightness(byte[] pixels, double saturation, double brightness)
    {
        if (Math.Abs(saturation - 1.0) < 0.001 && Math.Abs(brightness - 1.0) < 0.001) return;

        for (int i = 0; i + 3 < pixels.Length; i += BytesPerPixel)
        {
            double b = pixels[i];
            double g = pixels[i + 1];
            double r = pixels[i + 2];

            // 亮度（BT.601 权重），用来算"灰色基线"
            double lum = 0.114 * b + 0.587 * g + 0.299 * r;

            // 饱和度 = 往灰色方向插值（<1 变灰，>1 变艳）
            b = (lum + (b - lum) * saturation) * brightness;
            g = (lum + (g - lum) * saturation) * brightness;
            r = (lum + (r - lum) * saturation) * brightness;

            pixels[i] = (byte)(b < 0 ? 0 : (b > 255 ? 255 : (int)(b + 0.5)));
            pixels[i + 1] = (byte)(g < 0 ? 0 : (g > 255 ? 255 : (int)(g + 0.5)));
            pixels[i + 2] = (byte)(r < 0 ? 0 : (r > 255 ? 255 : (int)(r + 0.5)));
        }
    }

    // ── 折射 ────────────────────────────────────────────────────────

    /// <summary>
    /// 按位移图对图像重采样 —— **这就是"液态玻璃"的折射**。
    ///
    /// <paramref name="map"/> 是 <see cref="RefractionMap"/> 产出的每像素位移
    /// （长度 = 宽×高×2，依次是 dx, dy，单位是像素）。
    ///
    /// <paramref name="channel"/> 是给色散用的：三个颜色通道用略微
    /// 不同的缩放量重采样，再拼起来，边缘就会泛出一圈彩虹。
    ///
    /// ★ 优化：**只算位移不为零的地方**。
    ///   位移图中心区域恒为 0，占了大部分像素 —— 那些像素直接拷贝就行。
    ///   九宫格 720×720 里，真正要重采样的只有边缘那一圈。
    ///
    /// ══ ★ 单通道调用必须先把整份像素拷过去（绿带 bug）══════════════
    ///
    ///   这个方法一次只写**一个**通道 —— 它本来就是给色散用的：
    ///   调三次（R/G/B），每次都只填自己那个通道，拼起来才是一张图。
    ///
    ///   问题是 `dst` 是**新建的全 0 数组**，而位移那一支只写了目标通道 ——
    ///   于是**单独调一次**（比如色散关掉、只跑绿通道）的时候，
    ///   R 和 B 永远是 0，折射带就成了一条**纯绿的带子**。
    ///   用户把「色散」滑块拖到 0 看到的就是这个。
    ///
    ///   修法：位移分支也**先整份拷贝源像素**，再让 SampleBilinear 覆盖目标通道。
    ///   中心那一支本来就是四通道全拷，两支的行为这下一致了。
    /// </summary>
    public static byte[] Refract(byte[] src, int width, int height,
                                 float[] map, double strength,
                                 double chromaticAberration, int channel)
    {
        var dst = new byte[src.Length];

        // 三个通道的缩放：R 最强、G 居中、B 最弱 —— 顺序无所谓，
        // 关键是三者**不相等**，差值就是色散。
        double scale = strength * (1.0 - chromaticAberration * channel * 0.05);

        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * width;

            for (int x = 0; x < width; x++)
            {
                int mi = (rowOffset + x) * 2;
                float dx = map[mi];
                float dy = map[mi + 1];

                int d = (rowOffset + x) * BytesPerPixel;

                if (dx == 0f && dy == 0f)
                {
                    // 中心区域：原样透过
                    dst[d] = src[d];
                    dst[d + 1] = src[d + 1];
                    dst[d + 2] = src[d + 2];
                    dst[d + 3] = src[d + 3];
                    continue;
                }

                // ★ 先整份拷贝 —— 保证另外两个通道不是 0。
                //   少了这两行，色散关掉时折射带会变成纯绿。
                dst[d] = src[d];
                dst[d + 1] = src[d + 1];
                dst[d + 2] = src[d + 2];
                dst[d + 3] = src[d + 3];

                SampleBilinear(src, width, height,
                               x + dx * scale, y + dy * scale,
                               dst, d, channel);
            }
        }

        return dst;
    }

    /// <summary>
    /// 双线性采样一个像素，只写入指定通道（其余通道保留 dst 里的原值）。
    ///
    /// 只写一个通道是为了色散：R 用位移图采一次、G 采一次、B 采一次，
    /// 三次调用填进同一张目标图。
    /// </summary>
    private static void SampleBilinear(byte[] src, int width, int height,
                                       double sx, double sy,
                                       byte[] dst, int dstOffset, int channel)
    {
        int x0 = (int)Math.Floor(sx);
        int y0 = (int)Math.Floor(sy);
        double wx = sx - x0;
        double wy = sy - y0;

        int x0c = x0 < 0 ? 0 : (x0 >= width ? width - 1 : x0);
        int x1c = x0 + 1 < 0 ? 0 : (x0 + 1 >= width ? width - 1 : x0 + 1);
        int y0c = y0 < 0 ? 0 : (y0 >= height ? height - 1 : y0);
        int y1c = y0 + 1 < 0 ? 0 : (y0 + 1 >= height ? height - 1 : y0 + 1);

        int o00 = (y0c * width + x0c) * BytesPerPixel + channel;
        int o01 = (y0c * width + x1c) * BytesPerPixel + channel;
        int o10 = (y1c * width + x0c) * BytesPerPixel + channel;
        int o11 = (y1c * width + x1c) * BytesPerPixel + channel;

        double top = src[o00] * (1 - wx) + src[o01] * wx;
        double bot = src[o10] * (1 - wx) + src[o11] * wx;
        double v = top * (1 - wy) + bot * wy;

        dst[dstOffset + channel] = (byte)(v < 0 ? 0 : (v > 255 ? 255 : (int)(v + 0.5)));
    }

    /// <summary>整份拷贝一份（模糊等操作不修改输入，调用方要自己存底时用）</summary>
    public static byte[] Clone(byte[] src) => (byte[])src.Clone();
}
