using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static ClipDesk.NativeMethods;

namespace ClipDesk;

/// <summary>
/// 玻璃的**显示层** —— 把 <see cref="GlassSurface"/> 算出来的两张小图贴到窗口上。
///
/// ══ 图层结构（顺序 = 绘制顺序，先加的在下层）════════════════════════
///
///     ┌─ ① ScrimLayer  半透明着色 —— 让文字读得清（alpha 由 ScrimOpacity 控制）
///     ├─ ② RawLayer    未处理的原始背景 —— **只在圆角外面看得见**
///     ├─ ③ GlassLayer  折射 + 模糊 + 调色，被**圆角裁剪**（透镜本体）
///     ├─ ④ GlowLayer   内发光
///     └─ ⑤ HighlightLayer 高光描边
///
/// ══ ★ 为什么 A 层是**不透明**的（五轮返工的结论）════════════════════
///
///   这一节以前写的是反的：文档里说"折射层必须带真实 alpha，否则就是
///   糊一块贴上去 = 黑底"。**这句话是错的**，它把我自己带偏了两轮。
///
///   浏览器里 `backdrop-filter` 的真实行为是**两层**：
///
///       backdrop  →  滤镜（模糊/扭曲）  →  **取代**原来的背景   ← 不透明
///       元素自己的 background-color（带 alpha）叠在上面        ← 这一层才带 alpha
///
///   `filter: blur()` 从不"把背景变透明"，它是**替换**掉那块背景。
///   所以正确做法是：
///
///     · A 层：折射后的真实桌面 —— **不透明**，但它画的就是真桌面，看着像透的
///     · B 层：玻璃自己的着色 —— 很淡的 alpha（ScrimOpacity，默认 0.06）
///
///   我之前把这两层混成一层：一边想让它透明、一边往里掺深色，
///   结果既不是真背景也不是真透明，就是用户说的那块"黑底"。
///
/// ══ 圆角是怎么画的（一个 WPF 的坑）═════════════════════════════════
///
///   Border 的 `CornerRadius` **只裁背景色**，它的 `Child` 内容照样铺满
///   整块矩形（WPF 的已知行为）—— 所以一层 Border 画不出"圆角里才有位图"。
///   半透明窗口还有个更简单的答案：**圆角外面什么都不画就行了**（就是透的）。
///   所以这里用 <see cref="System.Windows.Shapes.Path"/>：几何 = 圆角矩形，
///   Fill = 位图笔刷。位图连同圆角一起被裁掉，边缘还自带抗锯齿。
/// </summary>
internal sealed class GlassChrome
{
    private readonly GlassParams _params;
    private readonly Grid _root;

    /// <summary>
    /// 圆角覆盖值（DIP）。null = 用 <see cref="GlassParams.CornerRadius"/>。
    ///
    /// ★ 存在的理由：条子（收起态）只有 ~11 DIP 高，玻璃默认的圆角 10
    ///   会把两端削成半圆 —— 那就不是"一条线"了。条子自己传一个小值进来。
    /// </summary>
    private readonly double? _cornerRadiusOverride;
    private readonly Border _scrim;
    private readonly Border _glow;
    private readonly System.Windows.Shapes.Path _highlight1;
    private readonly System.Windows.Shapes.Path _highlight2;

    /// <summary>折射后的玻璃本体（圆角路径 + 位图笔刷）</summary>
    private readonly System.Windows.Shapes.Path _glassPath;

    /// <summary>未处理的原始背景 —— 只在"折射还没画出来"的那一帧里顶一下</summary>
    private readonly System.Windows.Shapes.Path _rawPath;

    /// <summary>
    /// ★ 透明窗口**画不出投影**，所以这里画一圈**假投影**：
    ///   一块比玻璃大一圈、被大半径模糊的深色圆角矩形。
    ///
    ///   这条对「玻璃看得见」至关重要 —— 参考项目里
    ///   `box-shadow: 0 12px 40px rgba(0,0,0,0.25)` 是玻璃最重要的可见线索：
    ///   有投影 = 有一块东西浮在桌面上。
    ///   没有它、面上又是全透明的话，玻璃就"消失"了。
    /// </summary>
    private readonly System.Windows.Shapes.Path _shadowPath;

    public GlassParams Params => _params;

    /// <summary>
    /// ★ 给测试用的：第 1 层高光的渐变笔刷。
    ///
    /// 存在的理由：`UpdateHighlight` 的效果（角度、stop 位置）
    /// **只能从 brush 上读回来**。第一版量纲写错时，
    /// 截图逐点对比全是 0、误判成"渲染没生效"，排查了一轮。
    /// 有了这个入口，测试可以直接断言角度，不用再靠截图猜。
    /// </summary>
    internal System.Windows.Media.Brush? HighlightBrush1 => _highlight1.Fill;
    internal System.Windows.Media.Brush? HighlightBrush2 => _highlight2.Fill;

    private GlassChrome(GlassParams p, Border scrim, Border glow,
                        System.Windows.Shapes.Path highlight1,
                        System.Windows.Shapes.Path highlight2,
                        System.Windows.Shapes.Path shadowPath,
                        System.Windows.Shapes.Path rawPath,
                        System.Windows.Shapes.Path glassPath, Grid root,
                        double? cornerRadiusOverride = null)
    {
        _params = p;
        _scrim = scrim;
        _glow = glow;
        _highlight1 = highlight1;
        _highlight2 = highlight2;
        _shadowPath = shadowPath;
        _rawPath = rawPath;
        _glassPath = glassPath;
        _root = root;
        // ★ 圆角覆盖 —— 条子只有 ~11 DIP 高，套用 10 会把两端削成半圆。
        //   见 StripPanelWindow.StripCoreRadius 的注释。
        _cornerRadiusOverride = cornerRadiusOverride;
    }

    /// <summary>
    /// 建一套玻璃图层。<paramref name="widthDip"/>/<paramref name="heightDip"/>
    /// 是**这块玻璃**的 DIP 尺寸（WPF 坐标单位）。
    ///
    /// ══ ★ 一个实例 = 一块玻璃，不再等于一扇窗口 ═══════════════════════
    ///
    ///   九宫格要的是「九个各自独立的液态玻璃格子」，缝里露出真实桌面。
    ///   所以这个类不能再假设"我铺满整扇窗口" —— 它只负责**一块矩形**：
    ///   九宫格建 9 个实例，每个的几何是那一格的大小。
    ///   外面（缝、四角）什么都不画 = 真透明。
    /// </summary>
    public static GlassChrome Build(GlassParams p, double widthDip, double heightDip,
                                    double? cornerRadiusOverride = null)
    {
        var root = new Grid
        {
            // ★ 必须完全透明。窗口是真透明的，
            //   Root 上任何不透明的东西都会把背后的桌面彻底挡住。
            Background = Brushes.Transparent,
            // ★ 必须是 false —— 假投影画在玻璃**外面**一圈，
            //   裁剪掉就看不见了。玻璃本体自己有圆角几何，不会溢出。
            ClipToBounds = false,
        };

        // ① 假投影（**最底下**，先画）—— 玻璃"浮起来"的唯一线索
        var shadowPath = new System.Windows.Shapes.Path
        {
            IsHitTestVisible = false,
            Stretch = Stretch.None,
        };
        root.Children.Add(shadowPath);

        // ② 着色层 —— 半透明，让文字有对比；桌面透过它可见
        var scrim = new Border
        {
            IsHitTestVisible = false,
            Background = Brushes.Transparent,
        };
        root.Children.Add(scrim);

        // ② 未处理的原始背景（只在"折射还没画出来"的那一帧里可见）
        var rawPath = new System.Windows.Shapes.Path
        {
            IsHitTestVisible = false,
            Stretch = Stretch.None,
        };
        root.Children.Add(rawPath);

        // ③ 玻璃本体（折射 + 模糊 + 调色），圆角几何自带裁剪
        var glassPath = new System.Windows.Shapes.Path
        {
            IsHitTestVisible = false,
            Stretch = Stretch.None,
        };
        root.Children.Add(glassPath);

        // ④ 内发光 —— ★ 默认**不添加**。
        //
        //   这是一层盖在玻璃**上面**的白色渐变（顶部最高 18% 白）。
        //   参考项目里没有这个东西，它就是"白雾"的一部分。
        //   InnerGlow 默认已改为 0；滑块调上去时才挂进来。
        var glow = new Border { IsHitTestVisible = false };

        // ⑤ 高光描边 —— ★ **两层**（参考项目 index.tsx:508-559 就是两层）
        //
        //   第 1 层对应参考项目的 `mixBlendMode: "screen"` + `opacity: 0.2`
        //   第 2 层对应 `mixBlendMode: "overlay"` + `opacity: 1.0`
        //
        //   ⚠️ WPF **没有 mix-blend-mode**，第 2 层只是"比第 1 层更亮的一层"，
        //      **不等于 overlay** —— 详见 ApplyStyle 里的长注释。
        //
        //   用 Path 而不是 Border：参考项目的环是 `padding:1.5px` +
        //   `mask-composite:xor` 挖出来的。WPF 里 Border 的边框在圆角处
        //   粗细会走样（外圈半径和内圈半径不一样），Path 的差集几何才对得上。
        var highlight1 = new System.Windows.Shapes.Path
        {
            IsHitTestVisible = false,
            Stretch = Stretch.None,
        };
        var highlight2 = new System.Windows.Shapes.Path
        {
            IsHitTestVisible = false,
            Stretch = Stretch.None,
        };
        root.Children.Add(highlight1);   // 先加的在下面
        root.Children.Add(highlight2);

        var chrome = new GlassChrome(p, scrim, glow, highlight1, highlight2,
                                     shadowPath, rawPath, glassPath, root,
                                     cornerRadiusOverride);
        chrome.ApplyStyle(widthDip, heightDip);
        return chrome;
    }

    // ── 样式（尺寸/参数变了就重设，不重建视觉树）────────────────────

    public void ApplyStyle(double widthDip, double heightDip)
    {
        widthDip = Math.Max(1, widthDip);
        heightDip = Math.Max(1, heightDip);

        double radius = Math.Clamp(_cornerRadiusOverride ?? _params.CornerRadius,
                                   0, Math.Min(widthDip, heightDip) / 2.0);
        double w = widthDip, h = heightDip;

        // ① 假投影：比玻璃大一圈的深色圆角矩形，被大半径模糊。
        //    ★ 这是"玻璃看得见"的关键 —— 参考项目的
        //      `box-shadow: 0 12px 40px rgba(0,0,0,0.25)` 做的是同一件事。
        //      透明窗口画不出 WPF 的 DropShadowEffect，所以自己画一块。
        double shadowStrength = Math.Clamp(_params.Shadow, 0, 1);
        if (shadowStrength <= 0.01)
        {
            _shadowPath.Data = null;
        }
        else
        {
            // ★ 尺度要克制：九宫格格子之间只有 4.5 物理像素的缝，
            //   投影太大就会把缝糊黑 —— 而用户要的是"缝里露出桌面"。
            //   所以扩得少一点、糊得轻一点，只求在缝里压出一道**暗边**，
            //   让人看出这是两块独立的玻璃。
            double spread = 4;              // 向外扩多少（DIP）
            double dropY = 3;               // 向下偏多少（光源在上方）

            // ══ ★★★ 投影不许铺到圆角之外 —— 这就是那个「暗色小直角」═══
            //
            //   用户的原话：每个格子四个角**外面**都裹着"一小截暗色的直角"。
            //   像素取证（out/shot-grid.png，'#'=很暗）：
            //
            //     cell(1,1) 左上角 y=163 起：############  ← 从窗口最外圈一路
            //     cell(0,0) 左上角 y=0   起：............  ← 到格子角点，整片暗
            //
            //   根因：投影比玻璃**大一圈**（向外 4 DIP），而窗口本身就是那个
            //   154×154 的格子大小 —— 投影超出窗口的部分被裁掉，剩下那块
            //   方形暗底在窗口边界上形成一个直角，正好压住玻璃的圆角。
            //
            //   ★ 修法：给投影加**裁剪几何**，只保留圆角矩形内部。
            //     投影的圆角半径要比玻璃的大（10 → 22），
            //     这样暗边沿着圆角走，不会在角上冒出直角。
            //     边缘那圈阴影不受影响 —— 那里投影本来就是直的。
            double shadowRadius = radius * 2 + spread;
            var shadowGeo = new RectangleGeometry(
                new Rect(-spread, -spread + dropY, w + spread * 2, h + spread * 2),
                shadowRadius, shadowRadius);
            Freeze(shadowGeo);
            _shadowPath.Data = shadowGeo;
            _shadowPath.Fill = new SolidColorBrush(
                Color.FromArgb((byte)(shadowStrength * 140), 0, 0, 0));
            _shadowPath.Effect = new System.Windows.Media.Effects.BlurEffect
            {
                Radius = 8,
                KernelType = System.Windows.Media.Effects.KernelType.Gaussian,
            };

            // ★★ 关键：把投影裁进圆角里。
            //   光靠"给投影一个大圆角"是不够的 —— 模糊会把圆弧重新抹平，
            //   角上又会渗出方形。必须显式给这块 Path 一个圆角裁剪几何，
            //   几何比玻璃**略大一点点**（+1 DIP），让圆角外面干净。
            _shadowPath.Clip = new RectangleGeometry(
                new Rect(-1, -1, w + 2, h + 2), radius + 1, radius + 1);
        }

        // ② 着色层：一块圆角色，让文字有对比
        _scrim.Background = new SolidColorBrush(WithAlpha(_params.TintColor, _params.ScrimOpacity));
        _scrim.CornerRadius = new CornerRadius(radius);

        // ③ 圆角几何 —— 两层共用。
        //    ★ 几何的坐标系既是这块玻璃自身的左上角 ——
        //      九宫格把每个实例摆到自己那一格的 (x,y)，几何不用知道外面的事。
        var geometry = new RectangleGeometry(new Rect(0, 0, w, h), radius, radius);
        Freeze(geometry);

        // ④ 未处理的原图（圆角外本来什么都不画，保留它是为了
        //    在"折射还没渲染出来"的那一帧里先看到清晰的桌面，而不是空白）
        _rawPath.Data = geometry;

        // ⑤ 玻璃本体：折射 + 模糊 + 调色
        _glassPath.Data = geometry;

        // ── 内发光（默认 0 = 不挂载）──
        double glowAlpha = Math.Clamp(_params.InnerGlow, 0, 1);
        if (glowAlpha <= 0.001)
        {
            _glow.Background = null;
            _root.Children.Remove(_glow);     // ★ 彻底不参与绘制
        }
        else
        {
            _glow.Background = new LinearGradientBrush
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
            _glow.CornerRadius = new CornerRadius(radius);

            if (!_root.Children.Contains(_glow)) _root.Children.Add(_glow);
        }

        // ══ ★★★ 高光描边：两层，和参考项目对齐（尽量对齐）══════════════
        //
        // 参考项目（liquid-glass-react index.tsx:508-559）画的是**两层**，
        // 结构完全一样，只有三处不同：
        //
        //     公共：padding: 1.5px + mask-composite: xor  → 只有 1.5px 宽的一条环
        //           渐变铺满整块（0%~100%），角度 135 + mouseOffset.x * 1.2
        //           两条 stop 的位置：33 + mouseOffset.y * 0.3 / 66 + mouseOffset.y * 0.4
        //
        //     第 1 层：mix-blend-mode screen,  opacity 0.2, stop alpha 0.12 / 0.40
        //     第 2 层：mix-blend-mode overlay, opacity 1.0, stop alpha 0.32 / 0.60
        //
        //   ══ ★ WPF 做不到什么（说清楚，不糊过去）══════════════════════
        //
        //     · `mask-composite: xor`  → **能做**：用 CombinedGeometry 做差集
        //     · `padding: 1.5px`       → **能做**：差集的内缩量就是 EdgeWidth
        //     · `screen`               → **近似**：半透明白 + Opacity。
        //                                screen 对亮底有"提亮上限"，白 alpha 是线性叠加，
        //                                亮底上会稍过亮。方向一致，可以接受。
        //     · `overlay`              → ❌ **复现不了**。
        //                                overlay 是**对比度增强**：暗底压暗、亮底提亮。
        //                                白色的半透明层**只会变亮** —— 方向就错了。
        //
        //   ★ 所以第 2 层不是"近似的 overlay"，是"比参考项目弱的一层"。
        //     它在**暗背景**上仍然接近 overlay 的表现（overlay 在暗底本来就提亮），
        //     而玻璃目前就是暗色取向（TintColor = #0A0A10）。
        //     **但在亮背景上这一层是错的** —— 这点用户已经知道并同意。
        //     取证图：out/highlight-dark.png / out/highlight-light.png
        //
        //   ══ ★ 两层的 alpha 不能照抄参考项目 ═══════════════════════════
        //
        //     参考项目第 2 层是 `overlay`，在暗底上提亮幅度比"白 alpha 叠加"小得多。
        //     照抄 0.32/0.60 会得到"两层都在猛提亮" → 比参考项目亮一大截，
        //     又变回用户已经返工两次的"罩了层白塑料"。
        //     所以第 2 层压到参考项目的 **~40%**（下面 Layer2Scale）。
        //     这个系数是第 4.4 步对着截图定的，不是拍的 —— 若还是过亮就继续降。
        const double Layer2Scale = 0.40;

        double edgeAlpha = Math.Clamp(_params.EdgeHighlight, 0, 1);
        double edgeWidth = Math.Max(0, _params.EdgeWidth);

        // 渐变 stop 位置（参考项目：随 mouseOffset.y 推；鼠标跟随先不做，取 0）
        const double StopA = 0.33;   // max(10, 33 + 0) / 100
        const double StopB = 0.66;   // min(90, 66 + 0) / 100

        if (edgeAlpha <= 0.001 || edgeWidth <= 0.01)
        {
            _highlight1.Data = null;
            _highlight2.Data = null;
        }
        else
        {
            // ── 环几何：外圆角矩形 **减去** 内圆角矩形 ──
            //
            //   内缩量 = edgeWidth（就是参考项目的 padding: 1.5px）。
            //   内层的圆角半径要按内缩量减小，否则内圈会"切进"外圈。
            var outer = new RectangleGeometry(new Rect(0, 0, w, h), radius, radius);
            double innerRadius = Math.Max(0, radius - edgeWidth);
            var inner = new RectangleGeometry(
                new Rect(edgeWidth, edgeWidth,
                         Math.Max(0, w - edgeWidth * 2),
                         Math.Max(0, h - edgeWidth * 2)),
                innerRadius, innerRadius);

            var ring = new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner);
            Freeze(ring);

            _highlight1.Data = ring;
            _highlight2.Data = ring;
            _highlight1.Opacity = 1.0;
            _highlight2.Opacity = 1.0;

            // ── 两条渐变 ──
            //
            //   ★ 关键：渐变的坐标系是**这块 Path 的包围盒**，
            //     而 Path 的包围盒 = 整块玻璃（环只是被几何挖出来的形状）。
            //     所以 0% / 33% / 66% / 100% 和参考项目的语义天然对得上 ——
            //     环只是"显示渐变边缘那一小段"的窄条。
            //
            //   角度 135°（左上→右下方向的亮带）。等价的 WPF 起止点：
            //     135° 在 CSS 里 = 从上边逆时针 135°，即指向右下
            //     StartPoint = (0.5 - cos45°*0.5, 0.5 - sin45°*0.5) = (0.146, 0.146)
            //     EndPoint   = (0.854, 0.854)
            _highlight1.Fill = MakeHighlightBrush(edgeAlpha, Layer2Scale,
                                                  StopA, StopB, 0);
            _highlight2.Fill = MakeHighlightBrush(edgeAlpha, Layer2Scale,
                                                  StopA, StopB, 1);
        }
    }

    /// <summary>
    /// ★ 只改高光渐变的**角度和停止点**，不碰视觉树、不碰位图、不重渲染。
    ///
    /// ══ 为什么必须单独开这个方法（这是跟鼠标的唯一正确接法）══════════
    ///
    /// 九宫格**只在弹出时渲染一次**（`GlassWindow.RenderGlass` 之后才显示）。
    /// 抓屏 + 真高斯模糊那一笔钱（每格 ~14ms）**每帧都付不起**。
    ///
    /// 所以跟鼠标这件事**只许改 brush 的属性**：
    ///   · `StartPoint` / `EndPoint`  → 角度
    ///   · `GradientStop.Offset`      → 亮带上下移动
    /// 都是几个 double，60Hz 跑九个格子也毫无压力。
    ///
    /// ⚠️ **绝不能**在这里调到 `ApplyStyle` —— 那个方法会重设整棵视觉树。
    ///
    /// ══ ★ 两个分量都是**百分比**，不是 [-1,1] ════════════════════════
    ///
    /// 参考项目（index.tsx:307-310）算的是：
    ///     x = (光标X − 中心X) / 元素宽 × 100      → 范围 ±50
    ///     y = (光标Y − 中心Y) / 元素高 × 100
    ///
    /// 所以这里的入参语义也是 ±50（调用方 `GridWindow.UpdateCellHighlights`
    /// 负责换算）。含义和参考项目一致（index.tsx:526-529）：
    ///   · x → 角度：`135 + x * 1.2` 度      → ±50 时能转 ±60 度
    ///   · y → 亮带位置：`33 + y * 0.3` / `66 + y * 0.4`（百分比）
    /// </summary>
    public void UpdateHighlight(double mouseOffsetX, double mouseOffsetY)
    {
        if (_highlight1.Fill is not LinearGradientBrush b1) return;
        if (_highlight2.Fill is not LinearGradientBrush b2) return;

        // 冻结的 brush 改不了 —— 那说明有别的代码冻结了它，
        // 与其抛异常不如安静跳过（宁可高光不转，也不能崩）。
        if (b1.IsFrozen || b2.IsFrozen) return;

        // ── 角度：CSS 的 `${angle}deg` → WPF 的起止点 ──
        //
        // CSS 线性渐变角度的定义：0° 指向正上方，**顺时针**增加。
        // 135° 就是"从左上射向右下"。转成 WPF 的相对起止点：
        //   方向向量 = (sin θ, −cos θ)
        //   起点 = 0.5 − 向量×0.5  ，终点 = 0.5 + 向量×0.5
        double angle = 135.0 + mouseOffsetX * 1.2;
        double rad = angle * Math.PI / 180.0;
        double vx = Math.Sin(rad), vy = -Math.Cos(rad);

        var start = new Point(0.5 - vx * 0.5, 0.5 - vy * 0.5);
        var end = new Point(0.5 + vx * 0.5, 0.5 + vy * 0.5);

        // ── 停止点：y 偏移推亮带（参考项目是百分比，这里换算成 0~1 的比例）──
        double stopA = Math.Clamp(0.33 + mouseOffsetY * 0.3 / 100.0, 0.10, 0.90);
        double stopB = Math.Clamp(0.66 + mouseOffsetY * 0.4 / 100.0, 0.10, 0.90);

        foreach (var b in new[] { b1, b2 })
        {
            b.StartPoint = start;
            b.EndPoint = end;

            // 四条 stop：0(透明) / A / B / 1(透明)
            if (b.GradientStops.Count == 4)
            {
                b.GradientStops[1].Offset = stopA;
                b.GradientStops[2].Offset = stopB;
            }
        }
    }

    /// <summary>
    /// 高光渐变。`layer` 0 = 第 1 层（对应 screen），1 = 第 2 层（尽量接近 overlay）。
    ///
    /// 两层的**形状完全一样**，差别只在 stop 的 alpha —— 和参考项目一致。
    /// </summary>
    private static LinearGradientBrush MakeHighlightBrush(double edgeAlpha, double layer2Scale,
                                                          double stopA, double stopB, int layer)
    {
        // 参考项目的 stop alpha
        //   第 1 层：0.12 / 0.40
        //   第 2 层：0.32 / 0.60  ← 但那是 overlay 的数值，我们没有 overlay，
        //                          所以要乘 layer2Scale 压下来（见上面注释）
        double a1 = layer == 0 ? 0.12 : 0.32 * layer2Scale;
        double a2 = layer == 0 ? 0.40 : 0.60 * layer2Scale;

        // opacity：第 1 层 0.2（照抄），第 2 层 1.0（照抄，幅度已经由 alpha 压过了）
        double opacity = layer == 0 ? 0.2 : 1.0;

        double s1 = edgeAlpha * opacity * a1;
        double s2 = edgeAlpha * opacity * a2;

        var b = new LinearGradientBrush
        {
            StartPoint = new Point(0.146, 0.146),
            EndPoint = new Point(0.854, 0.854),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 0),
                new GradientStop(Color.FromArgb((byte)Math.Round(Math.Clamp(s1, 0, 1) * 255), 255, 255, 255), stopA),
                new GradientStop(Color.FromArgb((byte)Math.Round(Math.Clamp(s2, 0, 1) * 255), 255, 255, 255), stopB),
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 1),
            },
        };
        return b;
    }

    /// <summary>
    /// 按**这块玻璃**的尺寸排图层：圆角路径 + 位图填充 + 抗锯齿边。
    ///
    /// ══ 为什么用 Path 而不是 Border / Image ═════════════════════════
    ///
    ///   窗口是真透明的（`AllowsTransparency=true`），所以"圆角"这件事
    ///   不需要"外面填原图"那种障眼法了 —— **外面什么都不画，就是透的**。
    ///
    ///   而 Border 的 `CornerRadius` 只是把**背景色**裁圆，
    ///   它的 `Child` 内容**照样铺满整块矩形**（WPF 的已知行为）——
    ///   所以一层 Border 画不出"圆角里才有位图"。
    ///   改成 <see cref="Path"/>：几何就是圆角矩形，填充就是位图笔刷，
    ///   位图连同圆角一起被裁掉，边缘还自带抗锯齿。
    /// </summary>
    private static void Freeze(Freezable f)
    {
        if (f.CanFreeze) f.Freeze();
    }

    /// <summary>
    /// 把 <see cref="GlassSurface"/> 算出来的两张位图挂上去。
    ///
    /// ★ 必须在窗口**显示之前**调 —— 位图就是那一刻抓的。
    /// </summary>
    public void AttachBitmaps(GlassSurface surface)
    {
        if (surface is null) return;

        _scrim.Background = new SolidColorBrush(WithAlpha(_params.TintColor, _params.ScrimOpacity));

        _rawPath.Fill = _rawBrush ??= new ImageBrush { Stretch = Stretch.Fill };
        _glassPath.Fill = _glassBrush ??= new ImageBrush { Stretch = Stretch.Fill };

        // ══ ★★★ 让 GPU 用**高质量插值**放大这张小图 ═══════════════════════
        //
        //   这层玻璃的位图是**降采样过的**（57×57），放大到 231×231 由 GPU 做。
        //   WPF 默认用 `BitmapScalingMode.Linear`（双线性），
        //   在 4 倍放大 + 低模糊的情况下，底层那 57×57 的格子结构会**透出来**，
        //   看着就是一片「透亮的马赛克」。
        //
        //   `HighQuality` 走 Fant 算法（带预滤波的高质量缩放），
        //   专门用来消这种放大块状。**代价为零** —— 本来就在 GPU 上做，
        //   只是换了个采样核。
        //
        //   ★ 这是本轮唯一"白拿"的 GPU 收益：不增加任何 CPU 计算，
        //     也不用写 shader（WPF 的 PS 2.0 限制太多，重写模糊+折射要几天）。
        RenderOptions.SetBitmapScalingMode(_rawBrush, BitmapScalingMode.HighQuality);
        RenderOptions.SetBitmapScalingMode(_glassBrush, BitmapScalingMode.HighQuality);

        _rawBrush.ImageSource = surface.RawBitmap;
        _glassBrush.ImageSource = surface.GlassBitmap;

        LastAttachedReport = surface.GlassBitmap is null
            ? "❌ 没有位图可挂（渲染失败）"
            : $"✅ 已挂上 玻璃 {surface.GlassBitmap.PixelWidth}×{surface.GlassBitmap.PixelHeight} "
              + $"/ 原图 {(surface.RawBitmap is null ? "无" : "有")} "
              + $"／ ★alpha {surface.GlassAlpha.Min}~{surface.GlassAlpha.Max}"
              + (surface.GlassAlpha.Min < 255 ? " ❌ 半透明！叠上去会发白" : " ✅ 不透明");
    }

    /// <summary>最近一次挂位图的结果（日志用）</summary>
    public string LastAttachedReport { get; private set; } = "(还没挂过)";

    private ImageBrush? _rawBrush;
    private ImageBrush? _glassBrush;

    /// <summary>"#RRGGBB" + alpha → Color</summary>
    private static Color WithAlpha(string hex, double alpha)
    {
        try
        {
            string s = hex.TrimStart('#');
            if (s.Length != 6) return Colors.Transparent;
            byte r = Convert.ToByte(s[..2], 16);
            byte g = Convert.ToByte(s.Substring(2, 2), 16);
            byte b = Convert.ToByte(s.Substring(4, 2), 16);
            byte a = (byte)Math.Clamp((int)Math.Round(Math.Clamp(alpha, 0, 1) * 255), 0, 255);
            return Color.FromArgb(a, r, g, b);
        }
        catch (Exception)
        {
            return Colors.Transparent;
        }
    }

    // ── 小图 → 大尺寸的采样表 ───────────────────────────────────────

    // ── 投影 ────────────────────────────────────────────────────────

    /// <summary>外投影需要窗口比玻璃大一圈才画得出来，而条子只有 15mm 高。</summary>
    public void ApplyShadow() => _ = _params.Shadow;

    /// <summary>给测试用：玻璃层挂了位图没。</summary>
    public bool HasBitmap => _glassBrush?.ImageSource is not null;

    /// <summary>给测试用：玻璃图层的位图。</summary>
    public BitmapSource? GlassSource => _glassBrush?.ImageSource as BitmapSource;

    /// <summary>给测试用：原始背景图层的位图。</summary>
    public BitmapSource? RawSource => _rawBrush?.ImageSource as BitmapSource;

    /// <summary>根节点，塞进窗口 Content</summary>
    public Grid Root => _root;

    /// <summary>着色的 alpha（给测试和日志）</summary>
    public double ScrimAlpha => _scrim.Background is SolidColorBrush b ? b.Color.A / 255.0 : 0;

    /// <summary>调试用：当前图层结构</summary>
    public string Describe()
        => $"玻璃层={(GlassSource is null ? "无图" : "有图")} "
         + $"原图层={(RawSource is null ? "无图" : "有图")} "
         + $"着色alpha={ScrimAlpha:0.###} "
         + $"圆角={_scrim.CornerRadius.TopLeft:0.#}";
}
