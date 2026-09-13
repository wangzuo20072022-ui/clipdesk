using System;
using System.Runtime.InteropServices;

namespace S2Strip;

/// <summary>
/// 剪贴板的裸 Win32 读写。
///
/// 为什么不用 WPF 的 Clipboard.GetText()：
///   1. 它会抛 CLIPBRD_E_CANT_OPEN 异常（S9 风险），我们需要的是"重试几次能成"，不是异常；
///   2. 它有自己的重试与数据格式转换，会掩盖我们想观察的真实行为。
/// 裸 API 能让我们数清楚"第几次才成功"，那是 S9 的实测数字。
/// </summary>
internal static class ClipboardIo
{
    /// <summary>读文本。返回 (文本, 用了几次尝试)。文本为 null 表示彻底失败。</summary>
    public static (string? Text, int Attempts) ReadText(int maxAttempts = 5, int delayMs = 50)
    {
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    if (!NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_UNICODETEXT))
                    {
                        // 剪贴板打开了，但里面不是文本（可能是图片/文件）—— 算成功，只是没内容
                        return (null, attempt);
                    }

                    IntPtr handle = NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT);
                    if (handle == IntPtr.Zero) return (null, attempt);

                    IntPtr ptr = NativeMethods.GlobalLock(handle);
                    if (ptr == IntPtr.Zero) return (null, attempt);

                    try
                    {
                        string? text = Marshal.PtrToStringUni(ptr);
                        return (text, attempt);
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
            }

            // 打开失败 —— 别的程序正占着剪贴板（CLIPBRD_E_CANT_OPEN），等一下再试
            System.Threading.Thread.Sleep(delayMs);
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
