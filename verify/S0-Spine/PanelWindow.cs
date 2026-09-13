using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using static S0Spine.NativeMethods;

namespace S0Spine;

/// <summary>
/// S0 的"面板"。故意做得很丑 —— 这是探针不是产品。
///
/// 唯一要紧的是三件事：
///   1. 它带着 WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW 出生
///   2. 用 SW_SHOWNOACTIVATE 显示，绝不调用 Activate()
///   3. 点一条 → 写回剪贴板 → SendInput Ctrl+V
///
/// 材质、圆角、动效一律没有。那全部属于 S1 之后的阶段。
/// 背景用实色（不是半透明），是为了把"透明度"这个变量从 S0 里彻底拿掉。
/// </summary>
internal sealed class PanelWindow : Window
{
    private readonly ClipboardHistory _history;
    private readonly Action<string> _log;
    private readonly StackPanel _list = new();

    /// <summary>点击条目后交给外面去粘贴</summary>
    public event Action<string>? ItemActivated;

    public PanelWindow(ClipboardHistory history, Action<string> log)
    {
        _history = history;
        _log = log;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;          // 不抢焦点的第一道保险
        Topmost = true;
        AllowsTransparency = false;     // 实色背景，不需要分层窗口
        Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));

        Width = 420;
        Height = 260;

        Content = BuildContent();
        SourceInitialized += OnSourceInitialized;
    }

    private UIElement BuildContent()
    {
        var root = new DockPanel { Margin = new Thickness(10) };

        var title = new TextBlock
        {
            Text = "ClipDesk S0 脊柱探针  ·  Alt+V 开关  ·  Esc 关闭  ·  点一条粘贴",
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xE6, 0xFF)),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 8),
        };
        DockPanel.SetDock(title, Dock.Top);
        root.Children.Add(title);

        root.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _list,
        });

        return root;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;

        // 第二道保险：扩展样式必须在窗口首次显示前就位
        IntPtr exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        IntPtr wanted = (IntPtr)(exStyle.ToInt64() | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, wanted);

        IntPtr after = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        bool noActivate = (after.ToInt64() & WS_EX_NOACTIVATE) != 0;
        bool toolWindow = (after.ToInt64() & WS_EX_TOOLWINDOW) != 0;

        _log($"  ★ 扩展样式已设置: NOACTIVATE={(noActivate ? "是" : "否")}  TOOLWINDOW={(toolWindow ? "是" : "否")}");

        // 无边框窗口在 WPF 里默认会画一层不透明底色，这里清掉
        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } ct)
        {
            ct.BackgroundColor = Colors.Transparent;
        }

        // 顺便把 Win11 默认那圈 1px 描边去掉 —— 不然点的时候边缘很扎眼
        int none = unchecked((int)DWMWA_COLOR_NONE);
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref none, sizeof(int));
    }

    /// <summary>
    /// 用 SW_SHOWNOACTIVATE 显示（不是 WPF 的 Show()）。
    /// 这是 Q1 的核心动作：显示窗口但绝不激活它。
    /// </summary>
    public void ShowNoActivate()
    {
        Refresh();
        MoveNearCursor();

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        ShowWindow(hwnd, SW_SHOWNOACTIVATE);
    }

    public void HidePanel()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        ShowWindow(hwnd, SW_HIDE);
    }

    public bool IsPanelVisible
    {
        get
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            return IsWindowVisibleNative(hwnd);
        }
    }

    private void MoveNearCursor()
    {
        GetCursorPos(out POINT p);

        // 贴到鼠标右下一点；越界就翻到左边/上边
        double x = p.X + 16;
        double y = p.Y + 16;
        if (x + Width > SystemParameters.VirtualScreenWidth + SystemParameters.VirtualScreenLeft)
            x = p.X - Width - 16;
        if (y + Height > SystemParameters.VirtualScreenHeight + SystemParameters.VirtualScreenTop)
            y = p.Y - Height - 16;

        Left = x;
        Top = y;
    }

    private void Refresh()
    {
        _list.Children.Clear();

        var items = _history.Items;
        if (items.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = "（还没有历史 —— 先在别的窗口复制点什么）",
                Foreground = Brushes.Gray,
                Margin = new Thickness(4),
            });
            return;
        }

        for (int i = 0; i < items.Count; i++)
        {
            var entry = items[i];
            string preview = entry.Text.Replace("\r", " ").Replace("\n", " ");
            if (preview.Length > 60) preview = preview[..60] + "…";

            var btn = new Border
            {
                Margin = new Thickness(0, 0, 0, 4),
                Padding = new Thickness(8, 6, 8, 6),
                Background = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)),
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = $"{i + 1}.  {preview}",
                    Foreground = Brushes.White,
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
                Tag = entry.Text,
            };

            btn.MouseLeftButtonDown += OnItemClicked;
            _list.Children.Add(btn);
        }
    }

    private void OnItemClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: string text }) return;

        // 先藏起来，再把焦点还给原窗口那边 —— 藏完再粘贴，视觉上更干净
        HidePanel();
        ItemActivated?.Invoke(text);
    }

    /// <summary>PanelWindow 是 NOACTIVATE 的，键盘事件收不到，Esc 交给外面轮询处理</summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private static bool IsWindowVisibleNative(IntPtr hWnd) => IsWindowVisible(hWnd);
}
