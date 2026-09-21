using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace S2Strip;

/// <summary>
/// 玻璃的**矢量层** —— 圆角、高光描边、内发光、投影。
///
/// ══ 图层结构（顺序就是绘制顺序，下面的先画）══════════════════════
///
///     ┌─ ① 原始背景图（未处理，铺满）
///     │     只在**圆角外面**看得见 —— 它和桌面像素一模一样，
///     │     所以看上去就是"圆角处透过去了"
///     ├─ ② 玻璃图（模糊+折射+调色），**被圆角裁剪**
///     ├─ ③ 内发光（上亮下暗的柔和渐变）
///     └─ ④ 高光描边（上边缘亮、下边缘暗 —— "有厚度"的关键暗示）
///
/// ══ 为什么这么分层（性能逼出来的）══════════════════════════════
///
/// 第一版是**在 C# 里逐像素**把圆角混合好的：算遮罩、算抗锯齿、混合两层。
/// 720×720 光这两步就 100ms。
///
/// 改成分层之后：
///   · 放大 → WPF 把 180×180 的小图拉到 720×720（GPU，免费）
///   · 圆角 → WPF 的 RectangleGeometry 裁剪（GPU，免费）
///   · 高光/内发光 → WPF 矢量画（GPU，免费）
///
/// 我们只负责把两张**小图**算出来。整条链路从 245ms 降到 20ms 上下。
/// **能在 GPU 上做的就别在 CPU 上做。**
/// </summary>
internal sealed class GlassChrome
{
    /// <summary>玻璃图那一层。重新渲染之后要更新它的 Source。</summary>
    public Image GlassLayer { get; }

    /// <summary>原始背景那一层。</summary>
    public Image RawLayer { get; }

    /// <summary>高光描边（参数变了要更新它的画刷）</summary>
    public Border HighlightLayer { get; }

    /// <summary>内发光（参数变了要更新）</summary>
    public Border GlowLayer { get; }

    /// <summary>整个玻璃层的根节点，塞进窗口的 Content 里</summary>
    public Grid Root { get; }

    private readonly GlassParams _params;

    private GlassChrome(GlassParams p, Image raw, Image glass,
                        Border glow, Border highlight, Grid root)
    {
        _params = p;
        RawLayer = raw;
        GlassLayer = glass;
        GlowLayer = glow;
        HighlightLayer = highlight;
        Root = root;
    }

    /// <summary>
    /// 建一套玻璃图层。
    ///
    /// <paramref name="widthDip"/>/<paramref name="heightDip"/> 是窗口的
    /// **DIP** 尺寸（WPF 的坐标单位）。
    /// </summary>
    public static GlassChrome Build(GlassParams p, double widthDip, double heightDip)
    {
        var root = new Grid
        {
            // 窗口是实心的，万一抓屏失败才露出这个底色。
            // 正常情况下 Image 会被 AttachBitmaps 的原始图完全覆盖，
            // 这里不能用接近黑色的底色来伪装成功，否则抓屏失败会和正常玻璃混淆。
            Background = new SolidColorBrush(Color.FromArgb(0x01, 0x10, 0x10, 0x10)),
            ClipToBounds = true,
        };

        // ── ① 原始背景 ──
        var raw = new Image
        {
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
        };
        // 小图放大要平滑，不然会看到马赛克
        RenderOptions.SetBitmapScalingMode(raw, BitmapScalingMode.HighQuality);
        root.Children.Add(raw);

        // ── ② 玻璃（带圆角裁剪）──
        var glass = new Image
        {
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
        };
        RenderOptions.SetBitmapScalingMode(glass, BitmapScalingMode.HighQuality);
        root.Children.Add(glass);

        // ── ③ 内发光 ──
        //    一层从上方白色渐变到透明的遮罩。玻璃"有体积"的感觉
        //    一大半来自这个 —— 光从上面照进来，上面亮下面暗。
        var glow = new Border { IsHitTestVisible = false };
        root.Children.Add(glow);

        // ── ④ 高光描边 ──
        var highlight = new Border { IsHitTestVisible = false };
        root.Children.Add(highlight);

        var chrome = new GlassChrome(p, raw, glass, glow, highlight, root);
        chrome.ApplyStyle(widthDip, heightDip);
        return chrome;
    }

    /// <summary>
    /// 把「圆角 / 高光 / 内发光」这些**跟尺寸和参数有关、跟像素无关**的
    /// 样式应用到图层上。参数被拖动时反复调用，不重建整棵树。
    /// </summary>
    public void ApplyStyle(double widthDip, double heightDip)
    {
        double radius = Math.Clamp(_params.CornerRadius, 0, Math.Min(widthDip, heightDip) / 2.0);

        // ── 玻璃层的圆角裁剪 ──
        //    ★ 这是"圆角"的全部实现。裁剪在 GPU 上做，所以圆角是矢量级的清晰，
        //      不会像"在低分辨率位图里烘焙圆角"那样发虚。
        GlassLayer.Clip = new RectangleGeometry(
            new Rect(0, 0, widthDip, heightDip), radius, radius);

        // ── 内发光 ──
        double glowAlpha = Math.Clamp(_params.InnerGlow, 0, 1);
        if (glowAlpha <= 0.001)
        {
            GlowLayer.Background = null;
        }
        else
        {
            var glowBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0.5, 0),
                EndPoint = new Point(0.5, 0.55),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb((byte)(glowAlpha * 255), 255, 255, 255), 0),
                    new GradientStop(Color.FromArgb((byte)(glowAlpha * 60), 255, 255, 255), 0.35),
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 1),
                },
            };
            GlowLayer.Background = glowBrush;
            GlowLayer.CornerRadius = new CornerRadius(radius);
        }

        // ── 高光描边 ──
        //    上边缘接近纯白、下边缘几乎透明 —— 这是"玻璃有厚度"的关键。
        //    四边一样亮的描边看起来像塑料边框，不是玻璃。
        double edgeAlpha = Math.Clamp(_params.EdgeHighlight, 0, 1);
        double edgeWidth = Math.Max(0, _params.EdgeWidth);

        if (edgeAlpha <= 0.001 || edgeWidth <= 0.01)
        {
            HighlightLayer.BorderThickness = new Thickness(0);
            HighlightLayer.BorderBrush = null;
        }
        else
        {
            var edgeBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0.5, 0),
                EndPoint = new Point(0.5, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb((byte)(edgeAlpha * 255), 255, 255, 255), 0),
                    new GradientStop(Color.FromArgb((byte)(edgeAlpha * 90), 255, 255, 255), 0.25),
                    new GradientStop(Color.FromArgb((byte)(edgeAlpha * 40), 255, 255, 255), 0.6),
                    new GradientStop(Color.FromArgb((byte)(edgeAlpha * 140), 255, 255, 255), 1),
                },
            };
            HighlightLayer.BorderThickness = new Thickness(edgeWidth);
            HighlightLayer.BorderBrush = edgeBrush;
            HighlightLayer.CornerRadius = new CornerRadius(Math.Max(0, radius - edgeWidth / 2));
        }
    }

    /// <summary>
    /// 把刚渲染出来的位图接到图层上。
    ///
    /// ★ 只是换 Source，不重建视觉树 —— 所以调参时反复调用也很便宜。
    /// </summary>
    public void AttachBitmaps(GlassSurface surface)
    {
        GlassLayer.Source = surface.GlassBitmap;
        RawLayer.Source = surface.RawBitmap;

        // 渲染失败（抓屏挂了）时，两张图都是 null，
        // 那就只剩描边和内发光 —— 至少窗口还在，不会变成一个黑洞。
        if (surface.GlassBitmap is null)
        {
            Root.Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14));
        }
    }

    /// <summary>给窗口加投影。留空实现，见下方注释。</summary>
    public void ApplyShadow()
    {
        // 外投影需要窗口比玻璃**大一圈**才画得出来 ——
        // 窗口边界会把阴影裁掉。而条子只有 15px 高，没有那一圈的空间。
        //
        // 所以这里暂时不实现外投影，"立体感"靠内发光 + 高光描边来撑。
        // 如果试过之后觉得缺投影，再加"窗口比玻璃大 N px"的方案。
        _ = _params.Shadow;
    }
}
