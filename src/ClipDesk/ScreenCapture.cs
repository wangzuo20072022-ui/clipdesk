using System;
using System.Diagnostics;
using static ClipDesk.NativeMethods;

namespace ClipDesk;

/// <summary>
/// 抓屏 —— 把屏幕上某一块矩形区域的像素读回来。
///
/// ══ 为什么玻璃材质非要抓屏不可 ══════════════════════════════════
///
/// 苹果的「液态玻璃」和普通「毛玻璃」的区别只有一个词：**折射**。
/// 毛玻璃只是把背景糊掉；液态玻璃还会让**玻璃边缘把背景放大扭曲**，
/// 看上去像一块真的厚玻璃压在上面。
///
/// 在浏览器里这是 `backdrop-filter` 干的活 —— 它能把"我背后的一切"
/// 喂给一个滤镜链。**WPF 和 Win32 都没有这个东西**。
/// （DWM 的原生亚克力只有模糊，没有折射，而且模糊半径、色调全由
///   Windows 决定，我们一个参数都改不了。）
///
/// 所以只能自己来：把背景像素抓回来 → 模糊 → 按位移图重采样 → 贴回窗口。
/// 这个文件是第一步。
///
/// ══ 实现要点 ══════════════════════════════════════════════════
///
/// 链路：GetDC(屏幕) → CreateCompatibleDC → CreateCompatibleBitmap
///       → BitBlt → GetDIBits → byte[]
///
/// 三个容易踩的坑，都写在对应的常量旁边了：
///   · 必须带 CAPTUREBLT，否则分层窗口（很多现代应用）会漏成空洞
///   · 位图必须从**屏幕 DC** 创建，否则色深可能不匹配
///   · biHeight 取**负值**，拿到的才是自上而下的行序
///     （正值是自下而上的，忘了这个画面会上下颠倒，而且很隐蔽 ——
///      纯色区域看不出来，只有渐变或文字才露馅）
/// </summary>
internal static class ScreenCapture
{
    /// <summary>
    /// 抓一块屏幕区域。返回 BGRA 自上而下的字节数组（长 = 宽×高×4），
    /// 失败返回 null。调用方要判空。
    ///
    /// 坐标是**物理像素**，左上角为原点，和 GetCursorPos 同一套。
    /// </summary>
    public static byte[]? Capture(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;

        IntPtr screenDc = IntPtr.Zero;
        IntPtr memDc = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;

        try
        {
            screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return null;

            memDc = CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero) return null;

            // ★ 必须从**屏幕 DC** 建位图 —— 从内存 DC 建会得到一个 1bpp
            //   的单色位图，抓出来全是黑白。
            bitmap = CreateCompatibleBitmap(screenDc, width, height);
            if (bitmap == IntPtr.Zero) return null;

            oldBitmap = SelectObject(memDc, bitmap);

            if (!BitBlt(memDc, 0, 0, width, height,
                        screenDc, x, y, SRCCOPY | CAPTUREBLT))
            {
                return null;
            }

            var header = new BITMAPINFOHEADER
            {
                biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,          // ★ 负 = 自上而下
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
                biSizeImage = (uint)(width * height * 4),
            };

            var info = new BITMAPINFO { bmiHeader = header };
            var pixels = new byte[width * height * 4];

            int lines = GetDIBits(memDc, bitmap, 0, (uint)height,
                                  pixels, ref info, DIB_RGB_COLORS);

            return lines == 0 ? null : pixels;
        }
        catch (Exception)
        {
            // 抓屏失败不该让整个程序崩 —— 调用方会退回到纯色背景
            return null;
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero && memDc != IntPtr.Zero)
                SelectObject(memDc, oldBitmap);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>
    /// 抓一块屏幕区域，**并在抓的同时降采样**。
    ///
    /// 这是给玻璃材质用的主入口。
    ///
    /// ══ 两次实测得到的两个结论 ═══════════════════════════════════
    ///
    ///   ① 全尺寸抓 720×720 要 12~15ms —— 还没开始模糊就把预算用光了。
    ///      所以这里顺手降采样（StretchBlt + HALFTONE），
    ///      传输量从 2MB 降到 130KB。
    ///
    ///   ② **但总耗时并没有线性下降**（还是 11ms 左右）——
    ///      说明瓶颈是**每次调用都要重新建 DC 和位图**，那是驱动层的固定开销。
    ///      （GetDC 8000 次才 0.6ms，不是它；CreateCompatibleDC/Bitmap
    ///        每对约 0.06ms，也不是它。剩下的是 BitBlt 自己的启动成本。）
    ///
    ///   ★ 所以正式链路要**复用 DC 和位图**，不能每次抓屏都重建。
    ///     见 <see cref="GlassSurface"/> 里的做法。
    ///
    /// 返回的字节数组尺寸是 (width/factor) × (height/factor) × 4，
    /// 通过 out 参数回传实际尺寸（除不尽时向下取整，最小 1）。
    /// </summary>
    public static byte[]? CaptureScaled(int x, int y, int width, int height, int factor,
                                         out int outWidth, out int outHeight)
    {
        if (factor < 1) factor = 1;

        outWidth = Math.Max(1, width / factor);
        outHeight = Math.Max(1, height / factor);

        if (width <= 0 || height <= 0) return null;

        IntPtr screenDc = IntPtr.Zero;
        IntPtr memDc = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;
        IntPtr bits = IntPtr.Zero;

        try
        {
            screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return null;

            memDc = CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero) return null;

            // ★ 用 DIB section：内存指针直接给我们，省掉整趟 GetDIBits
            var header = new BITMAPINFOHEADER
            {
                biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = outWidth,
                biHeight = -outHeight,       // ★ 负 = 自上而下
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
                biSizeImage = (uint)(outWidth * outHeight * 4),
            };

            var info = new BITMAPINFO { bmiHeader = header };

            bitmap = CreateDIBSection(memDc, ref info, DIB_RGB_COLORS, out bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) return null;

            oldBitmap = SelectObject(memDc, bitmap);

            // ★ 必须设 HALFTONE，否则缩放走最近邻，画面会有锯齿和摩尔纹
            SetStretchBltMode(memDc, HALFTONE);

            if (!StretchBlt(memDc, 0, 0, outWidth, outHeight,
                            screenDc, x, y, width, height,
                            SRCCOPY | CAPTUREBLT))
            {
                return null;
            }

            // 直接从 DIB 内存拷出来 —— 没有 GetDIBits 那一趟
            var pixels = new byte[outWidth * outHeight * 4];
            System.Runtime.InteropServices.Marshal.Copy(bits, pixels, 0, pixels.Length);
            return pixels;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero && memDc != IntPtr.Zero)
                SelectObject(memDc, oldBitmap);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    // ── 复用式抓屏器 ────────────────────────────────────────────────
    //
    // ★ 上面那个函数每次调用都要建/销毁 DC 和位图，实测有 ~8ms 的
    //   固定开销。玻璃材质要频繁抓屏（条子还常驻），这个开销不能接受。
    //
    //   所以再提供一个"长命"的抓屏器：DC 和位图建一次，反复用。
    //   用 IDisposable，窗口关掉时释放。

    /// <summary>
    /// 复用的抓屏器 —— DC 和位图只建一次。
    ///
    /// 用法：`using var grabber = new ScreenGrabber(w, h, 4);`
    ///      然后反复 `grabber.Grab(x, y)`。
    ///
    /// 非线程安全：玻璃渲染固定在一个线程上，别跨线程用。
    /// </summary>
    internal sealed class ScreenGrabber : IDisposable
    {
        private IntPtr _screenDc;
        private IntPtr _memDc;
        private IntPtr _bitmap;
        private IntPtr _oldBitmap;
        private IntPtr _bits;

        public int Width { get; }
        public int Height { get; }

        public ScreenGrabber(int width, int height, int factor)
        {
            if (factor < 1) factor = 1;
            Width = Math.Max(1, width / factor);
            Height = Math.Max(1, height / factor);

            _screenDc = GetDC(IntPtr.Zero);
            if (_screenDc == IntPtr.Zero) throw new InvalidOperationException("GetDC 失败");

            _memDc = CreateCompatibleDC(_screenDc);
            if (_memDc == IntPtr.Zero) throw new InvalidOperationException("CreateCompatibleDC 失败");

            var header = new BITMAPINFOHEADER
            {
                biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = Width,
                biHeight = -Height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
                biSizeImage = (uint)(Width * Height * 4),
            };
            var info = new BITMAPINFO { bmiHeader = header };

            _bitmap = CreateDIBSection(_memDc, ref info, DIB_RGB_COLORS, out _bits, IntPtr.Zero, 0);
            if (_bitmap == IntPtr.Zero || _bits == IntPtr.Zero)
                throw new InvalidOperationException("CreateDIBSection 失败");

            _oldBitmap = SelectObject(_memDc, _bitmap);
            SetStretchBltMode(_memDc, HALFTONE);
        }

        /// <summary>抓一次，返回新分配的字节数组（调用方可以留着）。</summary>
        public byte[]? Grab(int x, int y, int srcWidth, int srcHeight)
        {
            if (_memDc == IntPtr.Zero) return null;

            if (!StretchBlt(_memDc, 0, 0, Width, Height,
                            _screenDc, x, y, srcWidth, srcHeight,
                            SRCCOPY | CAPTUREBLT))
            {
                return null;
            }

            var pixels = new byte[Width * Height * 4];
            System.Runtime.InteropServices.Marshal.Copy(_bits, pixels, 0, pixels.Length);
            return pixels;
        }

        public void Dispose()
        {
            if (_oldBitmap != IntPtr.Zero && _memDc != IntPtr.Zero)
                SelectObject(_memDc, _oldBitmap);
            if (_bitmap != IntPtr.Zero) { DeleteObject(_bitmap); _bitmap = IntPtr.Zero; }
            if (_memDc != IntPtr.Zero) { DeleteDC(_memDc); _memDc = IntPtr.Zero; }
            if (_screenDc != IntPtr.Zero) { ReleaseDC(IntPtr.Zero, _screenDc); _screenDc = IntPtr.Zero; }
            _oldBitmap = IntPtr.Zero;
            _bits = IntPtr.Zero;
        }
    }

    /// <summary>抓屏 + 计时。给日志用。</summary>
    public static (byte[]? Pixels, double Ms) CaptureTimed(int x, int y, int width, int height)
    {
        var sw = Stopwatch.StartNew();
        byte[]? px = Capture(x, y, width, height);
        sw.Stop();
        return (px, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// 设置某个窗口对抓屏**可见 / 隐身**（人眼都一样看得见）。
    ///
    /// ══ ★ 一个曾经把用户坑惨的开关 ══════════════════════════════════
    ///
    /// 隐身（`WDA_EXCLUDEFROMCAPTURE`）当初是**必要**的：
    /// 条子常驻显示，抓它背后的画面时会把它自己抓进去，
    /// 玻璃层就会糊住自己上一帧的样子，越叠越脏。
    ///
    /// **但那条抓屏路径已经废了** —— `StripPanelWindow.RenderGlass`
    /// 现在是空方法，背景改由系统亚克力提供，不再自己抓屏。
    /// 保护对象没了，隐身却还挂着，于是它唯一的实效变成：
    ///
    ///   ★ 用户想截图给开发者看调参效果 —— 截图里**唯独少了调参面板**。
    ///
    /// 所以现在由调用方按 `exclude` 显式决定，默认一律**可见**。
    ///
    /// ⚠️ 这是 Win10 2004+ 才有的值；老系统上会返回 false。
    /// </summary>
    /// <returns>是否设置成功。失败时调用方可以退到别的办法。</returns>
    public static bool SetCaptureVisibility(IntPtr hwnd, bool exclude)
    {
        if (hwnd == IntPtr.Zero) return false;
        return SetWindowDisplayAffinity(hwnd, exclude ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
    }

    /// <summary>
    /// 抓回来的图**有没有内容**。
    ///
    /// 用来区分"抓屏坏了"和"这块地方本来就是纯色" —— 没有这个判断，
    /// 全黑会被当成"用户桌面是黑的"，问题就被吞掉了。
    /// </summary>
    public static string Describe(byte[]? pixels, int width, int height)
    {
        if (pixels is null) return "抓屏失败（null）";
        if (pixels.Length != width * height * 4) return $"尺寸不符（{pixels.Length} 字节）";

        int min = 255, max = 0;
        long sum = 0;

        // 每 16 个像素采一个就够看趋势了，别为了一行日志扫全图
        for (int i = 0; i + 3 < pixels.Length; i += 64)
        {
            int lum = (pixels[i] * 29 + pixels[i + 1] * 150 + pixels[i + 2] * 77) >> 8;
            if (lum < min) min = lum;
            if (lum > max) max = lum;
            sum += lum;
        }

        int samples = Math.Max(1, pixels.Length / 64);
        int avg = (int)(sum / samples);

        return max == min
            ? $"纯色（亮度 {min}）—— 可能是真空区域，也可能是抓屏坏了"
            : $"亮度 {min}~{max}，均值 {avg}（有内容）";
    }
}
