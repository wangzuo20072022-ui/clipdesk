using System;
using System.Drawing;
using System.Windows.Forms;

namespace ClipDesk;

/// <summary>
/// 右下角托盘图标 + 它的弹出菜单。
///
/// ══ 为什么用 WinForms 的 NotifyIcon ══════════════════════════════
///
///   手写 `Shell_NotifyIcon` 要自己管消息窗口、版本协商、任务栏重建
///   （explorer.exe 崩溃重启后图标会消失，得处理 `TaskbarCreated` 消息）。
///   `NotifyIcon` 这些全替我们做了。
///
///   代价是引入 WinForms —— 但自包含发布本来就打包了整个
///   `Microsoft.WindowsDesktop.App`，`System.Windows.Forms.dll`
///   早就在发布目录里躺着，用它**不增加一个字节**。
///
/// ══ 交互 ════════════════════════════════════════════════════════
///
///   左键单击 / 右键单击 → 都在鼠标位置弹出同一个菜单。
///   （Windows 惯例是右键弹菜单，但用户明确要"左键弹菜单"，
///     那就两个键都弹 —— 不跟用户的直觉打架。）
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _autoStartItem;

    /// <param name="onOpenSettings">点「设置…」时调用</param>
    /// <param name="onExit">点「退出 ClipDesk」时调用</param>
    public TrayIcon(Action onOpenSettings, Action onExit)
    {
        _autoStartItem = new ToolStripMenuItem("开机自动启动")
        {
            CheckOnClick = false,
            Checked = AutoStart.IsEnabled(),
        };
        _autoStartItem.Click += (_, _) =>
        {
            bool want = !_autoStartItem.Checked;
            bool ok = AutoStart.Set(want);

            // ★ 只有真的写成功了才改勾选状态。
            //   写失败还打勾，用户会以为设上了，下次开机发现没启动 —— 更糟。
            _autoStartItem.Checked = ok && want;
        };

        var settingsItem = new ToolStripMenuItem("设置…");
        settingsItem.Click += (_, _) => onOpenSettings();

        var exitItem = new ToolStripMenuItem("退出 ClipDesk");
        exitItem.Click += (_, _) => onExit();

        _menu = new ContextMenuStrip();
        _menu.Items.Add(settingsItem);
        _menu.Items.Add(_autoStartItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(exitItem);

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "ClipDesk",
            Visible = true,
            ContextMenuStrip = _menu,
        };

        // 左键单击也弹菜单，位置跟右键一致
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                // 打开菜单前重新读一次注册表 —— 用户可能在设置中心里改过
                _autoStartItem.Checked = AutoStart.IsEnabled();
                ShowMenu();
            }
        };
    }

    /// <summary>
    /// 在鼠标位置弹出菜单。
    ///
    /// ★ 不能用 `_menu.Show(Cursor.Position)` —— 那样菜单不会自动
    ///   在屏幕边缘翻转，靠右下角时会弹到屏幕外。
    ///   用 NotifyIcon 自己提供的 `ShowContextMenu()` 才会正确处理边界。
    /// </summary>
    private void ShowMenu()
    {
        // WinForms 的 ContextMenuStrip 需要一个"拥有者窗口"才能正确
        // 处理焦点和边界翻转。用内部方法拿到 NotifyIcon 自己那个隐藏窗口。
        var show = typeof(NotifyIcon).GetMethod(
            "ShowContextMenu",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        if (show is not null)
        {
            show.Invoke(_icon, null);
        }
        else
        {
            // 拿不到内部方法就退回到直接弹 —— 位置可能不完美，但功能还在
            _menu.Show(Cursor.Position);
        }
    }

    /// <summary>
    /// 从嵌入资源里读图标。
    /// 读不到就退回系统默认图标 —— 没有图标也不能让程序崩。
    /// </summary>
    private static Icon LoadIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/ClipDesk.ico");
            var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
            if (stream is not null) return new Icon(stream);
        }
        catch (Exception)
        {
            // 落到下面用默认图标
        }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        // ★ Visible 必须先置 false 再 Dispose。
        //   只 Dispose 不置 false 的话，图标会残留在托盘上
        //   直到用户把鼠标划过去才消失 —— 看起来像程序没退干净。
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
