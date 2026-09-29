using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipDesk;

/// <summary>
/// 可调玻璃材质参数及其用户目录持久化。
/// </summary>
internal sealed class GlassParams
{
    // ── 背景处理 ────────────────────────────────────────────────────

    /// <summary>
    /// 模糊半径（**高斯的标准差 σ，全分辨率物理像素**）。**默认 28**。
    ///
    /// ══ ★★ 单位从"盒式半径"改成了"高斯 σ" ═════════════════════════
    ///
    /// 参考项目 [index.tsx:192] 用的是：
    ///     `backdropFilter: blur(${(overLight ? 12 : 4) + blurAmount * 32}px)`
    /// CSS 的 `blur(Npx)` 里 **N 就是高斯标准差 σ**（规范如此定义），
    /// 而且是**一遍、全分辨率**，**没有"遍数"这个参数**。
    ///
    /// 所以这个值现在**直接是 σ**，滑块上的数字和参考项目的
    /// `blurAmount * 32 + 4` 可以对得上，标定不再是拍脑袋。
    ///
    /// ══ ★ 为什么从 8 改回 28 —— 标定基准错了 ═══════════════════════
    ///
    ///   我一直在拿"我们的 8px vs 参考项目的 6px"这种**绝对像素**比较，
    ///   但那是**两个尺寸完全不同的元素**：
    ///
    ///     参考项目 demo 卡片：约 69 CSS px 高，模糊 20px → 占高度 **0.29**
    ///     我们的九宫格格子：231 物理 px，   模糊  8px → 占高度 **0.035**
    ///
    ///   **弱了 8 倍。** 用户的原话是「一点模糊都没有」——
    ///   在 231px 的格子上，8px 的模糊确实几乎看不出来。
    ///
    ///   按 0.29 等比算是 67px，那太狠（格子会糊成一团、文字也废）。
    ///   取 28 是"明显看得出糊了、但不失去轮廓"的量。
    ///
    ///   ⚠️ 单位是**全分辨率像素**，内部会按工作分辨率换算 ——
    ///      <see cref="GlassSurface"/> 会先算降采样因子，再把 σ 除以它。
    /// </summary>
    public int BlurRadius { get; set; } = 28;

    /// <summary>兼容旧配置读取的字段；生产渲染不使用。</summary>
    [JsonIgnore]
    public int BlurPasses { get; set; } = 3;

    /// <summary>兼容旧配置读取的字段；生产渲染不使用。</summary>
    [JsonIgnore]
    public int Downsample { get; set; } = 1;

    /// <summary>饱和度。苹果的玻璃会让背景更鲜艳（1.4~1.8）。</summary>
    public double Saturation { get; set; } = 1.6;

    /// <summary>亮度倍数。1.0 = 不变，>1 更亮。</summary>
    public double Brightness { get; set; } = 1.0;

    /// <summary>
    /// 玻璃自身的**着色不透明度**（0~1）。**默认 0 = 完全不画底色**。
    ///
    /// ══ ★ 默认 0 是这一轮"去白雾"的核心修正 ══════════════════════════
    ///
    ///   以前默认 0.06，配合 `TintColor = #FFFFFF` —— 就是往整块玻璃上
    ///   糊了 6% 的**白色**。参考项目（liquid-glass-react）里
    ///   元素 background 是 **transparent**，一个白色图层都没有。
    ///   这一层白就是用户说的「像亚克力、有一层白雾」的一部分。
    ///
    ///   液态玻璃的"像玻璃"来自**折射 + 极细的边缘高光**，不来自底色。
    ///   所以默认 0（背景原样透出来）；要压暗文字底才往上加，
    ///   而且 `TintColor` 已经改成近黑，加的是**暗**而不是**白**。
    /// </summary>
    public double ScrimOpacity { get; set; } = 0.0;

    /// <summary>
    /// 压暗量（0~1）。0 = 完全不压暗。**默认就是 0**。
    ///
    /// ★ 这里曾经是「白雾」的最大来源：`Dim = 0` 时不但不压暗，
    ///   反而给每个像素**加** +10 蓝 +8 绿 +6 红（一层偏青的雾）。
    ///   现在 `Dim = 0` 是**逐像素恒等变换**，有单测钉着。
    /// </summary>
    public double Dim { get; set; } = 0.0;

    // ── 折射（液态玻璃的灵魂）────────────────────────────────────────

    /// <summary>生产路径已停用折射；保留为旧配置兼容字段。</summary>
    public double Refraction { get; set; } = 0.0;

    /// <summary>生产路径已停用折射；保留为旧配置兼容字段。</summary>
    public double ChromaticAberration { get; set; } = 0.0;

    /// <summary>生产路径已停用折射；保留为旧配置兼容字段。</summary>
    public double RefractionBand { get; set; } = 0.12;

    // ── 玻璃本体（矢量层）───────────────────────────────────────────

    /// <summary>
    /// 圆角半径（DIP）。0 = 直角。
    ///
    /// ★ 九宫格的每一格是 154 DIP 见方，格子本身的圆角是 10
    ///   （见 `GridWindow` 的 `CornerRadius = new CornerRadius(10)`）。
    ///   玻璃层跟它对齐才不会露出一圈错位的角，所以默认取 10。
    /// </summary>
    public double CornerRadius { get; set; } = 10;

    /// <summary>
    /// 边框高光不透明度。**默认 0.22（极细的一条亮线）**。
    ///
    /// ★ 以前是 0.55 —— 一圈很粗的白边，整块玻璃看着像罩了层白塑料。
    ///   参考项目对应的东西是内阴影 `0 0 0 0.5px rgba(255,255,255,0.5)`
    ///   + `0 1px 3px rgba(255,255,255,0.25)`，非常细。
    /// </summary>
    public double EdgeHighlight { get; set; } = 0.22;

    /// <summary>边框宽度（DIP）。默认 0.6 —— 细到接近 1 物理像素。</summary>
    public double EdgeWidth { get; set; } = 0.6;

    /// <summary>
    /// 内发光强度（0~1）。**默认 0 = 完全不画**。
    ///
    /// ★ 它是盖在玻璃**上面**的白色渐变（最高 18% 白），
    ///   参考项目里没有这个东西。默认关掉；想要"玻璃有体积感"再往上加。
    /// </summary>
    public double InnerGlow { get; set; } = 0.0;

    /// <summary>
    /// 玻璃着色层的颜色。**默认近黑**。
    ///
    /// ★ 以前是 `#FFFFFF`（纯白）—— 配合 `ScrimOpacity = 0.06`，
    ///   就是往玻璃上糊 6% 的白，白雾的来源之一。
    ///   现在默认不画（ScrimOpacity = 0），真要压暗文字底时用的是**黑**。
    /// </summary>
    public string TintColor { get; set; } = "#0A0A10";

    /// <summary>旧版本配置兼容字段；生产路径只使用 ScrimOpacity。</summary>
    [JsonIgnore]
    public double TintOpacity { get; set; } = 0.06;

    /// <summary>投影强度（0~1）。0 = 不画投影。</summary>
    public double Shadow { get; set; } = 0.35;

    /// <summary>旧版本配置兼容字段；启动时不再执行截图自检。</summary>
    [JsonIgnore]
    public bool RunSelfCheckOnStartup { get; set; } = false;

    // ── 存盘 ────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>存盘位置：%LocalAppData%\\ClipDesk\\glass.json</summary>
    public static string DefaultPath
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "ClipDesk", "glass.json");

    public GlassParams Clone() => (GlassParams)MemberwiseClone();

    /// <summary>读盘。文件不存在或坏了都返回默认值 —— 绝不能因此崩。</summary>
    public static GlassParams Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return new GlassParams();
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<GlassParams>(json, JsonOpts) ?? new GlassParams();
        }
        catch (Exception)
        {
            return new GlassParams();
        }
    }

    /// <summary>存盘。失败不抛 —— 调参面板不该因为磁盘问题崩掉。</summary>
    public bool Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>一行摘要，给日志用</summary>
    public string Summary =>
        $"高斯σ{BlurRadius}px "
        + $"饱和{Saturation:0.##} 着色{ScrimOpacity:0.##} "
        + $"圆角{CornerRadius:0.#} 高光{EdgeHighlight:0.##}";

    /// <summary>
    /// 各参数的中文名 + 范围，调参面板照着这个建滑块。
    ///
    /// ★ 「模糊遍数」和「降采样」两个滑块**已删除**：
    ///   前者参考项目根本没有这个概念，后者是用户报的"马赛克"的来源之一。
    ///   两者现在都由程序自动决定（真高斯一遍 + 按 σ 算工作分辨率）。
    ///   留在面板上只会让人拖了没反应 —— 那比没有更糟。
    /// </summary>
    public static IReadOnlyList<(string Label, string Prop, double Min, double Max, double Step)> Specs { get; }
        = new (string, string, double, double, double)[]
        {
            ("模糊半径 σ",  nameof(BlurRadius),           0,   60,  1),
            ("饱和度",      nameof(Saturation),           0,    3,  0.05),
            ("亮度",        nameof(Brightness),           0.3,  2,  0.05),
            ("★ 玻璃着色",  nameof(ScrimOpacity),         0,    1,  0.02),
            ("压暗",        nameof(Dim),                  0,    0.6, 0.02),
            ("圆角",        nameof(CornerRadius),         0,   80,  1),
            ("边框高光",    nameof(EdgeHighlight),        0,    1,  0.02),
            ("边框宽度",    nameof(EdgeWidth),            0,    4,  0.5),
            ("内发光",      nameof(InnerGlow),            0,    1,  0.02),
            ("投影",        nameof(Shadow),               0,    1,  0.05),
        };

    /// <summary>按名字读一个数值参数（调参面板用）</summary>
    public double Get(string prop) => prop switch
    {
        nameof(BlurRadius) => BlurRadius,
        nameof(Saturation) => Saturation,
        nameof(Brightness) => Brightness,
        nameof(ScrimOpacity) => ScrimOpacity,
        nameof(Dim) => Dim,
        nameof(Refraction) => Refraction,
        nameof(ChromaticAberration) => ChromaticAberration,
        nameof(RefractionBand) => RefractionBand,
        nameof(CornerRadius) => CornerRadius,
        nameof(EdgeHighlight) => EdgeHighlight,
        nameof(EdgeWidth) => EdgeWidth,
        nameof(InnerGlow) => InnerGlow,
        nameof(Shadow) => Shadow,
        _ => 0,
    };

    /// <summary>按名字写一个数值参数（调参面板用）</summary>
    public void Set(string prop, double value)
    {
        switch (prop)
        {
            case nameof(BlurRadius): BlurRadius = (int)Math.Round(value); break;
            case nameof(Saturation): Saturation = value; break;
            case nameof(Brightness): Brightness = value; break;
            case nameof(ScrimOpacity): ScrimOpacity = value; break;
            case nameof(Dim): Dim = value; break;
            case nameof(Refraction): Refraction = value; break;
            case nameof(ChromaticAberration): ChromaticAberration = value; break;
            case nameof(RefractionBand): RefractionBand = value; break;
            case nameof(CornerRadius): CornerRadius = value; break;
            case nameof(EdgeHighlight): EdgeHighlight = value; break;
            case nameof(EdgeWidth): EdgeWidth = value; break;
            case nameof(InnerGlow): InnerGlow = value; break;
            case nameof(Shadow): Shadow = value; break;
        }
    }
}
