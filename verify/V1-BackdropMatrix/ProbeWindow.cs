using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using static V1BackdropMatrix.NativeMethods;

namespace V1BackdropMatrix;

/// <summary>
/// 单个被验证的窗口。所有配置来自 Probe，构造函数里一次性应用，
/// 每条 API 的返回值都记进 SetupReport —— 那是这次验证的硬证据。
/// </summary>
internal sealed class ProbeWindow : Window
{
    private readonly Probe _probe;
    private readonly StringBuilder _log = new();

    /// <summary>探针窗口边长（DIP）。矩阵排布按它算格子。</summary>
    public const double Size = 240;

    /// <summary>
    /// 在 (left, top) 处建一个探针窗口。
    /// 位置必须由调用方明确给出 —— 靠系统默认摆放的话，
    /// 窗口会叠在左侧不透明的卡片区上，材质有没有模糊根本看不出来。
    /// </summary>
    public ProbeWindow(Probe probe, double left, double top)
    {
        _probe = probe;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = false;

        // 显式定位：必须抢在 Show() 之前设好，否则会先按默认位置闪一下
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;

        // 关键：客户区背景必须留出 alpha=0，DWM 材质才有地方透出来。
        // 若这里是实色，你会看到一块不透明的板子，而不是模糊。
        Background = null;

        Width = Size;
        Height = Size;
        AllowsTransparency = probe.Layered;

        Content = BuildContent();

        SourceInitialized += OnSourceInitialized;
    }

    private UIElement BuildContent()
    {
        var grid = new Grid
        {
            // 极细的白框，用来确认窗口的真实边界（包括 region 裁完之后的边界）
            ClipToBounds = true,
        };

        if (_probe.Cover)
        {
            grid.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Fill = (Brush)new BrushConverter().ConvertFromString(_probe.Tint)!,
            });
        }

        grid.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 1,
            Fill = Brushes.Transparent,
        });

        grid.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = _probe.Code,
            Foreground = Brushes.White,
            FontSize = 26,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });

        return grid;
    }

    public string SetupReport => _log.ToString().TrimEnd();

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var src = HwndSource.FromHwnd(hwnd);

        // 无边框窗口里，这行让 WPF 的合成目标本身保持完全透明，
        // 不至于把 DWM 材质挡掉。
        if (src?.CompositionTarget is { } ct)
            ct.BackgroundColor = Colors.Transparent;

        Line($"Layered={(_probe.Layered ? "是" : "否")}  Backdrop={_probe.Backdrop}  Shape={_probe.Shape}");

        // 关键一步：把 DWM 玻璃区扩到整个客户区。
        // 不调用它，DWM 没有可插入材质的区域，无论设什么 backdrop 属性都不会有模糊。
        int hrFrame = ExtendFrameIntoClientArea(hwnd);
        Line($"扩展玻璃区(ExtendFrame=-1) → hr=0x{hrFrame:X8}");

        ApplyBackdrop(hwnd);
        ApplyBorderAndCorners(hwnd);
        ApplyShape(hwnd);
    }

    private void ApplyBackdrop(IntPtr hwnd)
    {
        switch (_probe.Backdrop)
        {
            case Backdrop.None:
                _log.AppendLine("材质：不施加（基线）");
                break;

            case Backdrop.DwmAcrylic:
            {
                int v = DWMSBT_ACRYLIC;
                int hr = DwmSetInt(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref v, sizeof(int));
                Line($"DWM 亚克力(38={DWMSBT_ACRYLIC}) → hr=0x{hr:X8} {(hr == 0 ? "成功" : "★失败")}");
                break;
            }

            case Backdrop.AccentAcrylic:
            {
                uint abgr = ToAbgr(_probe.Tint);
                ApplyAccent(hwnd, AccentState.AcrylicBlurBehind, abgr);
                Line($"旧版叠色亚克力(Accent=4) 色=0x{abgr:X8} → 已调用(无返回值)");
                break;
            }

            case Backdrop.AccentBlur:
            {
                // 纯模糊：GradientColor 传全透明，不铺任何色板。
                // 这条才是 TranslucentTB 的行为 —— 只把背后糊掉。
                ApplyAccent(hwnd, AccentState.BlurBehind, 0x00000000);
                Line("旧版纯模糊(Accent=3, 无色板) → 已调用(无返回值)");
                break;
            }
        }
    }

    private void ApplyBorderAndCorners(IntPtr hwnd)
    {
        bool dark = _probe.Dark;
        int hrDark = DwmSetBool(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        Line($"深色模式(20)={(dark ? 1 : 0)} → hr=0x{hrDark:X8}");

        if (_probe.KillBorder)
        {
            uint none = DWMWA_COLOR_NONE;
            int hr = DwmSetUInt(hwnd, DWMWA_BORDER_COLOR, ref none, sizeof(uint));
            Line($"去边框色(34=NONE) → hr=0x{hr:X8} {(hr == 0 ? "成功" : "★失败")}");
        }
        else
        {
            _log.AppendLine("边框色(34)：故意不设，保留 1px 描边作对照");
        }

        if (_probe.DwmRound)
        {
            int round = DWMWCP_ROUND;
            int hr = DwmSetInt(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            Line($"DWM 圆角(33=ROUND) → hr=0x{hr:X8} {(hr == 0 ? "成功" : "★失败")}");
        }
    }

    private void ApplyShape(IntPtr hwnd)
    {
        if (_probe.Shape is not (Shape.RegionRound or Shape.RegionCapsule))
            return;

        // SetWindowRgn 吃的是物理像素，得把 DIP 换算过去
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int w = (int)Math.Round(Width * scale);
        int h = (int)Math.Round(Height * scale);

        if (_probe.Shape == Shape.RegionCapsule)
            ApplyCapsuleRegion(hwnd, w, h);
        else
            ApplyRoundRectRegion(hwnd, w, h, 18);

        Line($"区域裁剪 {_probe.Shape} → {w}×{h}px (缩放 {scale:0.##}×)");
        _log.AppendLine("  ↑ 重点看：材质还在不在？圆角有没有锯齿？");
    }

    private void Line(string s) => _log.AppendLine(s);

    /// <summary>把 "#AARRGGBB" 转成 AccentPolicy 要的 AABBGGRR。</summary>
    private static uint ToAbgr(string hex)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex)!;
        return ((uint)c.A << 24) | ((uint)c.B << 16) | ((uint)c.G << 8) | c.R;
    }
}
