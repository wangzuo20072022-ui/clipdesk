using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using static S2Strip.NativeMethods;

namespace S2Strip;

/// <summary>
/// 九宫格 —— 方向手势菜单。
///
/// 行为规格（见 CLAUDE.md 第 11 节「C. 九宫格」）：
///   1. 按住 Alt+V → 在**鼠标当前位置**弹出（鼠标在哪就贴着哪，不跳到屏幕正中）
///   2. 鼠标指针隐藏，进入选择模式
///   3. 以**按下那一刻的鼠标位置**为原点，鼠标朝哪动，哪个方位的格子亮起
///   4. 松开 Alt → 粘贴选中格
///   5. 鼠标没动（中心死区）→ 取消
///
/// 关于"原点"的一个重要修正（来自实测反馈）：
///   最初想用"光标永久锁在中心"，但那需要 ClipCursor（限制光标活动范围），
///   对多显示器 / 高 DPI 是雷区，而且用户移动太快时光标会卡在边界上。
///   现在的做法是**软原点**：窗口出现在鼠标处，光标照常移动，
///   但方向判定始终以"按下那一刻的位置"为基准。
///   体感上等价（因为光标本来就在九宫格中心），却没有 ClipCursor 那些坑。
/// </summary>
internal sealed class GridWindow : Window
{
    private const int Cols = 3;
    private const int Rows = 3;

    /// <summary>格子边长（DIP）。窗口整体尺寸 = 3 × 这个值。</summary>
    private const double CellSize = 160;

    /// <summary>格子之间的缝（DIP）</summary>
    private const double Gap = 3;

    private static readonly Color NormalBg = Color.FromRgb(0x2E, 0x2E, 0x2E);
    private static readonly Color NormalBorder = Color.FromRgb(0x50, 0x50, 0x50);
    private static readonly Color ActiveBg = Color.FromRgb(0x1E, 0x3A, 0x52);
    private static readonly Color ActiveBorder = Color.FromRgb(0x7A, 0xC8, 0xFF);

    private readonly ClipboardHistory _history;
    private readonly Action<string> _log;

    private readonly Border[,] _grid = new Border[Rows, Cols];
    private readonly TextBlock[,] _labels = new TextBlock[Rows, Cols];
    private readonly string?[,] _content = new string?[Rows, Cols];

    public event Action<string>? CellActivated;

    private IntPtr _hwnd;
    private string _lastShown = "(还没显示过)";

    /// <summary>
    /// 圆心（物理像素）—— 按下 Alt+V 那一刻的光标位置，**全程固定不动**。
    ///
    /// ★ 第四轮的关键认识：只记一个固定的圆心，不要记"起点/锚点"那类会移动的基准。
    ///   前三轮我一直在"起点"上做文章，而正确模型里根本没有起点这回事。
    /// </summary>
    private int _originX;
    private int _originY;

    private int _activeIndex = -1;

    /// <summary>
    /// 上一次采到的格子。-1 = 还没动过（松手就是取消）。
    ///
    /// 用户要求「只要移动过了就不能选中心」，所以一旦它变成有效值，
    /// 就再也不会回到 -1 —— 除非 Esc 或重新弹出。
    /// </summary>
    private int _lastIndex = -1;

    /// <summary>格子边长（物理像素）—— 显示时按 DPI 算</summary>
    private int _cellPx;

    public GridWindow(ClipboardHistory history, Action<string> log)
    {
        _history = history;
        _log = log;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowsTransparency = false;
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14));

        Width = CellSize * Cols;
        Height = CellSize * Rows;

        Content = BuildContent();
        SourceInitialized += OnSourceInitialized;
    }

    // ── 构建 ────────────────────────────────────────────────────────

    private UIElement BuildContent()
    {
        var root = new Grid();

        for (int r = 0; r < Rows; r++)
        {
            root.RowDefinitions.Add(new RowDefinition());
            root.ColumnDefinitions.Add(new ColumnDefinition());
        }

        // 格子内容宽度 = 格子边长 - 两边缝 - 边框 - 内边距
        double textWidth = CellSize - 2 * Gap - 2 * 1 - 2 * 8;

        for (int r = 0; r < Rows; r++)
        {
            for (int c = 0; c < Cols; c++)
            {
                // ★ 定宽、定高、不换行溢出 —— 三件套缺一不可。
                //
                //   之前"第一列文字消失"的根因就在这里：
                //   TextBlock 在 Grid 单元里**拿不到宽度约束**，
                //   长文本会把所在列撑宽，整个 Grid 超出窗口宽度，
                //   左边那列就被挤到可视区外（或渲染时被裁掉）。
                //
                //   Width 定宽 + TextTrimming 保证它绝不会撑破列。
                var label = new TextBlock
                {
                    Foreground = Brushes.White,
                    FontSize = 13,
                    LineHeight = 19,
                    Width = textWidth,
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(8, 6, 8, 6),
                };

                var border = new Border
                {
                    Margin = new Thickness(Gap),
                    Background = new SolidColorBrush(NormalBg),
                    BorderBrush = new SolidColorBrush(NormalBorder),
                    BorderThickness = new Thickness(1),
                    ClipToBounds = true,          // ★ 内容绝不越界
                    Child = label,
                };

                Grid.SetRow(border, r);
                Grid.SetColumn(border, c);
                root.Children.Add(border);

                _grid[r, c] = border;
                _labels[r, c] = label;
            }
        }

        return root;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        IntPtr exStyle = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE,
            (IntPtr)(exStyle.ToInt64() | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));

        int none = unchecked((int)DWMWA_COLOR_NONE);
        DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref none, sizeof(int));

        int round = DWMWCP_ROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

        int dark = 1;
        DwmSetWindowAttribute(_hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        _log($"  ★ 九宫格扩展样式: NOACTIVATE="
             + $"{(GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_NOACTIVATE) != 0}");
    }

    // ── 预热 / 显隐 ─────────────────────────────────────────────────

    public bool IsShown => _hwnd != IntPtr.Zero && IsWindowVisibleNative(_hwnd);

    public string LastShownPosition => _lastShown;

    public int FilledCellCount { get; private set; }

    public int RenderedCellCount => Rows * Cols;

    public string ActiveCellLabel =>
        _activeIndex < 0 ? "（中心，松手取消）"
                         : GridSelection.Label(_activeIndex) + " "
                           + GridSelection.DirectionName(_activeIndex);

    /// <summary>光标有没有被藏起来 —— 外部日志用它确认</summary>
    public bool CursorHidden => CursorHider.IsHidden;

    public void Prewarm()
    {
        SetWindowPos(_hwnd, HWND_TOPMOST, -32000, -32000, 0, 0,
                     SWP_NOACTIVATE | SWP_NOSIZE | SWP_SHOWWINDOW);
        FillCells();
        Show();
        UpdateLayout();
        Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        SetWindowPos(_hwnd, HWND_TOPMOST, -32000, -32000, 0, 0,
                     SWP_NOACTIVATE | SWP_NOSIZE);
        ShowWindow(_hwnd, SW_HIDE);
    }

    /// <summary>
    /// 在**鼠标当前位置**弹出 —— 光标正好落在九宫格正中那一格。
    ///
    /// 关于位置的两次反复（别再改了，这里是结论）：
    ///   中间试过"固定在屏幕正中"，两个问题：
    ///     1. 手感 —— 九宫格要脱离光标出现，眼睛得先找到它
    ///     2. ★ **鼠标又藏不住了**：见下
    ///
    /// ★ 位置和「光标藏不藏得住」是有因果的：
    ///   SetSystemCursor 把系统箭头换成透明，但**别的程序在光标进入自己窗口时
    ///   会用自己的光标覆盖它**（记事本里是 I 形，浏览器里是手形）。
    ///   窗口贴着光标 → 光标底下永远是我们自己的九宫格 → 我们是最后说话的人。
    ///   窗口挪到屏幕正中 → 光标常常落在**别的窗口**上 → 那个程序说了算 → 指针重现。
    ///   所以"跟着鼠标走"不只是手感好，它是**光标能藏住的前提**。
    ///
    /// 原点永远是"按下那一刻的光标位置"，与窗口画在哪无关 —— 挪窗口不影响准头。
    /// </summary>
    public void ShowAtCursor()
    {
        GetCursorPos(out POINT cursor);
        IntPtr monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _);
        double scale = dpiX > 0 ? dpiX / 96.0 : 1.0;

        _cellPx = (int)Math.Round(CellSize * scale);
        int sidePx = _cellPx * Cols;

        // 窗口左上角 = 光标位置减去中心那一格的一半 → 光标正好落在中心格正中
        int xPx = cursor.X - sidePx / 2;
        int yPx = cursor.Y - sidePx / 2;

        // 贴边时往回钳一下，别让九宫格跑出屏幕
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(monitor, ref info))
        {
            xPx = Math.Max(info.rcWork.Left, Math.Min(xPx, info.rcWork.Right - sidePx));
            yPx = Math.Max(info.rcWork.Top, Math.Min(yPx, info.rcWork.Bottom - sidePx));
        }

        // 同步 DIP 尺寸给 WPF
        double sideDip = sidePx / scale;
        if (Math.Abs(Width - sideDip) > 1)
        {
            Width = sideDip;
            Height = sideDip;
        }

        FillCells();
        ClearActive();
        UpdateLayout();

        SetWindowPos(_hwnd, HWND_TOPMOST, xPx, yPx, sidePx, sidePx,
                     SWP_NOACTIVATE | SWP_SHOWWINDOW);

        // ★ 原点 = 按下那一刻的光标位置（物理像素）
        _originX = cursor.X;
        _originY = cursor.Y;

        // ★ 每次弹出都要把"这一次的选择状态"清零 ——
        //   上一次按 Alt+V 选过什么，都不能带到这一次来。
        //   忘了清的话，会出现"刚弹出就已经选中了 21"这种莫名其妙的现象。
        _lastIndex = -1;
        _originX = cursor.X;
        _originY = cursor.Y;

        HideCursor();

        _lastShown = $"物理({xPx},{yPx}) 边长{sidePx}px 格子{_cellPx}px 缩放{scale:0.##}× "
                   + $"原点({_originX},{_originY}) 光标={CursorHider.IsHidden}";
    }

    public void HideGrid()
    {
        if (_hwnd == IntPtr.Zero) return;
        ShowCursorBack();
        ShowWindow(_hwnd, SW_HIDE);
    }

    // ── 方向选择 ────────────────────────────────────────────────────

    /// <summary>
    /// 由外部 60Hz 轮询调用：读鼠标位置 → 采样方位 → **逐格路过** → 更新高亮。
    ///
    /// ★ 第四轮的核心：换格时不再"啪"地跳过去，而是把中间那些格子
    ///   依次点亮一遍。高亮会**扫过去**，这就是用户要的"轮盘感"。
    ///
    ///   用户原话：「选到最左边的时候，再去选最右边，
    ///   你必须根据鼠标的移动方向顺时针到最右边，或者逆时针到最右边。
    ///   而你做的是跳过中间直接到最右边。」
    ///
    /// 省电：每 tick 只做纯数学，只有路径非空时才碰 UI（D9 那条规矩）。
    /// </summary>
    public void PollSelection()
    {
        if (!IsShown)
        {
            // 保险：万一窗口已经不在显示状态，光标绝不能还藏着
            if (CursorHider.IsHidden) CursorHider.Restore();
            return;
        }

        GetCursorPos(out POINT p);

        int sampled = GridSelection.Sample(p.X - _originX, p.Y - _originY);

        // 死区里：
        //   还没选过（_lastIndex < 0）→ 保持 -1（松手就是取消）
        //   选过了                    → ★ 锁定，保持上一次，回不到取消
        //   —— 用户明确要求「只要移动过了就不能选中心」
        if (sampled < 0)
        {
            if (_lastIndex < 0 && _activeIndex != -1) SetActive(-1);
            return;
        }

        if (sampled == _lastIndex) return;   // 方位没变，什么都不用做

        // ★ 路过：把从上一格到这一格之间要经过的每一格，依次点亮一遍
        var path = GridSelection.PathTo(_lastIndex, sampled);

        _lastIndex = sampled;

        if (path.Count <= 1)
        {
            SetActive(sampled);
            return;
        }

        // 一次性把整条路径应用到视觉上。
        //
        // 这里是同步循环、不是逐帧动画 —— 因为轮询是 60Hz，
        // 而人手划过去本来就只需要几十毫秒，逐帧反而会因为
        // 采样太密而"每一格都停不住"，看着更糊。
        // 同步扫一遍能让每一格至少被画一次，日志里也能看到完整路径。
        foreach (int step in path)
        {
            SetActive(step);
        }
    }

    public int CommitSelection() => _activeIndex;

    /// <summary>Esc 取消 —— 强制清掉选中状态，回到"未动过"</summary>
    public void CancelSelection()
    {
        _lastIndex = -1;
        ClearActive();
    }

    public string? GetCellContent(int row, int col)
    {
        if (row < 0 || row >= Rows || col < 0 || col >= Cols) return null;
        return _content[row, col];
    }

    /// <summary>
    /// 当前八格**分别对应哪一条历史**，按填充顺序（01→02→12→…→00）排好。
    ///
    /// 这个日志是给「粘贴后顶置」那条需求用的硬证据：
    ///   粘贴前打印一次、粘贴后再打印一次，两行一对比，
    ///   就能直接看出被粘的那条是不是从中间跑到了第一行（01）。
    ///   不用去数窗口上的字，也不会看错。
    /// </summary>
    public string DescribeSlots()
    {
        var items = _history.Items;
        var sb = new System.Text.StringBuilder("格子内容：");

        for (int i = 0; i < GridSelection.SelectableCount; i++)
        {
            var (r, c) = GridSelection.ToCell(i);
            string? text = _content[r, c];

            string name = string.IsNullOrEmpty(text)
                ? "（空）"
                : text.Replace("\r", " ").Replace("\n", " ");
            if (name.Length > 10) name = name[..10] + "…";

            sb.Append($" {GridSelection.Label(i)}={name}");
            if (i < GridSelection.SelectableCount - 1) sb.Append(' ');
        }

        sb.Append($"（历史共 {items.Count} 条）");
        return sb.ToString();
    }

    private void SetActive(int index)
    {
        if (_activeIndex >= 0)
        {
            var (pr, pc) = GridSelection.ToCell(_activeIndex);
            PaintCell(pr, pc, active: false);
        }

        _activeIndex = index;

        if (index >= 0)
        {
            var (r, c) = GridSelection.ToCell(index);
            PaintCell(r, c, active: true);

            _log($"  [选择] {GridSelection.Label(index)} {GridSelection.DirectionName(index)}"
                 + $"  → 「{Preview(_content[r, c])}」");
        }
    }

    private void PaintCell(int r, int c, bool active)
    {
        _grid[r, c].Background = new SolidColorBrush(active ? ActiveBg : NormalBg);
        _grid[r, c].BorderBrush = new SolidColorBrush(active ? ActiveBorder : NormalBorder);
        _grid[r, c].BorderThickness = new Thickness(active ? 3 : 1);
    }

    private void ClearActive()
    {
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
                PaintCell(r, c, active: false);

        _activeIndex = -1;
    }

    // ── 光标 ────────────────────────────────────────────────────────

    /// <summary>
    /// 藏光标。真正的实现在 CursorHider —— 那里用 SetSystemCursor。
    /// 这里只是转发 + 记日志。
    /// </summary>
    private void HideCursor()
    {
        if (CursorHider.IsHidden) return;      // 已经是隐藏状态，别重复设
        _log($"  [光标] {CursorHider.Hide()}");
    }

    private void ShowCursorBack()
    {
        if (!CursorHider.IsHidden) return;
        CursorHider.Restore();
        _log("  [光标] 已还原");
    }

    // ── 内容 ────────────────────────────────────────────────────────

    private static string Preview(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "—";
        string one = s.Replace("\r", " ").Replace("\n", " ");
        return one.Length > 24 ? one[..24] + "…" : one;
    }

    /// <summary>
    /// 把最近 8 条按 FillOrder 填进 8 个可选格。中心格永远留空。
    /// 宽度已经在 BuildContent 里定死了，这里只负责填文字。
    /// </summary>
    public void FillCells()
    {
        var items = _history.Items;
        FilledCellCount = 0;

        for (int r = 0; r < Rows; r++)
        {
            for (int c = 0; c < Cols; c++)
            {
                _content[r, c] = null;
            }
        }

        // 中心格：纯空
        _labels[1, 1].Text = "";
        _grid[1, 1].Background = new SolidColorBrush(Color.FromRgb(0x0C, 0x0C, 0x0C));

        for (int i = 0; i < GridSelection.SelectableCount; i++)
        {
            var (r, c) = GridSelection.ToCell(i);
            string label = GridSelection.Label(i);

            if (i < items.Count)
            {
                var entry = items[i];
                _content[r, c] = entry.Text;

                // 序号单独一行，保证一定看得见 —— 就算正文是空的，也能看出格子存在
                _labels[r, c].Text = $"{label}   {entry.At:HH:mm:ss}\n{Preview(entry.Text)}";
                _labels[r, c].Foreground = Brushes.White;
                FilledCellCount++;
            }
            else
            {
                _labels[r, c].Text = label;
                _labels[r, c].Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60));
            }
        }

        _log($"  ★ 填格完成：{FilledCellCount}/8 有内容，历史共 {items.Count} 条");
    }

    private void OnCellClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: string text }) return;
        HideGrid();
        CellActivated?.Invoke(text);
    }

    // ── P/Invoke 补充 ───────────────────────────────────────────────

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private static bool IsWindowVisibleNative(IntPtr hWnd) => IsWindowVisible(hWnd);
}
