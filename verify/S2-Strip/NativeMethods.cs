using System;
using System.Runtime.InteropServices;
using System.Text;

namespace S2Strip;

/// <summary>
/// S2 条子探针所需的全部 Win32 声明。
///
/// 从 S0-Spine 的 NativeMethods.cs 精简而来 —— 只保留这条链路用得上的，
/// 另外补了几个条子专用的（穿透、命中测试、窗口矩形）。
/// </summary>
internal static class NativeMethods
{
    // ── 窗口扩展样式 ────────────────────────────────────────────────
    internal const int GWL_EXSTYLE = -20;
    internal const int WS_EX_NOACTIVATE = 0x08000000;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_EX_TOPMOST = 0x00000008;

    /// <summary>
    /// 鼠标穿透用的样式位。
    ///
    /// ⚠️ 单独用它的效果**有不确定性** —— 历史上它常和 WS_EX_LAYERED 配对使用，
    ///    很多人报告说单独加它不生效。所以这是一条**要实测**的路，
    ///    退路是拦 WM_NCHITTEST 返回 HTTRANSPARENT（见 WndProc）。
    /// </summary>
    internal const int WS_EX_TRANSPARENT = 0x00000020;

    // ── ShowWindow ─────────────────────────────────────────────────
    internal const int SW_HIDE = 0;
    internal const int SW_SHOWNOACTIVATE = 4;

    // ── RegisterHotKey ──────────────────────────────────────────────
    internal const uint MOD_ALT = 0x0001;
    internal const uint MOD_CONTROL = 0x0002;
    internal const uint MOD_SHIFT = 0x0004;
    internal const uint MOD_NOREPEAT = 0x4000;
    internal const uint VK_V = 0x56;
    /// <summary>逃生热键用：Ctrl+Alt+Q</summary>
    internal const uint VK_Q = 0x51;
    /// <summary>玻璃调参面板用：Ctrl+Alt+G</summary>
    internal const uint VK_G = 0x47;

    // ── 窗口消息 ────────────────────────────────────────────────────
    internal const int WM_HOTKEY = 0x0312;
    internal const int WM_CLIPBOARDUPDATE = 0x031D;
    internal const int WM_NCHITTEST = 0x0084;
    internal const int WM_MOUSEWHEEL = 0x020A;
    internal const int WM_MOUSEACTIVATE = 0x0021;
    internal const int WM_DPICHANGED = 0x02E0;
    internal const int WM_GETMINMAXINFO = 0x0024;

    /// <summary>WM_NCHITTEST 的返回值：这次点击不是给我的，转给下面的窗口</summary>
    internal const int HTTRANSPARENT = -1;

    /// <summary>WM_MOUSEACTIVATE 的返回值：激活我，但**不要**把消息发给我（不抢焦点）</summary>
    internal const int MA_NOACTIVATE = 3;

    // ── DwmSetWindowAttribute ───────────────────────────────────────
    internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    internal const int DWMWA_BORDER_COLOR = 34;
    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    internal const int DWMWCP_ROUND = 2;

    /// <summary>
    /// 不要圆角。
    ///
    /// ★ 这是"条子下面有一块黑色阴影"的头号嫌疑。
    ///   DWM 的圆角半径约 8px，而收起态窗口只有 11px 高 ——
    ///   圆角比窗口还高，DWM 在这种扁窗口上画圆角就会产生黑边/黑块。
    ///   2mm 的线根本不需要圆角，直接关掉。
    /// </summary>
    internal const int DWMWCP_DONOTROUND = 1;

    /// <summary>非客户区渲染策略</summary>
    internal const int DWMWA_NCRENDERING_POLICY = 2;

    /// <summary>关掉非客户区渲染 —— 去掉 DWM 给窗口画的投影（第二个嫌疑）</summary>
    internal const int DWMNCRP_DISABLED = 1;

    internal const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

    // ── GetAsyncKeyState ────────────────────────────────────────────
    internal const int VK_LMENU = 0xA4;
    internal const int VK_RMENU = 0xA5;
    internal const int VK_ESCAPE = 0x1B;

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    internal const uint MONITOR_DEFAULTTONEAREST = 2;

    // ── user32 ─────────────────────────────────────────────────────
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_SHOWWINDOW = 0x0040;
    internal static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("shcore.dll")]
    internal static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    internal const int MDT_EFFECTIVE_DPI = 0;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool ShowWindow(IntPtr hWnd, int cmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ── SendInput（九宫格松手后要模拟 Ctrl+V）────────────────────────
    internal const uint INPUT_KEYBOARD = 1;
    internal const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    /// <summary>
    /// INPUT 是个联合体，**体积由最大的成员决定**。
    ///
    /// ★ 这里是 SendInput 一直返回 0 的根因（S0 踩过）：
    ///   如果联合体里只声明 KEYBDINPUT，x64 下 Marshal.SizeOf 算出 32，
    ///   但系统的 INPUT 实际是 40（MOUSEINPUT 更大）。
    ///   SendInput 校验 cbSize 不符 → **直接返回 0，一个事件都不送，而且不报错**。
    ///
    ///   把 MOUSEINPUT 也放进来，尺寸自然就对了。
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    internal struct INPUT
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public KEYBDINPUT Keyboard;
        [FieldOffset(8)] public MOUSEINPUT Mouse;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetFocus();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern short GetAsyncKeyState(int vKey);

    /// <summary>
    /// 整批替换系统级鼠标光标。这是"光标是每个窗口自己的"这个事实的正解。
    ///
    /// 为什么 SetCursor 不行（S0 试了两轮）：
    ///   光标归属于**某个窗口**，只有那个窗口是前台/覆盖时系统才用它。
    ///   九宫格是 WS_EX_NOACTIVATE —— 永远不是前台窗口，所以设了也白设。
    ///
    /// ⚠️ 它会真的改掉系统光标，退出前**必须** SystemParametersInfo(SPI_SETCURSORS)
    ///    还原，否则用户重启前都看不到鼠标指针。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetSystemCursor(IntPtr hcur, uint id);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    internal const uint OCR_NORMAL = 32512;
    internal const uint SPI_SETCURSORS = 0x0057;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool AddClipboardFormatListener(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RemoveClipboardFormatListener(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetClipboardData(uint format, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool IsClipboardFormatAvailable(uint format);

    /// <summary>
    /// 窗口在不在可见状态。**注意区分**：这个只说明 WS_VISIBLE 位，
    /// 不代表屏幕上真的画出了东西 —— S0 就是因为只看这个才误判过两次。
    /// </summary>
    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref int pvParam, uint fWinIni);

    /// <summary>鼠标滚轮路由设置。见 S2 的 Q5。</summary>
    internal const uint SPI_GETMOUSEWHEELROUTING = 0x201C;

    // ── dwmapi ─────────────────────────────────────────────────────
    [DllImport("dwmapi.dll", PreserveSig = true)]
    internal static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int value, int size);

    // ── kernel32（剪贴板内存）─────────────────────────────────────
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GlobalUnlock(IntPtr hMem);

    /// <summary>
    /// 这块全局内存有多大（字节）。
    /// 用来确认"读到的东西不是空的" —— 剪贴板的写入方是分两步交接的，
    /// 中间态会拿到一个存在但为空的句柄。
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint GlobalSize(IntPtr hMem);

    internal const uint GMEM_MOVEABLE = 0x0002;
    internal const uint CF_UNICODETEXT = 13;

    // ── GDI 抓屏（玻璃材质用）───────────────────────────────────────
    //
    // 为什么需要抓屏：苹果的「液态玻璃」核心是**折射背景** —— 玻璃边缘
    // 把背后的画面放大扭曲。浏览器里这是 backdrop-filter 干的活，
    // WPF / Win32 **没有等价物**，所以只能自己把背景像素抓回来处理。
    //
    // 链路：GetDC(屏幕) → 兼容DC → 兼容位图 → BitBlt → GetDIBits → byte[]
    //
    // ★ 本进程是 PerMonitorV2（见 app.manifest），所以这里全部是**物理像素**，
    //   和 GetCursorPos / SetWindowPos 用的是同一套坐标，不用换算。

    [DllImport("user32.dll")]
    internal static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest,
                                       int width, int height,
                                       IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines,
                                         byte[] bits, ref BITMAPINFO bmi, uint usage);

    /// <summary>
    /// 带缩放的块传送 —— **抓屏时顺手降采样**。
    ///
    /// 为什么不先抓全尺寸再自己降：抓 720×720 要传 2MB 像素，实测 15.5ms；
    /// 直接抓到 180×180 只传 130KB，快一个数量级。
    /// 反正下一步就是模糊，本来也不需要全分辨率。
    ///
    /// ★ 必须配 <see cref="SetStretchBltMode"/> = HALFTONE，
    ///   否则缩小的时候是**最近邻抽样**（丢掉大部分像素），
    ///   画面会出现明显的锯齿和摩尔纹。
    /// </summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern bool StretchBlt(IntPtr hdcDest, int xDest, int yDest,
                                           int wDest, int hDest,
                                           IntPtr hdcSrc, int xSrc, int ySrc,
                                           int wSrc, int hSrc, uint rop);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern int SetStretchBltMode(IntPtr hdc, int mode);

    /// <summary>缩放时做平均（比默认的 COLORONCOLOR 好看得多）</summary>
    internal const int HALFTONE = 4;

    /// <summary>
    /// 建一块**可以直接读写的** DIB（设备无关位图）。
    ///
    /// ★ 为什么不用 CreateCompatibleBitmap + GetDIBits：
    ///   实测 GetDIBits 那一趟往返很贵（驱动层拷贝），而整条抓屏链路
    ///   的固定开销本来就有 8ms 左右。
    ///   CreateDIBSection 直接把位图内存的指针交给我们（ppvBits），
    ///   BitBlt 画完就能直接读 —— **省掉整趟 GetDIBits**。
    ///
    /// 用完要 DeleteObject 释放，但 ppvBits 那块内存由 GDI 管理，
    /// 不能自己 free。
    /// </summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi,
                                                   uint usage, out IntPtr ppvBits,
                                                   IntPtr hSection, uint offset);

    /// <summary>直接拷贝源像素</summary>
    internal const uint SRCCOPY = 0x00CC0020;

    /// <summary>
    /// ★ 把**分层窗口**也一起抓进来。
    ///
    /// 不加这个标志，BitBlt 会漏掉 WS_EX_LAYERED 的窗口（很多现代应用
    /// 和所有 WPF 透明窗口都是），抓出来的背景会有空洞。
    /// </summary>
    internal const uint CAPTUREBLT = 0x40000000;

    internal const uint BI_RGB = 0;
    internal const uint DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    /// <summary>
    /// 让窗口**对抓屏不可见**，但人眼照样看得见。
    ///
    /// 用途：触发条是常驻显示的，抓它背后的画面时会把**它自己**抓进去，
    /// 那样玻璃层就会糊住自己上一帧的样子，越叠越脏。
    /// 加上这个之后 BitBlt 返回的是它背后真正的桌面内容。
    ///
    /// ⚠️ **副作用**：用户自己截图时，触发条也不会出现。
    ///    这是 Win10 2004+ 才有的值。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    internal const uint WDA_NONE = 0x00000000;
    internal const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    /// <summary>屏幕尺寸（物理像素）。0 = 宽，1 = 高。</summary>
    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int nIndex);

    // ── 小工具 ─────────────────────────────────────────────────────
    internal static string GetWindowTitle(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        GetWindowTextW(hWnd, sb, sb.Capacity);
        return sb.Length == 0 ? "(无标题)" : sb.ToString();
    }

    internal static uint GetWindowPid(IntPtr hWnd)
    {
        GetWindowThreadProcessId(hWnd, out uint pid);
        return pid;
    }
}
