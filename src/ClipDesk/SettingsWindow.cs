using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ClipDesk;

/// <summary>
/// ClipDesk 的**设置中心**。
///
/// 目前两块：
///   1. 常规 —— 开机自动启动
///   2. 玻璃材质 —— 所有材质参数做成滑块，拖动即生效
///
/// ══ 材质为什么做成滑块 ══════════════════════════════════════════
///
/// 九宫格手感来回折腾了四轮，根因是**我猜数字、用户看结果**。
/// 材质比手感更主观 —— 模糊多少、亮多少、描边多粗，
/// 光靠我猜必然再来四轮。所以参数全部做成滑块，用户自己拖着看。
///
/// 满意后按「保存」，写进用户配置目录，重启自动恢复。
///
/// ══ 关于"改了立刻看得见" ══════════════════════════════════════
///
/// 拖动滑块 → 立刻重渲染条子 / 面板 / 九宫格（约 20ms）。
/// 所以调的时候不需要来回切窗口：
///   · 条子就在屏幕上方，一直看得见
///   · 面板把鼠标移过去就展开
///   · 九宫格按 Alt+V
///
/// 拖动过程中会触发很多次重渲染，用一个"排队一次"的节流挡着，
/// 不会积压成卡顿。
/// </summary>
internal sealed class SettingsWindow : Window
{
    private readonly GlassParams _p;
    private readonly Action _onChanged;

    private readonly Dictionary<string, TextBlock> _valueLabels = new();

    /// <summary>初始化期间为 true，挡掉 Slider 的首次 ValueChanged</summary>
    private bool _loading = true;

    /// <summary>已经排了一次刷新还没执行 —— 防止拖动时积压</summary>
    private bool _refreshQueued;

    /// <summary>开机自启的勾选框 —— 保存时用它决定写不写注册表</summary>
    private CheckBox _autoStartBox = null!;

    public SettingsWindow(GlassParams p, Action onChanged)
    {
        _p = p;
        _onChanged = onChanged;

        Title = "ClipDesk · 设置";
        Width = 460;
        Height = 720;
        Topmost = true;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.Manual;

        // 靠屏幕右侧摆，别挡住屏幕上方的条子
        var wa = SystemParameters.WorkArea;
        Left = Math.Max(0, wa.Right - Width - 24);
        Top = Math.Max(0, wa.Top + 60);

        Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1E));
        Content = BuildContent();

        _loading = false;
    }

    // ── 构建 ────────────────────────────────────────────────────────

    private UIElement BuildContent()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 标题
        root.RowDefinitions.Add(new RowDefinition());                              // 内容区
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 按钮

        root.Children.Add(BuildHeader());

        var stack = new StackPanel { Margin = new Thickness(12, 6, 12, 6) };
        stack.Children.Add(BuildGeneralSection());
        stack.Children.Add(BuildMaterialSection());

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
            Text = "ClipDesk 设置",
            Foreground = Brushes.White,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
        });

        bar.Children.Add(new TextBlock
        {
            Text = "材质拖动即生效。条子在屏幕上方；面板把鼠标移过去；九宫格按 Alt+V。",
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA6)),
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });

        return bar;
    }

    /// <summary>「常规」区：开机自启。</summary>
    private UIElement BuildGeneralSection()
    {
        var panel = new StackPanel { Margin = new Thickness(2, 6, 2, 10) };

        panel.Children.Add(new TextBlock
        {
            Text = "常规",
            Foreground = new SolidColorBrush(Color.FromRgb(0x8C, 0xD0, 0xFF)),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
        });

        _autoStartBox = new CheckBox
        {
            Content = "开机时自动启动 ClipDesk",
            IsChecked = AutoStart.IsEnabled(),
            Foreground = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD8)),
            FontSize = 12,
            Cursor = Cursors.Hand,
        };
        panel.Children.Add(_autoStartBox);

        return panel;
    }

    /// <summary>「玻璃材质」区：所有滑块。</summary>
    private UIElement BuildMaterialSection()
    {
        var panel = new StackPanel { Margin = new Thickness(2, 0, 2, 0) };

        panel.Children.Add(new TextBlock
        {
            Text = "玻璃材质",
            Foreground = new SolidColorBrush(Color.FromRgb(0x8C, 0xD0, 0xFF)),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
        });

        foreach (var spec in GlassParams.Specs)
        {
            panel.Children.Add(BuildSliderRow(spec));
        }

        return panel;
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
        var bar = new WrapPanel
        {
            Margin = new Thickness(14, 8, 14, 14),
            Orientation = Orientation.Horizontal,
        };

        bar.Children.Add(MakeButton("保存", () =>
        {
            bool autoOk = AutoStart.Set(_autoStartBox.IsChecked == true);
            bool glassOk = _p.Save();

            if (!autoOk || !glassOk)
            {
                MessageBox.Show("保存失败（磁盘或权限问题），部分设置可能没有生效。",
                                "ClipDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MessageBox.Show("设置已保存。", "ClipDesk",
                            MessageBoxButton.OK, MessageBoxImage.Information);
        }));

        bar.Children.Add(MakeButton("重置材质", () =>
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
        }));

        return bar;
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
        // 简单可靠的做法：整棵树重建。参数只有十几个，重建成本可忽略。
        Content = BuildContent();
    }

    // ── 刷新节流 ────────────────────────────────────────────────────

    /// <summary>
    /// 请求刷新三个界面的玻璃。
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
}
