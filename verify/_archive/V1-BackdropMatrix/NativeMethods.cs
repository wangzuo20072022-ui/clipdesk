using System;
using System.Runtime.InteropServices;

namespace V1BackdropMatrix;

/// <summary>
/// 本文件是整个验证程序里唯一允许出现 P/Invoke 的地方。
/// 真实项目里这条规矩会写进 CLAUDE.md：Platform\ 层独占 P/Invoke，UI 层不碰。
/// </summary>
internal static class NativeMethods
{
    // ───────────────────────────── DWM ─────────────────────────────

    /// <summary>DwmExtendFrameIntoClientArea 的入参。四个 -1 表示「整个窗口都当玻璃区」。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    /// <summary>
    /// 把 DWM 玻璃区扩展到整个窗口（含客户区）。
    /// 官方 SystemBackdrop 示例里这步是必须的 —— 不调用的话，
    /// 客户区会被当成不透明内容，材质根本没地方透出来。
    /// </summary>
    public static int ExtendFrameIntoClientArea(IntPtr hwnd)
    {
        var m = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        return DwmExtendFrameIntoClientArea(hwnd, ref m);
    }

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_BORDER_COLOR = 34;
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    public const int DWMWCP_DEFAULT = 0;
    public const int DWMWCP_DONOTROUND = 1;
    public const int DWMWCP_ROUND = 2;
    public const int DWMWCP_ROUNDSMALL = 3;

    public const uint DWMWA_COLOR_DEFAULT = 0xFFFFFFFF;
    public const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

    /// <summary>DWMSBT_AUTO=0, DWMSBT_NONE=1, DWMSBT_MAINWINDOW(Mica)=2, DWMSBT_TRANSIENTWINDOW(Acrylic)=3, DWMSBT_TABBEDWINDOW=4</summary>
    public const int DWMSBT_ACRYLIC = 3;

    // 同一个 API 有 int / uint / bool 三种入参形态，这里用 EntryPoint 分开命名，
    // 避免 C# 的 ref 重载解析歧义。
    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute", PreserveSig = true)]
    public static extern int DwmSetInt(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute", PreserveSig = true)]
    public static extern int DwmSetUInt(IntPtr hwnd, int attr, ref uint value, int size);

    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute", PreserveSig = true)]
    public static extern int DwmSetBool(IntPtr hwnd, int attr, [MarshalAs(UnmanagedType.Bool)] ref bool value, int size);

    // ──────────────────── 旧版亚克力（Win10 1803+） ────────────────────

    public enum AccentState
    {
        Disabled = 0,
        Gradient = 1,
        TransparentGradient = 2,
        BlurBehind = 3,
        AcrylicBlurBehind = 4,
        Invalid = 5,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;   // 注意：格式是 ABGR，不是 RGBA
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WindowCompositionAttributeData
    {
        public int Attribute;        // WCA_ACCENT_POLICY = 19
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    /// <summary>套用 AccentPolicy。GradientColor 请用 AABBGGRR 顺序。</summary>
    public static void ApplyAccent(IntPtr hwnd, AccentState state, uint abgrGradientColor)
    {
        var accent = new AccentPolicy
        {
            AccentState = (int)state,
            AccentFlags = 2,               // 让渐变覆盖整个窗口，而不是只画边框
            GradientColor = abgrGradientColor,
            AnimationId = 0,
        };

        int size = Marshal.SizeOf<AccentPolicy>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = 19,            // WCA_ACCENT_POLICY
                Data = ptr,
                SizeOfData = size,
            };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    // ───────────────────────── 区域裁剪 ─────────────────────────

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseW, int ellipseH);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr hRgn, bool redraw);

    /// <summary>
    /// 用一个圆角矩形裁掉窗口的多余部分。
    /// 注意：区域是 1-bit 的，圆角会是锯齿 —— 这是 DWM 圆角之外的退路，
    /// 两者不要混用。
    /// </summary>
    public static void ApplyRoundRectRegion(IntPtr hwnd, int width, int height, int radius)
    {
        // CreateRoundRectRgn 的 right/bottom 要 +1 才是闭区间，否则右下少一像素
        IntPtr hRgn = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
        SetWindowRgn(hwnd, hRgn, true);

        // SetWindowRgn 之后，这个 region 归系统所有，但句柄仍需我们自己释放
        // —— 否则每跑一次泄漏一个 GDI 对象。
        // 实测：SetWindowRgn 成功后系统持有一份拷贝，所以这里释放是安全的。
        DeleteObject(hRgn);
    }

    /// <summary>胶囊形：radius 传高度的一半。</summary>
    public static void ApplyCapsuleRegion(IntPtr hwnd, int width, int height)
        => ApplyRoundRectRegion(hwnd, width, height, Math.Max(1, height / 2));
}
