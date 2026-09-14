using System;
using System.Runtime.InteropServices;
using static S2Strip.NativeMethods;

namespace S2Strip;

/// <summary>
/// 隐藏 / 恢复鼠标指针。
///
/// 为什么不能用 SetCursor（试过，真的不行）：
///   光标是**属于某个窗口**的。系统在绘制光标时，看的是"哪个窗口覆盖了光标位置"，
///   而不是"谁最后调了 SetCursor"。
///   我们的九宫格是 WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW —— **永远不是前台窗口**，
///   所以 SetCursor 设的是"我们自己的"光标，系统压根不用它。
///   这就是为什么藏了两次都没效果。
///
/// 正解是 SetSystemCursor：直接替换系统级的 OCR_NORMAL 光标资源。
/// 代价是**它是全局的** —— 改完整个系统都看不到指针了，
/// 所以必须配套一套"无论如何都要还原"的保险。
///
/// 三道保险：
///   1. 每次收起九宫格时还原
///   2. 进程退出时还原（AppDomain.ProcessExit）
///   3. 一个看门狗定时器：万一程序崩了没还原，超时后强制还原
/// </summary>
internal static class CursorHider
{
    private static IntPtr _blankCursor;
    private static bool _hidden;
    private static System.Threading.Timer? _watchdog;

    /// <summary>藏了多久之后强制还原（毫秒）。防止程序异常时留下"没有鼠标"的系统。</summary>
    private const int WatchdogMs = 30_000;

    public static bool IsHidden => _hidden;

    /// <summary>藏起来。UI 线程调用。</summary>
    public static string Hide()
    {
        if (_hidden) return "已经是隐藏状态";

        _blankCursor = CreateBlank();
        if (_blankCursor == IntPtr.Zero)
        {
            return $"★ 造空光标失败（错误码 {Marshal.GetLastWin32Error()}）";
        }

        // SetSystemCursor 会**接管**这个句柄；之后不能再 DestroyCursor 它，
        // 要还原只能靠 SPI_SETCURSORS。
        if (!SetSystemCursor(_blankCursor, OCR_NORMAL))
        {
            return $"★ SetSystemCursor 失败（错误码 {Marshal.GetLastWin32Error()}）";
        }

        _hidden = true;
        ArmWatchdog();
        return "已隐藏";
    }

    /// <summary>还原。可重复调用。</summary>
    public static void Restore()
    {
        if (!_hidden) return;

        // 用系统内置光标重置全部系统光标 —— 这是唯一可靠的还原方式，
        // 因为 SetSystemCursor 把句柄交出去了，DestroyCursor 已经不管用。
        SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0);

        _hidden = false;
        _watchdog?.Dispose();
        _watchdog = null;
    }

    /// <summary>装看门狗：超时还没还原就强制还原</summary>
    private static void ArmWatchdog()
    {
        _watchdog?.Dispose();
        _watchdog = new System.Threading.Timer(
            _ => Restore(), null, WatchdogMs, System.Threading.Timeout.Infinite);
    }

    /// <summary>
    /// 造一张 32×32 全透明光标。
    /// CreateCursor 要求 32×32 单色位图：AND 掩码全 1、XOR 掩码全 0 = 全透明。
    /// 掩码每行占 4 字节（32 位），共 32 行 → 128 字节。
    /// </summary>
    private static IntPtr CreateBlank()
    {
        var andMask = new byte[32 * 4];
        var xorMask = new byte[32 * 4];
        for (int i = 0; i < andMask.Length; i++)
        {
            andMask[i] = 0xFF;    // AND=1 → 保留背景
            xorMask[i] = 0x00;    // XOR=0 → 不叠加 → 结果全透明
        }

        return CreateCursor(IntPtr.Zero, 0, 0, 32, 32, andMask, xorMask);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateCursor(
        IntPtr hInst, int xHotSpot, int yHotSpot, int nWidth, int nHeight,
        byte[] pvANDPlane, byte[] pvXORPlane);

    /// <summary>进程退出时的兜底还原 —— 绝不能给用户留下一个没有鼠标的系统</summary>
    static CursorHider()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Restore();
    }
}
