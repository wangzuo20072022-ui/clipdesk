using System;
using System.Runtime.InteropServices;

namespace S2Strip;

/// <summary>
/// 剪贴板的裸 Win32 读写。
///
/// 为什么不用 WPF 的 Clipboard.GetText()：
///   它会抛 CLIPBRD_E_CANT_OPEN 异常（S9 风险），我们需要的是"重试几次能成"，不是异常。
///
/// ══ 【第二轮实测发现的真 bug】══════════════════════════════════
///
/// 用户反馈「复制的东西根本不显示」。日志里 25 个事件**全是**
/// 「非文本或读不到」，一条都没读到。
///
/// 我写了一个独立探针复刻这里的读取逻辑，抓到现场：
///
///     事件#1 序列号=1546 → ✅ 读到 "复刻测试1" 尝试1次
///     事件#2 序列号=1549 → ★ 开得了剪贴板，但 CF_UNICODETEXT 不在！
///     事件#3 第1次尝试：OpenClipboard 失败 err=5
///     事件#4 序列号=1556 → ✅ 读到 "复刻测试2" 尝试2次
///
/// **根因是这里有两个错，叠加成了"全灭"：**
///
///   ① 「格式不在」这条路上**直接 return，不重试**。
///      但写入方（记事本 / 浏览器）是分两步交接的：
///      先 EmptyClipboard 再 SetClipboardData。中间那个瞬间，
///      剪贴板**开得了、但目标格式还没放进去** ——
///      于是 IsClipboardFormatAvailable 返回 false，我们扭头就走。
///
///   ② 上层的 100ms 去抖把**第一个事件整个丢掉**。
///      而第一个事件恰恰是"格式已经就位、内容已经填好"的那一次。
///      丢掉它，剩下的全是中间的半成品状态 → 必然全灭。
///
/// **修法：**
///   · 「格式不在」不再早退，改成等一下重试（它只是个中间态，不是终态）
///   · 去抖窗口从 100ms 降到 20ms（写入方交接只需要几十毫秒）
///   · 每次读到后，用 GlobalSize 确认内容非空再认账
/// ══════════════════════════════════════════════════════════════
/// </summary>
internal static class ClipboardIo
{
    /// <summary>读文本。返回 (文本, 用了几次尝试)。文本为 null 表示彻底失败。</summary>
    public static (string? Text, int Attempts) ReadText(int maxAttempts = 5, int delayMs = 40)
    {
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            // ── 打开剪贴板 ──
            // 失败一般是别的程序正占着（CLIPBRD_E_CANT_OPEN），等一下再试
            if (!NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                if (attempt < maxAttempts) System.Threading.Thread.Sleep(delayMs);
                continue;
            }

            string? result = null;
            bool gotText = false;

            try
            {
                // ★ 关键修正：格式不在**不是**终态，是写入方还没填完的中间态。
                //   早退就会读到一半就放弃 —— 这正是"复制的东西根本不显示"的根因。
                if (!NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_UNICODETEXT))
                {
                    if (attempt < maxAttempts) System.Threading.Thread.Sleep(delayMs);
                    continue;                    // ← 以前这里是 return，错的
                }

                IntPtr handle = NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT);
                if (handle == IntPtr.Zero)
                {
                    if (attempt < maxAttempts) System.Threading.Thread.Sleep(delayMs);
                    continue;
                }

                IntPtr ptr = NativeMethods.GlobalLock(handle);
                if (ptr == IntPtr.Zero)
                {
                    if (attempt < maxAttempts) System.Threading.Thread.Sleep(delayMs);
                    continue;
                }

                try
                {
                    uint bytes = NativeMethods.GlobalSize(handle);
                    result = Marshal.PtrToStringUni(ptr);

                    // ★ 第二道确认：句柄在、内存锁住了，但内容可能还是空的。
                    //   用 GlobalSize 核对一下，避免把半成品当成有效内容收下来。
                    gotText = bytes > 2;         // >2 是因为至少要有 "x\0" 两个字节
                }
                finally
                {
                    NativeMethods.GlobalUnlock(handle);
                }
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }

            if (gotText)
            {
                return (result, attempt);
            }

            // 拿到了但内容是空的，或者中途失败 —— 也等一下再试
            if (attempt < maxAttempts) System.Threading.Thread.Sleep(delayMs);
        }

        return (null, maxAttempts);
    }

    /// <summary>写文本进去。返回是否成功。</summary>
    public static bool WriteText(string text)
    {
        if (!NativeMethods.OpenClipboard(IntPtr.Zero)) return false;

        try
        {
            if (!NativeMethods.EmptyClipboard()) return false;

            int bytes = (text.Length + 1) * 2;   // UTF-16 + 结尾 \0
            IntPtr hMem = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (UIntPtr)bytes);
            if (hMem == IntPtr.Zero) return false;

            IntPtr target = NativeMethods.GlobalLock(hMem);
            if (target == IntPtr.Zero)
            {
                NativeMethods.GlobalFree(hMem);
                return false;
            }

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                Marshal.WriteInt16(target, text.Length * 2, 0);   // 结尾 \0
            }
            finally
            {
                NativeMethods.GlobalUnlock(hMem);
            }

            // 成功后所有权归系统，不能 GlobalFree
            if (NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, hMem) == IntPtr.Zero)
            {
                NativeMethods.GlobalFree(hMem);
                return false;
            }

            return true;
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }
}
