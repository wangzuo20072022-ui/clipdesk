using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using static V1BackdropMatrix.NativeMethods;

namespace V1BackdropMatrix;

/// <summary>
/// 一块待测的窗口配置。
/// CLAUDE.md 里写了一条铁律：「窗口的样式组合是数据不是代码」—— 这张表就是那个数据。
/// 正式项目里它会留在 Platform\ 层，三个窗口各自从表里取一行。
/// </summary>
internal sealed record Probe
{
    public required string Code { get; init; }      // 如 "A"
    public required string Title { get; init; }     // 卡片上显示的名字

    /// <summary>叠色，形如 "#99202020"。只有对照组用得上，所以给了默认值。</summary>
    public string Tint { get; init; } = "#3C101418";

    /// <summary>true = AllowsTransparency，窗口会成为 WS_EX_LAYERED，DWM 材质预期失效</summary>
    public bool Layered { get; init; }

    /// <summary>亚克力路径</summary>
    public Backdrop Backdrop { get; init; } = Backdrop.None;

    /// <summary>裁剪形状。注意与 DWM 圆角二选一</summary>
    public Shape Shape { get; init; } = Shape.Square;

    /// <summary>是否关闭 Win11 默认的 1px 描边</summary>
    public bool KillBorder { get; init; } = true;

    /// <summary>是否应用 Win11 抗锯齿圆角（与 Shape.Region* 互斥）</summary>
    public bool DwmRound { get; init; }

    /// <summary>是否叠色矩形盖住整个客户区</summary>
    public bool Cover { get; init; }

    /// <summary>
    /// 深色 / 浅色分支。DWM 亚克力在深色分支下本身就是一块偏黑的玻璃 ——
    /// 这不是我们叠上去的，是材质自带的样子。想看清楚「到底糊没糊」，得两边都看。
    /// </summary>
    public bool Dark { get; init; } = true;
}

internal enum Backdrop
{
    /// <summary>什么都不做。基线，用来确认「其余代码没意外引入背景」</summary>
    None,
    /// <summary>Win11 官方：DWMWA_SYSTEMBACKDROP_TYPE = DWMSBT_TRANSIENTWINDOW</summary>
    DwmAcrylic,
    /// <summary>旧版：SetWindowCompositionAttribute + ACCENT_ENABLE_ACRYLICBLURBEHIND（带叠色）</summary>
    AccentAcrylic,
    /// <summary>
    /// 旧版纯模糊：ACCENT_ENABLE_BLURBEHIND，不带任何叠色。
    /// TranslucentTB 用的就是这条 —— 只是把背后的东西糊掉，不铺色板。
    /// </summary>
    AccentBlur,
}

internal enum Shape
{
    Square,
    /// <summary>DWM 抗锯齿圆角（DWMWA_WINDOW_CORNER_PREFERENCE）</summary>
    DwmRound,
    /// <summary>圆角矩形区域裁剪（SetWindowRgn），与 DWM 圆角互斥</summary>
    RegionRound,
    /// <summary>胶囊形区域裁剪</summary>
    RegionCapsule,
}

public partial class MatrixWindow : Window
{
    private const double CardW = 290;
    private const double CardH = 200;

    /// <summary>
    /// 卡片区宽度（DIP）= 两列卡片 + 边距。只占屏幕左侧这一条，
    /// 右边整片留给底层条纹 —— 探针窗口叠在条纹上，模糊与否一眼可辨。
    /// </summary>
    private const double PanelWidth = 2 * (CardW + 12) + 10;

    /// <summary>
    /// 叠色。「叠色矩形」会把亚克力整个压暗成一块黑板 —— 那不是材质本身的样子。
    /// 所以这一版默认全都不叠色：探针上看到的就是系统给的模糊，跟 TranslucentTB 一样。
    /// 只有两个对照组（分层窗口）保留一点点叠色，用来证明「那边确实没有材质」。
    /// </summary>
    private const string DefaultTint = "#3C101418";

    private readonly List<(Probe Probe, TextBlock Log)> _entries = new();

    public MatrixWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 窗口贴左上角，方便统一截图
        Left = 0;
        Top = 0;

        var probes = new List<Probe>
        {
            // ── 第一排：验证「分层窗口上材质是否真的失效」这一核心判断 ──
            //   A/B 是唯一的对照组：故意叠色，好看出「没有材质」长什么样。
            new() { Code = "A", Title = "分层窗口 + DWM亚克力（预期：材质不生效）",
                    Layered = true,  Backdrop = Backdrop.DwmAcrylic, Tint = DefaultTint, Cover = true },
            new() { Code = "B", Title = "分层窗口 + 无材质（纯色对照）",
                    Layered = true,  Backdrop = Backdrop.None,       Tint = DefaultTint, Cover = true },

            //   C 开始全是纯材质，不叠色 —— 看到的就是系统糊出来的样子。
            new() { Code = "C", Title = "DWM新版亚克力（官方路径，预期较柔和）",
                    Backdrop = Backdrop.DwmAcrylic,    Cover = false, DwmRound = true },
            new() { Code = "D", Title = "旧版纯模糊 BlurBehind（TranslucentTB 同款）",
                    Backdrop = Backdrop.AccentBlur,    Cover = false, DwmRound = true },

            // ── 第二排：深浅分支 + 形状 ──
            new() { Code = "E", Title = "DWM亚克力 · 浅色分支（亚克力是不是没那么黑？）",
                    Backdrop = Backdrop.DwmAcrylic, Cover = false, DwmRound = true, Dark = false },
            new() { Code = "F", Title = "旧版叠色亚克力（对照组：会压暗成黑板）",
                    Backdrop = Backdrop.AccentAcrylic, Cover = false, DwmRound = true },
            new() { Code = "G", Title = "区域圆角裁剪（材质还在吗？有锯齿吗？）",
                    Backdrop = Backdrop.DwmAcrylic, Cover = false, Shape = Shape.RegionRound },
            new() { Code = "H", Title = "胶囊区域裁剪（收缩条的候选形状）",
                    Backdrop = Backdrop.DwmAcrylic, Cover = false, Shape = Shape.RegionCapsule },
        };

        var grid = new UniformGrid { Columns = 2, Margin = new Thickness(10) };
        foreach (var p in probes)
        {
            var (card, log) = BuildCard(p);
            grid.Children.Add(card);
            _entries.Add((p, log));
        }

        // 卡片区靠左占一条，右边留空露出底层条纹
        var host = new Grid
        {
            Width = PanelWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        host.Children.Add(grid);
        Root.Children.Add(host);

        // 窗口尺寸按屏幕自动算，避免越界（右侧留给条纹，宽度比卡片区宽）
        int rows = (probes.Count + 1) / 2;
        Width = Math.Min(SystemParameters.PrimaryScreenWidth, 1720);
        Height = Math.Min(SystemParameters.PrimaryScreenHeight, rows * (CardH + 12) + 20);

        // 卡片建完再弹窗口，保证截图能一次拍到全部
        Dispatcher.InvokeAsync(OpenAllProbes, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OpenAllProbes()
    {
        // 探针一律排到 PanelWidth 右边，直接压在彩色条纹上。
        // 排在左边等于叠在不透明卡片上，条纹被挡住，模糊与否无从判断 —— 那是上一轮的坑。
        const double Gap = 16;
        var work = SystemParameters.WorkArea;

        int i = 0;
        foreach (var (probe, log) in _entries)
        {
            try
            {
                int col = i % 2;
                int row = i / 2;
                double left = work.Left + PanelWidth + Gap + col * (ProbeWindow.Size + Gap);
                double top = work.Top + Gap + row * (ProbeWindow.Size + Gap);

                var w = new ProbeWindow(probe, left, top);
                w.Show();
                log.Text = w.SetupReport;
            }
            catch (Exception ex)
            {
                log.Text = "★ 窗口创建失败：" + ex.Message;
            }
            i++;
        }
    }

    private (FrameworkElement Card, TextBlock Log) BuildCard(Probe probe)
    {
        var log = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xE6, 0xFF)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8, 4, 8, 6),
            Text = "…",
        };

        var title = new TextBlock
        {
            Text = $"{probe.Code}  {probe.Title}",
            Foreground = Brushes.White,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8, 8, 8, 0),
        };

        var stack = new StackPanel();
        stack.Children.Add(title);
        stack.Children.Add(log);

        return (new Border { Width = CardW, Height = CardH, Margin = new Thickness(6), Child = stack }, log);
    }
}
