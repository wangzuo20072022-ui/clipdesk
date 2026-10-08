using System;
using Microsoft.Win32;

namespace ClipDesk;

/// <summary>
/// 开机自启开关。写的是当前用户的 Run 键，**不需要管理员权限**。
///
/// ══ 为什么用 HKCU 而不是 HKLM ══════════════════════════════════
///
///   HKLM 那份需要管理员权限才能写，而 ClipDesk 从安装到运行全程
///   都不提权（安装包也是 lowest 权限装到用户目录）。
///   为了一个自启开关去要 UAC，用户会看到一个莫名其妙的提权弹窗，
///   而收益只是"所有用户都自启"——对一个剪贴板工具毫无意义。
///
/// ══ 路径怎么来的 ═══════════════════════════════════════════════
///
///   用 <see cref="Environment.ProcessPath"/> 而不是 `Assembly.Location`：
///   后者在单文件发布下会返回空字符串。
///   写进去的值**必须带引号** —— 安装路径默认是
///   `C:\Users\某某\AppData\Local\Programs\ClipDesk\ClipDesk.exe`，
///   中间有空格，不带引号 Windows 会把路径截断到第一个空格。
/// </summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClipDesk";

    /// <summary>当前是否已启用开机自启。读不到（权限/键不存在）一律当作未启用。</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            if (key is null) return false;

            return key.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>开启开机自启。返回是否成功。</summary>
    public static bool Enable()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return false;

            key.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>关闭开机自启。键本来就不存在也算成功。</summary>
    public static bool Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return true;

            if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>按勾选状态设置。</summary>
    public static bool Set(bool enabled) => enabled ? Enable() : Disable();
}
