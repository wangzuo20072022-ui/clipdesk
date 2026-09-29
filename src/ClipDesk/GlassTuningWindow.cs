using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ClipDesk;

/// <summary>
/// 玻璃材质的**实时调参面板**。
///
/// ══ 为什么非做这个不可 ══════════════════════════════════════════
///
/// 九宫格手感来回折腾了四轮，根因是**我猜数字、用户看结果**。
/// 材质比手感更主观 —— 模糊多少、亮多少、描边多粗，
/// 光靠我猜必然再来四轮。
///
/// 所以把 16 个参数全部做成滑块，**用户自己拖着看**。
/// 满意后按「保存」，写进用户配置目录，重启自动恢复。
///
/// ══ 关于"改了立刻看得见" ══════════════════════════════════════
///
/// 拖动滑块 → 立刻重渲染条子 / 面板 / 九宫格（约 20ms）。
/// 所以调的时候不需要来回切窗口：
///   · 条子就在右上角，一直看得见
///   · 面板把鼠标移过去就展开
///   · 九宫格按 Alt+V
///
/// 拖动过程中会触发很多次重渲染，用一个"排队一次"的节流挡着，
/// 不会积压成卡顿。
///
/// ══ ★ 关于「截屏时看不到这个窗口」（第六轮修掉的一个真 bug）══════
///
/// 这里原来无条件调用 `ScreenCapture.HideFromCapture(hwnd)`，
/// 也就是 `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`。
///
/// 它当初是**必要**的：条子常驻显示，抓屏时会把**自己上一帧**抓进去，
/// 越叠越脏，所以要对自己的抓屏隐身。
///
/// **但那条抓屏路径已经废掉了** —— `StripPanelWindow.RenderGlass`
/// 现在是空方法（背景改由系统亚克力提供，不再自己抓屏）。
/// 保护对象没了，这行隐身却留着，于是它唯一的实效变成：
///
///   ★ 用户在调参时想截图给我看效果 —— 截图里**唯独少了调参面板**。
///     他只能说"我截图给你"，截出来却没有那块要调的东西。
///
/// 实测取证（扫描桌面 391 个窗口的 `GetWindowDisplayAffinity`）：
///
///     非 NONE 的窗口只有 1 个 → 本面板，值 17 (EXCLUDEFROMCAPTURE)
///     九宫格 / 条子 / 面板 全是 0 (NONE)，截图里都正常
///
/// 所以现在改成**默认不隐身**，面板上给一个复选框让用户按需打开。
/// </summary>
internal sealed class GlassTuningWindow : Window
{
    private readonly GlassParams _p;
    private readonly Action _onChanged;

    private readonly Dictionary<string, TextBlock> _valueLabels = new();

    /// <summary>初始化期间为 true，挡掉 Slider 的首次 ValueChanged</summary>
    private bool _loading = true;

    /// <summary>已经排了一次刷新还没执行 —— 防止拖动时积压</summary>
    private bool _refreshQueued;

    /// <summary>
    /// ★ 抓屏时是否对本面板隐身。**默认 false** —— 见类注释。
    ///
    /// 打开的场景：既要开九宫格调材质，又不想让本面板被糊进玻璃里
    /// （九宫格会真的抓屏）。这时候勾上。
    /// 关掉后记得取消勾选，否则自己截图又看不到它了。
    /// </summary>
    private bool _excludeFromCapture;

    public GlassTuningWindow(GlassParams p, Action onChanged)
    {
        _p = p;
        _onChanged = onChanged;

        Title = "ClipDesk · 玻璃材质调参";
        Width = 460;
        Height = 680;
        Topmost = true;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.Manual;

        // 靠屏幕右侧摆，别挡住右上角的条子
        var wa = SystemParameters.WorkArea;
        Left = Math.Max(0, wa.Right - Width - 24);
        Top = Math.Max(0, wa.Top + 60);

        Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1E));
        Content = BuildContent();

        SourceInitialized += (_, _) =>
        {
            // ★ 默认**不**隐身 —— 用户在调参时截图，面板必须在场。
            //   （以前这里无条件隐身，导致用户截图里唯独少了这块面板。）
            //   需要时由面板上的复选框打开。
            ApplyCaptureExclusion();
        };

        _loading = false;
    }

    /// <summary>
    /// 按 <see cref="_excludeFromCapture"/> 把本窗口的抓屏可见性设成对应的值。
    ///
    /// 关 = <c>WDA_NONE</c>（正常，截图里看得见）
    /// 开 = <c>WDA_EXCLUDEFROMCAPTURE</c>（抓屏里消失）
    /// </summary>
    private void ApplyCaptureExclusion()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        bool ok = ScreenCapture.SetCaptureVisibility(hwnd, _excludeFromCapture);
        GlassLog(ok
            ? (_excludeFromCapture
                ? "[调参] 已对本面板开启「抓屏隐身」—— ⚠️ 你现在自己截图也看不到它了"
                : "[调参] 已对本面板关闭「抓屏隐身」—— 截图里能看见它")
            : "[调参] ★ 切换抓屏隐身失败（老系统不支持 WDA_EXCLUDEFROMCAPTURE）");
    }

    // ── 构建 ────────────────────────────────────────────────────────

    private UIElement BuildContent()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 标题
        root.RowDefinitions.Add(new RowDefinition());                              // 滑块区
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 按钮

        root.Children.Add(BuildHeader());

        var stack = new StackPanel { Margin = new Thickness(12, 6, 12, 6) };
        foreach (var spec in GlassParams.Specs)
        {
            stack.Children.Add(BuildSliderRow(spec));
        }

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = stack,
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var buttons = BuildButtons();
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        return root;
    }

    private UIElement BuildHeader()
    {
        var bar = new StackPanel
        {
            Margin = new Thickness(14, 12, 14, 4),
        };

        bar.Children.Add(new TextBlock
        {
            Text = "玻璃材质 · 实时调参",
            Foreground = Brushes.White,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
        });

        bar.Children.Add(new TextBlock
        {
            Text = "拖动即生效。条子在右上角；面板把鼠标移过去；九宫格按 Alt+V。"
                 + "截图时可以正常截到本面板（除非你勾了右下角的「抓屏隐身」）。",
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA6)),
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });

        return bar;
    }

    /// <summary>
    /// 一行 = 标签 + 滑块 + 数值。
    ///
    /// 布局用 Grid 的三列而不是 DockPanel —— 列宽是固定的，
    /// 这样所有滑块左右对齐，拖起来手感一致。
    /// </summary>
    private UIElement BuildSliderRow(
        (string Label, string Prop, double Min, double Max, double Step) spec)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });

        var label = new TextBlock
        {
            Text = spec.Label,
            Foreground = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD8)),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(label, 0);
        row.Children.Add(label);

        var slider = new Slider
        {
            Minimum = spec.Min,
            Maximum = spec.Max,
            SmallChange = spec.Step,
            LargeChange = spec.Step * 5,
            Value = _p.Get(spec.Prop),
            VerticalAlignment = VerticalAlignment.Center,
            IsSnapToTickEnabled = spec.Step >= 1,
            TickFrequency = spec.Step,
        };
        Grid.SetColumn(slider, 1);
        row.Children.Add(slider);

        var value = new TextBlock
        {
            Text = Format(_p.Get(spec.Prop), spec.Step),
            Foreground = new SolidColorBrush(Color.FromRgb(0x8C, 0xD0, 0xFF)),
            FontSize = 12,
            FontFamily = new FontFamily("Consolas"),
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(8, 0, 0, 0),
        };
        Grid.SetColumn(value, 2);
        row.Children.Add(value);

        _valueLabels[spec.Prop] = value;

        string prop = spec.Prop;
        double step = spec.Step;

        slider.ValueChanged += (_, e) =>
        {
            if (_loading) return;

            _p.Set(prop, e.NewValue);
            value.Text = Format(e.NewValue, step);
            RequestRefresh();
        };

        return row;
    }

    private static string Format(double v, double step)
        => step >= 1 ? ((int)Math.Round(v)).ToString() : v.ToString("0.##");

    private UIElement BuildButtons()
    {
        // ★ WrapPanel 而不是横向 StackPanel —— 加了第 4 个「抓屏隐身」复选框
        //   之后，460px 宽放不下了。WrapPanel 会自动换到第二行。
        var bar = new WrapPanel
        {
            Margin = new Thickness(14, 8, 14, 14),
            Orientation = Orientation.Horizontal,
        };

        bar.Children.Add(MakeButton("保存", () =>
        {
            bool ok = _p.Save();
            GlassLog(ok
                ? $"[调参] 已保存到 {GlassParams.DefaultPath}"
                : "[调参] ★ 保存失败（磁盘/权限？）——参数仍在内存里有效");
        }));

        bar.Children.Add(MakeButton("打印参数", () =>
        {
            GlassLog("[调参] 当前参数：" + _p.Summary);
            GlassLog("[调参] JSON：" + System.Text.Json.JsonSerializer.Serialize(_p));
        }));

        bar.Children.Add(MakeButton("重置", () =>
        {
            var fresh = new GlassParams();
            _loading = true;
            foreach (var spec in GlassParams.Specs)
            {
                _p.Set(spec.Prop, fresh.Get(spec.Prop));
            }
            _loading = false;
            RebuildValues();
            RequestRefresh();
            GlassLog("[调参] 已重置为默认值（还没存盘，按「保存」才会写文件）");
        }));

        bar.Children.Add(MakeCaptureCheck());

        return bar;
    }

    /// <summary>
    /// 「抓屏隐身」复选框。**默认不勾** —— 勾上之后自己截图就看不到本面板。
    ///
    /// 为什么要有这个开关：九宫格是真的抓屏做玻璃的，
    /// 本面板如果悬在九宫格要抓的区域上，会被糊进玻璃里（越叠越脏）。
    /// 那种情况下勾上；平时留空，截图才不会缺东西。
    /// </summary>
    private UIElement MakeCaptureCheck()
    {
        var box = new CheckBox
        {
            Content = "抓屏隐身（勾上后截图看不到本面板）",
            IsChecked = _excludeFromCapture,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA6)),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
            Cursor = Cursors.Hand,
        };

        box.Checked += (_, _) => { _excludeFromCapture = true; ApplyCaptureExclusion(); };
        box.Unchecked += (_, _) => { _excludeFromCapture = false; ApplyCaptureExclusion(); };

        return box;
    }

    private Button MakeButton(string text, Action onClick)
    {
        var b = new Button
        {
            Content = text,
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 8, 0),
            FontSize = 12,
            Background = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x38)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>重置之后把滑块和数值文字同步回参数里的当前值</summary>
    private void RebuildValues()
    {
        // 简单可靠的做法：整棵树重建。参数只有 16 个，重建成本可忽略。
        Content = BuildContent();
    }

    // ── 刷新节流 ────────────────────────────────────────────────────

    /// <summary>
    /// 请求刷新三个窗口的玻璃。
    ///
    /// ★ 拖动滑块时这个函数每秒被调几十次，每次重渲染要 20ms。
    ///   如果来一次做一次，事件会积压成明显的卡顿。
    ///   所以用"**已经排了一次就不再排**"合并掉：
    ///   拖动过程中最多保持一次待执行的刷新，松手后必然有最后一次。
    /// </summary>
    private void RequestRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
            new Action(() =>
            {
                _refreshQueued = false;
                _onChanged();
            }));
    }

    /// <summary>调参窗口没有控制台；只对需要用户处理的失败显示提示。</summary>
    private static void GlassLog(string msg)
    {
        if (msg.Contains("失败", StringComparison.Ordinal))
        {
            MessageBox.Show(msg, "ClipDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
