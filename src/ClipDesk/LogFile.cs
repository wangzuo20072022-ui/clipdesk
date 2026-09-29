using System;
using System.IO;

namespace ClipDesk;

/// <summary>
/// 极简运行日志：写到 <c>%LocalAppData%\ClipDesk\clipdesk.log</c>。
///
/// 存在的理由：ClipDesk 是 WinExe，没有控制台。
/// 启动期的诊断（热键被占用、剪贴板监听失败……）既不能弹窗打断用户，
/// 也不能像探针那样打到控制台 —— 只能落一个文件。
///
/// 纪律：
///   · **绝不记录剪贴板内容**，只记事件和错误。
///   · 写失败一律吞掉 —— 日志问题绝不能让主程序崩。
///   · 超过 <see cref="MaxBytes"/> 就从头截断，不让它无限长大。
/// </summary>
internal static class LogFile
{
    private const long MaxBytes = 256 * 1024;

    private static readonly object Gate = new();

    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClipDesk", "clipdesk.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                string path = Path;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    File.Delete(path);
                }

                File.AppendAllText(path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // 日志写不进去不影响任何功能。
        }
    }
}
