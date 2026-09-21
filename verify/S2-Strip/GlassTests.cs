using System;

namespace S2Strip;

/// <summary>
/// 玻璃材质的自测 —— 折射位移图 + 纯像素运算。
///
/// ══ 为什么这组测试非写不可 ══════════════════════════════════════
///
/// 玻璃效果出错的表现是**"看着有点怪"** —— 模糊重一点、颜色偏一点，
/// 肉眼根本分不清是算法错了还是参数没调好。
///
/// 我第一版就踩了：照着参考实现抄了参数，但没注意它的"圆角矩形"是
/// **比元素小一圈**的，结果 `d` 永远够不到斜坡的门槛，整张位移图全是 0 ——
/// **折射完全失效，画面看起来却"只是没效果"，不报错、不崩、不异常。**
///
/// 所以这里最重要的一条是「位移图不能全是 0」。
/// 它花了我一次编译才想到要验，以后不会再花第二次。
///
/// 最后一组是**依赖屏幕**的抓屏探针 —— 它单独跑，失败了只警告不判错，
/// 因为无头环境本来就抓不到画面。
/// </summary>
internal static class GlassTests
{
    private static int _pass;
    private static int _fail;

    public static void Run()
    {
        _pass = 0;
        _fail = 0;

        Console.WriteLine();
        Console.WriteLine("── Glass 材质自测 ────────────────────────────────");

        TestSmoothStep();
        TestSdf();
        TestRefractionMap();
        TestBlur();
        TestResample();
        TestColor();
        TestVisibility();
        TestTintNotBlack();

        Console.WriteLine($"  小计：{_pass} 通过 / {_fail} 失败");
        Console.WriteLine("──────────────────────────────────────────────────");
    }

    // ── ★ 可见性断言（本次新增，就是它该早点存在）─────────────────

    /// <summary>
    /// 测 "这张图看起来是不是黑的"。
    ///
    /// ★ 为什么必须补这一组：连续两轮我报"已修复"，用户看到的还是全黑。
    ///   日志里明明有"原图 32.6；玻璃图 43.8"，但**没有断言**，
    ///   全黑数据被当成正常值播了过去。
    ///   **单测全绿不等于用户能看见** —— 这组就是"看得见"的量化证明。
    /// </summary>
    private static void TestVisibility()
    {
        // 全黑 = 必须被判为不可见
        byte[] black = Solid(20, 20, 0, 0, 0);
        var vBlack = GlassSurface.Measure(black);
        Report($"★ 全黑被判为不可见（{vBlack.Mean:0.#}）", !vBlack.Ok);

        // 纯色（哪怕是亮的）= 判为不可见 —— 看不出玻璃透出了什么
        byte[] flat = Solid(20, 20, 128, 128, 128);
        var vFlat = GlassSurface.Measure(flat);
        Report($"★ 纯色被判为不可见（均值 {vFlat.Mean:0.#}）", !vFlat.Ok);

        // 有明暗变化的图 = 可见
        byte[] varied = new byte[40 * 40 * 4];
        for (int y = 0; y < 40; y++)
            for (int x = 0; x < 40; x++)
            {
                int o = (y * 40 + x) * 4;
                byte v = (byte)(x < 20 ? 40 : 220);
                varied[o] = v; varied[o + 1] = v; varied[o + 2] = v; varied[o + 3] = 255;
            }
        var vGood = GlassSurface.Measure(varied);
        Report($"★ 有明暗的图被判为可见（{vGood.Mean:0.#}，起伏 {vGood.StdDev:0.#}）", vGood.Ok);

        // 空输入不能崩
        var vNull = GlassSurface.Measure(null);
        Report("null 输入不崩且判为不可见", !vNull.Ok);
        var vEmpty = GlassSurface.Measure(Array.Empty<byte>());
        Report("空数组不崩且判为不可见", !vEmpty.Ok);
    }

    /// <summary>
    /// ★ 最核心的一条：**玻璃处理不能把亮背景变黑**。
    ///
    /// 这是用户看到的故障本身 —— 抓屏成功了（原图均值 60+），
    /// 但经过处理之后变成了黑色。所以这条直接对着故障断言。
    /// </summary>
    private static void TestTintNotBlack()
    {
        // 造一张"像真实桌面"的图：中等亮度 + 有起伏
        const int W = 60, H = 60;
        byte[] backdrop = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int o = (y * W + x) * 4;
                byte v = (byte)(60 + (x * 120 / W));
                backdrop[o] = v; backdrop[o + 1] = v; backdrop[o + 2] = v; backdrop[o + 3] = 255;
            }

        var before = GlassSurface.Measure(backdrop);

        // 跑默认参数的完整处理链（模糊 + 调色 + 底色）
        var p = new GlassParams();
        byte[] processed = ImageOps.BoxBlur(backdrop, W, H, p.BlurRadius / p.Downsample, p.BlurPasses);
        ImageOps.ApplySaturationBrightness(processed, p.Saturation, p.Brightness);
        GlassSurface.ApplyGlassTintForTest(processed, p);

        var after = GlassSurface.Measure(processed);

        Report($"★ 处理前可见（{before.Mean:0.#}）", before.Ok);
        Report($"★★ 处理后仍然可见（{after.Mean:0.#}），没被变黑", after.Ok);
        Report($"★★ 处理后没有整体压暗（{before.Mean:0.#} → {after.Mean:0.#}）",
               after.Mean >= before.Mean * 0.7);

        // 最关键的回归：亮背景绝不能被压成黑（均值 < 20 就是黑）
        Report($"★★ 亮背景处理后的均值 > 30（实测 {after.Mean:0.#}）", after.Mean > 30);
    }

    // ── 平滑阶跃 ────────────────────────────────────────────────────

    private static void TestSmoothStep()
    {
        // 正向斜坡：a < b
        Report("smoothStep(0,1,0) = 0", Near(RefractionMap.SmoothStep(0, 1, 0), 0));
        Report("smoothStep(0,1,1) = 1", Near(RefractionMap.SmoothStep(0, 1, 1), 1));
        Report("smoothStep(0,1,0.5) = 0.5（中点）", Near(RefractionMap.SmoothStep(0, 1, 0.5), 0.5));

        // ★ 反向斜坡：a > b —— 苹果那套参数用的就是这个，写成交换参数就错了
        Report("★ 反向 smoothStep(0.8,0,0.8) = 0", Near(RefractionMap.SmoothStep(0.8, 0, 0.8), 0));
        Report("★ 反向 smoothStep(0.8,0,0) = 1", Near(RefractionMap.SmoothStep(0.8, 0, 0), 1));
        Report("★ 反向 smoothStep(0.8,0,-1) 钳位到 1",
               Near(RefractionMap.SmoothStep(0.8, 0, -1), 1));
        Report("★ 反向 smoothStep(0.8,0,2) 钳位到 0",
               Near(RefractionMap.SmoothStep(0.8, 0, 2), 0));

        // 退化为阶跃（a == b）不能除零
        double degenerate = RefractionMap.SmoothStep(1, 1, 2);
        Report("a == b 不崩（除零保护）", !double.IsNaN(degenerate) && !double.IsInfinity(degenerate));
    }

    // ── 有符号距离场 ────────────────────────────────────────────────

    private static void TestSdf()
    {
        // 100×100 的方框，半边长 50，圆角 10
        Report("SDF 中心为负（在内部）",
               RefractionMap.RoundedBoxSdf(0, 0, 50, 50, 10) < 0);

        Report("SDF 边界约等于 0",
               Math.Abs(RefractionMap.RoundedBoxSdf(50, 0, 50, 50, 10)) < 0.01);

        Report("SDF 外部为正",
               RefractionMap.RoundedBoxSdf(60, 0, 50, 50, 10) > 0);

        // 圆角处：正方形角点 (50,50) 在圆角之外，应该是正的
        double corner = RefractionMap.RoundedBoxSdf(50, 50, 50, 50, 10);
        Report("★ 圆角处的角点被判为外部（说明圆角真的生效了）", corner > 0);

        // 圆角为 0 时，角点正好在边界上
        double sharpCorner = RefractionMap.RoundedBoxSdf(50, 50, 50, 50, 0);
        Report("圆角=0 时角点恰在边界（≈0）", Math.Abs(sharpCorner) < 0.01);

        // 到中心的距离对称
        Report("SDF 左右对称",
               Near(RefractionMap.RoundedBoxSdf(-30, 5, 50, 50, 10),
                    RefractionMap.RoundedBoxSdf(30, 5, 50, 50, 10)));
    }

    // ── 折射位移图 ──────────────────────────────────────────────────

    private static void TestRefractionMap()
    {
        const int W = 120, H = 120, R = 16;

        float[] map = RefractionMap.Compute(W, H, R);
        Report("位移图长度 = 宽×高×2", map.Length == W * H * 2);

        // ★★ 最重要的一条 ★★
        // 全是 0 = 折射完全失效，但**不报错、不崩、不异常** ——
        // 只是"看起来没效果"。我第一版就是这样。
        float maxMag = 0;
        foreach (float v in map) maxMag = Math.Max(maxMag, Math.Abs(v));
        Report($"★ 位移图**不能全为 0**（实测最大 {maxMag:0.###}）", maxMag > 0.5f);

        // 中心几乎不动（原样透过）
        float center = Math.Max(Math.Abs(At(map, W, W / 2, H / 2, 0)),
                                Math.Abs(At(map, W, W / 2, H / 2, 1)));
        Report($"中心位移≈0（实测 {center:0.###}）", center < 0.02f);

        // 最外圈被压回 0 —— 这是"窗口和桌面无缝衔接"的前提
        float border = 0;
        for (int x = 0; x < W; x++)
        {
            border = Math.Max(border, Math.Abs(At(map, W, x, 0, 0)));
            border = Math.Max(border, Math.Abs(At(map, W, x, 0, 1)));
            border = Math.Max(border, Math.Abs(At(map, W, x, H - 1, 0)));
            border = Math.Max(border, Math.Abs(At(map, W, x, H - 1, 1)));
        }
        Report($"★ 最外圈位移=0（实测 {border:0.###}）—— 圆角才能无缝", border < 0.001f);

        // 位移指向**内侧**：下半部分的 dy 应该是负的（往中心拉）
        float dyBottom = At(map, W, W / 2, H - 4, 1);
        Report($"★ 下边缘的位移指向上方（往中心拉，实测 dy={dyBottom:0.###}）", dyBottom < -0.1f);

        float dyTop = At(map, W, W / 2, 3, 1);
        Report($"★ 上边缘的位移指向下方（实测 dy={dyTop:0.###}）", dyTop > 0.1f);

        // 折射带：从中心往外应该单调增强（在一条中轴线上采样）
        bool monotonic = true;
        float prev = 0;
        for (int y = H / 2; y < H - 2; y++)
        {
            float mag = Math.Abs(At(map, W, W / 2, y, 1));
            if (mag < prev - 0.02f) { monotonic = false; break; }
            prev = mag;
        }
        Report("中轴线上位移从中心向外单调增强", monotonic);

        // 圆角：正中间那条边有位移，而**紧挨圆角内侧**的位移应该更小
        float midEdge = Math.Abs(At(map, W, W / 2, 2, 1));
        float cornerIn = Math.Abs(At(map, W, 3, 3, 1));
        Report($"圆角附近位移小于边中点（{cornerIn:0.##} < {midEdge:0.##}）", cornerIn < midEdge);

        // 缓存：同参数两次调用拿到同一个数组
        var a = RefractionMap.BuildDirection(W, H, R);
        var b = RefractionMap.BuildDirection(W, H, R);
        Report("同尺寸同圆角命中缓存（同一份数组）", ReferenceEquals(a, b));

        // 极端尺寸不能崩
        Report("1×1 不崩", RefractionMap.Compute(1, 1, 0).Length == 2);
        Report("2×2 不崩", RefractionMap.Compute(2, 2, 0).Length == 8);
        Report("超长条 200×4 不崩", RefractionMap.Compute(200, 4, 2).Length == 200 * 4 * 2);
        Report("圆角比尺寸还大不崩", RefractionMap.Compute(10, 10, 999).Length == 200);
    }

    // ── 模糊 ────────────────────────────────────────────────────────

    private static void TestBlur()
    {
        const int W = 40, H = 30;

        // 纯色图模糊后还是纯色 —— 边缘钳位写错的话这条立刻红
        byte[] solid = Solid(W, H, 120, 60, 200);
        byte[] blurredSolid = ImageOps.BoxBlur(solid, W, H, 3);

        bool same = true;
        for (int i = 0; i + 3 < blurredSolid.Length; i += 4)
        {
            if (blurredSolid[i] != 120 || blurredSolid[i + 1] != 60 || blurredSolid[i + 2] != 200)
            { same = false; break; }
        }
        Report("★ 纯色图模糊后仍是纯色（边缘钳位正确）", same);

        // radius = 0 = 原样返回
        byte[] noBlur = ImageOps.BoxBlur(solid, W, H, 0);
        Report("radius=0 原样返回", noBlur.Length == solid.Length && noBlur[0] == 120);

        // 单点脉冲：模糊后中心变暗、周围变亮（能量扩散出去了）
        byte[] pulse = new byte[W * H * 4];
        for (int i = 0; i + 3 < pulse.Length; i += 4) pulse[i + 3] = 255;
        int cx = W / 2, cy = H / 2;
        int co = (cy * W + cx) * 4;
        pulse[co] = 255; pulse[co + 1] = 255; pulse[co + 2] = 255;

        byte[] blurredPulse = ImageOps.BoxBlur(pulse, W, H, 3);

        Report($"脉冲中心被削弱（255 → {blurredPulse[co]}）", blurredPulse[co] < 255);
        Report("脉冲能量扩散到邻居",
               blurredPulse[co + 4] > 0 || blurredPulse[co + W * 4] > 0);

        // 模糊不应该改变 alpha（我们的图 alpha 恒为 255）
        Report("alpha 保持 255", blurredPulse[co + 3] == 255);

        // 尺寸不变
        Report("模糊不改变尺寸", blurredPulse.Length == pulse.Length);

        // 极小尺寸不崩
        Report("1×1 模糊不崩", ImageOps.BoxBlur(new byte[4], 1, 1, 5).Length == 4);
        Report("半径远大于图像不崩", ImageOps.BoxBlur(solid, W, H, 999).Length == solid.Length);
    }

    // ── 重采样 ──────────────────────────────────────────────────────

    private static void TestResample()
    {
        const int W = 40, H = 40;

        byte[] solid = Solid(W, H, 10, 20, 30);

        // 降采样
        byte[] down = ImageOps.Downsample(solid, W, H, 4, out int dw, out int dh);
        Report("降采样尺寸 = 原尺寸 ÷ 4", dw == 10 && dh == 10);
        Report("降采样后仍是纯色（块平均正确）",
               down[0] == 10 && down[1] == 20 && down[2] == 30);

        // 升采样回原尺寸
        byte[] up = ImageOps.Upsample(down, dw, dh, W, H);
        Report("升采样尺寸正确", up.Length == W * H * 4);
        Report("纯色图往返不变",
               up[0] == 10 && up[1] == 20 && up[2] == 30 && up[up.Length - 4] == 10);

        // 同尺寸升采样 = 近似恒等
        byte[] same = ImageOps.Upsample(solid, W, H, W, H);
        Report("同尺寸升采样 = 恒等", same[0] == 10 && same[1] == 20 && same[2] == 30);

        // 非整数倍
        byte[] odd = ImageOps.Upsample(down, dw, dh, 37, 37);
        Report("非整数倍升采样尺寸正确", odd.Length == 37 * 37 * 4);

        // 1×1 不崩
        Report("1×1 降采样不崩", ImageOps.Downsample(new byte[4], 1, 1, 4, out _, out _).Length == 4);
        Report("1×1 升采样不崩", ImageOps.Upsample(new byte[4], 1, 1, 8, 8).Length == 8 * 8 * 4);

        // ── 折射重采样 ──
        float[] zeroMap = new float[W * H * 2];      // 全 0 = 不位移
        byte[] refracted = ImageOps.Refract(solid, W, H, zeroMap, 1.0, 0, 0);
        Report("位移全 0 时折射 = 原样拷贝",
               refracted[0] == 10 && refracted[1] == 20 && refracted[2] == 30);

        // 给一个固定位移，中心区域应该被拉动
        float[] shiftMap = new float[W * H * 2];
        for (int i = 0; i < shiftMap.Length; i += 2) shiftMap[i] = 5;   // dx = +5

        // 造一张左黑右白的图，往右位移后采样点右移 → 应该变亮
        byte[] gradient = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int o = (y * W + x) * 4;
                byte v = (byte)(x * 6);
                gradient[o] = v; gradient[o + 1] = v; gradient[o + 2] = v; gradient[o + 3] = 255;
            }

        byte[] shifted = ImageOps.Refract(gradient, W, H, shiftMap, 1.0, 0, 0);
        int probe = (20 * W + 20) * 4;
        Report($"★ 位移生效（dx=+5 时采样点右移，{gradient[probe]} → {shifted[probe]}）",
               shifted[probe] > gradient[probe]);
    }

    // ── 色彩 ────────────────────────────────────────────────────────

    private static void TestColor()
    {
        byte[] mid = Solid(4, 4, 128, 128, 128);

        // 饱和度 0 → 灰（本来就是灰，不变）
        byte[] gray = ImageOps.Clone(mid);
        ImageOps.ApplySaturationBrightness(gray, 0, 1.0);
        Report("灰色去饱和还是灰", gray[0] == 128 && gray[1] == 128 && gray[2] == 128);

        // 亮度 2× → 变亮
        byte[] bright = ImageOps.Clone(mid);
        ImageOps.ApplySaturationBrightness(bright, 1.0, 2.0);
        Report($"亮度 2× 后变亮（128 → {bright[0]}）", bright[0] > 128);

        // 亮度不能溢出（255 封顶）
        byte[] white = Solid(4, 4, 255, 255, 255);
        ImageOps.ApplySaturationBrightness(white, 1.0, 5.0);
        Report("亮度溢出被钳到 255", white[0] == 255 && white[1] == 255 && white[2] == 255);

        // 饱和度 2× → 有色像素更艳。
        //
        // ★ 注意 Solid 的参数顺序是 (b, g, r) —— 这是像素数组的真实顺序。
        //   我第一版把断言写反了（以为是 r,g,b），测试红了但代码是对的。
        //   这里用纯蓝（b=200, g=50, r=50）：加饱和后蓝更蓝、红更弱。
        byte[] color = Solid(4, 4, 200, 50, 50);      // 偏蓝
        byte[] vivid = ImageOps.Clone(color);
        ImageOps.ApplySaturationBrightness(vivid, 2.0, 1.0);
        Report($"饱和度 2× 后蓝色通道更强（{color[0]} → {vivid[0]}）", vivid[0] > color[0]);
        Report($"饱和度 2× 后红色通道更弱（{color[2]} → {vivid[2]}）", vivid[2] < color[2]);

        // 参数都是 1 时不动（快速返回路径）
        byte[] untouched = ImageOps.Clone(color);
        ImageOps.ApplySaturationBrightness(untouched, 1.0, 1.0);
        Report("参数全 1 时原样返回", untouched[0] == 200 && untouched[2] == 50);
    }

    // ── 抓屏探针（依赖屏幕，单独跑）────────────────────────────────

    /// <summary>
    /// 真的去抓一小块屏幕，验证 GDI 链路通不通。
    ///
    /// 这一条**失败只警告不判错** —— 无头环境/锁屏时本来就抓不到。
    /// 但只要是在正常桌面上跑，它必须通。
    /// </summary>
    public static void RunScreenProbe()
    {
        Console.WriteLine();
        Console.WriteLine("── 抓屏探针（依赖屏幕）───────────────────────────");

        // 抓屏幕正中 200×120，那块地方几乎不可能全黑
        int w = 200, h = 120;
        GetSystemMetrics(out int sw, out int sh);
        int x = Math.Max(0, sw / 2 - w / 2);
        int y = Math.Max(0, sh / 2 - h / 2);

        var (pixels, ms) = ScreenCapture.CaptureTimed(x, y, w, h);

        if (pixels is null)
        {
            Console.WriteLine($"  ⚠️  抓屏失败（屏幕 {sw}×{sh}，区域 {x},{y} {w}×{h}）");
            Console.WriteLine("      —— 这不一定是 bug，锁屏/无头环境本来就抓不到");
        }
        else
        {
            Console.WriteLine($"  ✅ 抓屏成功：{w}×{h}，{pixels.Length} 字节，耗时 {ms:0.#}ms");
            Console.WriteLine($"     {ScreenCapture.Describe(pixels, w, h)}");
        }

        // 性能预算：九宫格是 720×720。
        //
        // ★ 关键结论（第一轮实测）：**全尺寸抓屏太慢** —— 15.5ms，
        //   还没开始模糊就把预算用光了。
        //   改成"抓的同时降采样"（StretchBlt + HALFTONE）之后快一个数量级。
        //   所以正式链路用的是 CaptureScaled，不是 Capture。
        int gx = Math.Max(0, sw / 2 - 360), gy = Math.Max(0, sh / 2 - 360);

        var (big, bigMs) = ScreenCapture.CaptureTimed(gx, gy, 720, 720);
        Console.WriteLine(big is null
            ? "  ⚠️  720×720 全尺寸抓屏失败"
            : $"  ℹ️  720×720 全尺寸抓屏 {bigMs:0.#}ms（慢，正式链路不用这条）");

        var (small, smallMs) = CaptureScaledTimed(gx, gy, 720, 720, 4);
        if (small is null)
        {
            Console.WriteLine("  ⚠️  720×720 ÷4 抓屏失败");
        }
        else
        {
            Console.WriteLine($"  ✅ 720×720 ÷4 抓屏 {smallMs:0.#}ms"
                              + $"（{small.Length / 1024}KB）");
            Console.WriteLine($"     {ScreenCapture.Describe(small, 180, 180)}");
        }

        // ★★ 关键探针：抓屏成本到底花在哪 ★★
        //
        //   结论（实测）：
        //     · 固定开销 ≈ 4.3ms —— 32×32 那么小也要 4.55ms，
        //       这是 BitBlt/DWM 合成的一次往返，省不掉
        //     · 按像素收费 ≈ 0.017ms / 千像素 —— 720×720 再加 8.5ms
        //
        //   所以架构上两条：
        //     ① 降采样**确实有用**（720×720 从 13ms 降到 ~5ms），
        //        但不能指望降到 1ms —— 有 4.3ms 的地板
        //     ② 这个地板意味着**绝不能每帧抓屏**。
        //        九宫格/面板只在弹出时抓一次；条子只在前台窗口变化时抓。
        try
        {
            using var grabber = new ScreenCapture.ScreenGrabber(720, 720, 1);

            // 热身（第一次要建立 GDI 资源，不算数）
            grabber.Grab(gx, gy, 720, 720);
            grabber.Grab(gx, gy, 720, 720);

            (int W, int H)[] sizes = { (32, 32), (151, 15), (200, 120), (360, 360), (720, 720) };

            foreach (var (cw, ch) in sizes)
            {
                int cx = Math.Max(0, sw / 2 - cw / 2);
                int cy = Math.Max(0, sh / 2 - ch / 2);

                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                const int iterations = 20;
                for (int i = 0; i < iterations; i++) grabber.Grab(cx, cy, cw, ch);
                sw2.Stop();

                double perCall = sw2.Elapsed.TotalMilliseconds / iterations;
                Console.WriteLine($"  · {cw,4}×{ch,-4} ({cw * ch,7} px) → {perCall,6:0.##}ms/次");
            }

            Console.WriteLine("     ↑ 32×32 也要 ~4.3ms = 固定开销地板；"
                              + "其余按像素线性增长");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ⚠️  复用式抓屏器建立失败：{ex.Message}");
        }

        // ★★ 端到端：真跑一遍完整流水线 ★★
        //
        //   前面测的都是零件，这一条把「抓屏→模糊→折射→调色→圆角→上传」
        //   串起来跑，验证不崩、耗时可控、位图真的产出了。
        //
        // ★ 必须跑两遍：**第一遍的数不能信**。
        //   实测第一遍 659ms，第二遍 340ms，光"上传"一步就从 486ms 掉到 1.5ms ——
        //   那是 WPF 首次建位图 + JIT 的一次性开销。
        //   拿第一遍的数去调优会完全跑偏。
        try
        {
            var gp = new GlassParams { Downsample = 4 };
            using var surface = new GlassSurface(gp);

            // 热身（结果丢弃）
            surface.Render(gx, gy, 720, 720);

            bool ok = surface.Render(gx, gy, 720, 720);
            if (ok && surface.GlassBitmap is not null)
            {
                Console.WriteLine($"  ✅ 完整流水线 720×720（简化版）：{surface.LastRenderMs:0.#}ms"
                                  + $" → 玻璃图 {surface.GlassBitmap.PixelWidth}×{surface.GlassBitmap.PixelHeight}"
                                  + $"，背景图 {(surface.RawBitmap is null ? "无" : "有")}");
                Console.WriteLine($"     {surface.LastBreakdown}");

                // ★★★ 交付前自检 —— 这就是用户要的"你自己先测一下" ★★★
                //
                //   前面测的是"没崩、够快"，这一行测的是**"用户能不能看见"**。
                //   两者是不同的东西：我连续两轮都做到了前者，用户看到的还是全黑。
                Console.WriteLine($"  ★ 可见性自检（用户最终看到的画面）：{surface.GlassVisibility}");
                Console.WriteLine($"    原始抓屏：{surface.RawVisibility}");

                if (!surface.GlassVisibility.Ok)
                {
                    Console.WriteLine("  ❌ 玻璃图被判为不可见 —— 交付前必须解决，不能交给用户");
                }
            }
            else
            {
                Console.WriteLine($"  ⚠️  完整流水线失败：{surface.LastBreakdown}");
            }

            // 折射打开再跑一次（最慢的情况）
            var gp2 = new GlassParams { Downsample = 4, Refraction = 40, ChromaticAberration = 2 };
            using var surface2 = new GlassSurface(gp2);

            surface2.Render(gx, gy, 720, 720);      // 热身
            if (surface2.Render(gx, gy, 720, 720))
            {
                Console.WriteLine($"  ✅ 开启折射：{surface2.LastRenderMs:0.#}ms");
                Console.WriteLine($"     {surface2.LastBreakdown}");
            }
            else
            {
                Console.WriteLine($"  ⚠️  折射路径失败：{surface2.LastBreakdown}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ⚠️  流水线探针异常：{ex.GetType().Name} {ex.Message}");
        }

        Console.WriteLine("──────────────────────────────────────────────────");
    }

    private static (byte[]? Pixels, double Ms) CaptureScaledTimed(
        int x, int y, int w, int h, int factor)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        byte[]? px = ScreenCapture.CaptureScaled(x, y, w, h, factor, out _, out _);
        sw.Stop();
        return (px, sw.Elapsed.TotalMilliseconds);
    }

    private static void GetSystemMetrics(out int width, out int height)
    {
        width = NativeMethods.GetSystemMetrics(0);
        height = NativeMethods.GetSystemMetrics(1);
    }

    // ── 工具 ────────────────────────────────────────────────────────

    private static float At(float[] map, int width, int x, int y, int component)
        => map[(y * width + x) * 2 + component];

    private static byte[] Solid(int w, int h, byte b, byte g, byte r)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i + 3 < px.Length; i += 4)
        {
            px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255;
        }
        return px;
    }

    private static bool Near(double a, double b, double tol = 1e-6) => Math.Abs(a - b) < tol;

    private static void Report(string name, bool ok)
    {
        if (ok) { _pass++; Console.WriteLine($"  ✅ {name}"); }
        else { _fail++; Console.WriteLine($"  ❌ {name}"); }
    }
}
