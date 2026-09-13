using System;
using System.Runtime.InteropServices;
using System.Text;

namespace S0Spine;

/// <summary>
/// S0 脊柱验证所需的全部 Win32 声明。
///
/// 正式项目里这些会落在 src\ClipDesk\Platform\ —— 这里先按同样的规矩集中放一处，
/// 验证代码不做 P/Invoke 的散落。
/// </summary>
internal static class NativeMethods
{
    // ── 窗口扩展样式 ────────────────────────────────────────────────
    internal const int GWL_EXSTYLE = -20;
    internal const int WS_EX_NOACTIVATE = 0x08000000;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_EX_TOPMOST = 0x00000008;

    // ── ShowWindow ─────────────────────────────────────────────────
    internal const int SW_HIDE = 0;
    /// <summary>显示但不激活 —— Q1 的关键参数</summary>
    internal const int SW_SHOWNOACTIVATE = 4;
    internal const int SW_SHOWNA = 8;

    // ── RegisterHotKey 修饰键 ───────────────────────────────────────
    internal const uint MOD_ALT = 0x0001;
    internal const uint MOD_CONTROL = 0x0002;
    internal const uint MOD_SHIFT = 0x0004;
    internal const uint MOD_NOREPEAT = 0x4000;
    internal const uint VK_V = 0x56;

    // ── 窗口消息 ────────────────────────────────────────────────────
    internal const int WM_HOTKEY = 0x0312;
    internal const int WM_CLIPBOARDUPDATE = 0x031D;
    internal const int WM_DESTROY = 0x0002;

    // ── DwmSetWindowAttribute 属性号 ────────────────────────────────
    internal const int DWMWA_CLOAK = 13;
    internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    internal const int DWMWA_BORDER_COLOR = 34;
    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    internal const int DWMWCP_ROUND = 2;
    internal const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

    // ── SendInput ───────────────────────────────────────────────────
    internal const uint INPUT_KEYBOARD = 1;
    internal const uint KEYEVENTF_KEYUP = 0x0002;

    // ── GetAsyncKeyState 虚拟键 ─────────────────────────────────────
    internal const int VK_LMENU = 0xA4;
    internal const int VK_RMENU = 0xA5;
    internal const int VK_ESCAPE = 0x1B;

    // ── 进程权限级别（Q4 用）────────────────────────────────────────
    internal const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

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
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    internal const uint MONITOR_DEFAULTTONEAREST = 2;
    internal const uint MONITOR_DEFAULTTOPRIMARY = 1;

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
    /// ★ 这里是 SendInput 一直返回 0 的根因：
    ///   如果联合体里只声明 KEYBDINPUT，x64 下 Marshal.SizeOf 算出 32，
    ///   但系统的 INPUT 实际是 40（MOUSEINPUT 更大）。
    ///   SendInput 校验 cbSize 不符 → 直接返回 0，一个事件都不送。
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

    // ── user32 ─────────────────────────────────────────────────────
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_SHOWWINDOW = 0x0040;
    internal static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetDpiForWindow(IntPtr hWnd);

    /// <summary>
    /// 拿某个显示器的 DPI。Shcore.dll 的 GetDpiForMonitor。
    /// 用它而不是 GetDpiForWindow —— 窗口还没显示时，我们不知道它最终落在哪个屏上。
    /// </summary>
    [DllImport("shcore.dll")]
    internal static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    internal const int MDT_EFFECTIVE_DPI = 0;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetFocus();

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

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetCursorPos(out POINT point);

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

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint count, INPUT[] inputs, int size);

    /// <summary>
    /// 整批设置鼠标光标。这是"光标是每个窗口自己的"这个事实的正解。
    ///
    /// 为什么 SetCursor 不行：
    ///   光标归属于**某个窗口**，只有当那个窗口是前台/覆盖时系统才用它。
    ///   我们的九宫格是 WS_EX_NOACTIVATE —— 永远不是前台窗口，
    ///   所以 SetCursor 设了也白设，用户看到的一直是原窗口的光标。
    ///
    /// SetSystemCursor 直接替换**系统级**光标资源，绕开那套归属逻辑。
    /// ⚠️ 它会真的改掉系统光标，所以退出前必须调 SystemParametersInfo(SPI_SETCURSORS)
    ///    把系统光标还原 —— 否则用户重启前都看不到鼠标指针。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetSystemCursor(IntPtr hcur, uint id);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    internal const uint OCR_NORMAL = 32512;
    internal const uint SPI_SETCURSORS = 0x0057;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetOpenClipboardWindow();

    // ── dwmapi ─────────────────────────────────────────────────────
    [DllImport("dwmapi.dll", PreserveSig = true)]
    internal static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int value, int size);

    // ── kernel32 ───────────────────────────────────────────────────
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(int access, bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr handle);

    internal const uint GMEM_MOVEABLE = 0x0002;
    internal const uint CF_UNICODETEXT = 13;

    // ── advapi32（Q4：判断目标窗口是不是管理员权限）──────────────────
    internal const int TokenIntegrityLevel = 25;
    internal const int SE_GROUP_INTEGRITY = 0x00000020;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SID_AND_ATTRIBUTES
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TOKEN_MANDATORY_LABEL
    {
        public SID_AND_ATTRIBUTES Label;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool GetTokenInformation(
        IntPtr token, int infoClass, IntPtr info, int infoLength, out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    internal const int TOKEN_QUERY = 0x0008;

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

    /// <summary>
    /// Q4 用：读目标进程的完整性级别。
    /// >= 0x3000（High）就是"管理员跑的程序"，UIPI 会拦我们的 SendInput。
    /// 读失败时返回 -1 —— 那是"S4 风险"的候选证据，不要当成 0。
    /// </summary>
    internal static int GetIntegrityLevel(uint pid)
    {
        IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero)
        {
            return -1;
        }

        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(process, TOKEN_QUERY, out token))
            {
                return -1;
            }

            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out int needed);
            if (needed <= 0)
            {
                return -1;
            }

            IntPtr buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, needed, out _))
                {
                    return -1;
                }

                var label = Marshal.PtrToStructure<TOKEN_MANDATORY_LABEL>(buffer);
                IntPtr countPtr = GetSidSubAuthorityCount(label.Label.Sid);
                byte count = Marshal.ReadByte(countPtr);
                IntPtr ridPtr = GetSidSubAuthority(label.Label.Sid, (uint)(count - 1));
                return Marshal.ReadInt32(ridPtr);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
            CloseHandle(process);
        }
    }

    /// <summary>把完整性级别 RID 翻译成人看的词</summary>
    internal static string DescribeIntegrity(int level) => level switch
    {
        -1 => "读不到",
        < 0x2000 => "Low/Untrusted",
        < 0x3000 => "Medium（普通程序）",
        < 0x4000 => "High（管理员）",
        _ => "System"
    };
}
