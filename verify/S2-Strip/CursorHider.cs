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
///
/// 正解是 SetSystemCursor：直接替换系统级的光标资源。
/// 代价是**它是全局的** —— 改完整个系统都看不到指针了，
/// 所以必须配套一套"无论如何都要还原"的保险。
///
/// ══ 第五轮：为什么要换掉**一整组**光标，而不只是箭头 ═══════════
///
/// 用户反馈：「鼠标在一定范围外又显示了」。
///
/// 根因：九宫格窗口只有 720×720 物理像素，鼠标拖远就离开了我们的窗口，
/// 落到**别的程序**的窗口上。那个程序（记事本、浏览器……）会给光标
/// 设一个自己的样子 —— 而**我们只换了 OCR_NORMAL（标准箭头）**，
/// 换不到它的 I 形光标 / 手形光标。于是指针又冒出来了。
///
/// 修法：把常见的那十几种系统光标**全部**换成透明的。
/// 这样不管鼠标落在谁家的窗口上，它设出来的也是我们那张空白图。
///
/// 三道保险（还原）：
///   1. 每次收起九宫格时还原
///   2. 进程退出时还原（AppDomain.ProcessExit）
///   3. 一个看门狗定时器：万一程序崩了没还原，超时后强制还原
/// </summary>
internal static class CursorHider
{
    private static bool _hidden;
    private static System.Threading.Timer? _watchdog;

    /// <summary>藏了多久之后强制还原（毫秒）。防止程序异常时留下"没有鼠标"的系统。</summary>
    private const int WatchdogMs = 30_000;

    /// <summary>
    /// 要替换成空白的系统光标 id。
    ///
    /// 覆盖了箭头、文本 I 形、手形、十字、等待、各种缩放方向 ——
    /// 也就是日常会遇到的全部。少一个，鼠标经过对应控件时就会露馅。
    /// </summary>
    private static readonly uint[] CursorsToBlank =
    {
        32512,   // OCR_NORMAL      标准箭头
        32513,   // OCR_IBEAM       文本输入（记事本正文）
        32514,   // OCR_WAIT        忙
        32515,   // OCR_CROSS       十字
        32516,   // OCR_UP          向上
        32642,   // OCR_SIZENWSE    左上/右下缩放
        32643,   // OCR_SIZENESW    右上/左下缩放
        32644,   // OCR_SIZEWE      左右缩放
        32645,   // OCR_SIZENS      上下缩放
        32646,   // OCR_SIZEALL     四向移动
        32648,   // OCR_NO          禁止
        32649,   // OCR_HAND        手形（链接、可点元素）
        32650,   // OCR_APPSTARTING 后台忙
        32651,   // OCR_HELP        帮助
    };

    public static bool IsHidden => _hidden;

    /// <summary>藏起来。UI 线程调用。</summary>
    public static string Hide()
    {
        if (_hidden) return "已经是隐藏状态";

        int ok = 0, fail = 0;

        foreach (uint id in CursorsToBlank)
        {
            // ★ 每个 id 都要**单独**造一个空光标。
            //   SetSystemCursor 会接管句柄的所有权（并在替换时销毁旧的），
            //   同一个句柄给两个 id 用会导致其中一个失效。
            IntPtr blank = CreateBlank();
            if (blank == IntPtr.Zero) { fail++; continue; }

            if (SetSystemCursor(blank, id)) ok++;
            else fail++;
        }

        if (ok == 0)
        {
            return $"★ 全部失败（错误码 {Marshal.GetLastWin32Error()}）";
        }

        _hidden = true;
        ArmWatchdog();

        return fail == 0
            ? $"已隐藏（{ok} 种光标全部替换）"
            : $"已隐藏（{ok} 成功 / {fail} 失败）";
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
