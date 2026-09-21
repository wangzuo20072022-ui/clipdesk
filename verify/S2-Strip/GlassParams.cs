using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace S2Strip;

/// <summary>
/// 玻璃材质的**全部可调参数** + 存盘。
///
/// ══ 为什么参数要单独拿出来 ══════════════════════════════════════
///
/// 九宫格手感来回折腾了四轮，根因是**我猜数字、用户看结果**。
/// 材质比手感更主观 —— 模糊多少、亮多少、描边多粗，
/// 光靠我猜必然再来四轮。
///
/// 所以这次把参数全部集中到这里，配一个**实时滑块面板**
/// （<see cref="GlassTuningWindow"/>），用户自己拖着看。
/// 满意后按「保存」，写进 out/glass.json，重启自动恢复。
///
/// 默认值 = **简化版**（折射 0 = 关闭）。
/// 想要苹果那种"边缘折射"的感觉，把 <see cref="Refraction"/> 往上推。
/// </summary>
internal sealed class GlassParams
{
    // ── 背景处理 ────────────────────────────────────────────────────

    /// <summary>模糊半径（降采样后的像素）。越大越糊。</summary>
    public int BlurRadius { get; set; } = 12;

    /// <summary>模糊跑几遍盒式。3 遍 ≈ 高斯。</summary>
    public int BlurPasses { get; set; } = 3;

    /// <summary>
    /// 降采样倍数。1 = 不降采样（最清晰最慢），4 = 1/16 运算量。
    ///
    /// 实测：抓屏本身有 ~4.3ms 的固定开销地板，降采样能把
    /// 720×720 的抓屏从 13ms 降到 5ms，所以这个值值得调。
    /// </summary>
    public int Downsample { get; set; } = 4;

    /// <summary>饱和度。苹果的玻璃会让背景更鲜艳（1.4~1.8）。</summary>
    public double Saturation { get; set; } = 1.6;

    /// <summary>亮度倍数。1.0 = 不变，>1 更亮。</summary>
    public double Brightness { get; set; } = 1.0;

    /// <summary>
    /// 背景整体不透明度（0~1）。越小玻璃越"透"，越能看见桌面。
    ///
    /// ★ 这是决定"像不像玻璃"的第一个参数。
    ///   1.0 = 完全不透明（那就不叫玻璃了）；
    ///   0.6~0.8 通常最好看 —— 既压得住背景，又明显透光。
    /// </summary>
    public double BackgroundOpacity { get; set; } = 0.90;

    /// <summary>
    /// 压暗量（0~1）。玻璃会稍微吸光，纯透亮会显得"塑料"。
    /// 0.1~0.2 会有很明显的质感提升。
    /// </summary>
    public double Dim { get; set; } = 0.03;

    // ── 折射（液态玻璃的灵魂）────────────────────────────────────────

    /// <summary>
    /// ★ 折射强度（像素）。**默认 0 = 关闭**（先做简化版）。
    ///
    /// 推到 20~60 会看到玻璃边缘把背景"放大扭曲"，那就是苹果的感觉。
    /// 太大（>100）会显得像哈哈镜。
    /// </summary>
    public double Refraction { get; set; } = 0.0;

    /// <summary>
    /// 色散强度。边缘泛出的那圈彩虹。
    /// 依赖 <see cref="Refraction"/> —— 折射是 0 时它没有任何效果。
    /// </summary>
    public double ChromaticAberration { get; set; } = 0.0;

    /// <summary>折射带占半边长度的比例。0.25 = 边缘 25% 是折射带。</summary>
    public double RefractionBand { get; set; } = 0.25;

    // ── 玻璃本体（矢量层）───────────────────────────────────────────

    /// <summary>圆角半径（DIP）。0 = 直角。</summary>
    public double CornerRadius { get; set; } = 18;

    /// <summary>
    /// 边框高光不透明度。光从上方打下来，上边缘亮、下边缘暗 ——
    /// 这是"玻璃有厚度"的关键暗示。
    /// </summary>
    public double EdgeHighlight { get; set; } = 0.55;

    /// <summary>边框宽度（DIP）</summary>
    public double EdgeWidth { get; set; } = 1.0;

    /// <summary>内发光强度（0~1）。让玻璃内部有一层柔和的光。</summary>
    public double InnerGlow { get; set; } = 0.18;

    /// <summary>玻璃底色。白色偏冷，比较像苹果。</summary>
    public string TintColor { get; set; } = "#FFFFFF";

    /// <summary>底色不透明度（叠在背景之上）。</summary>
    public double TintOpacity { get; set; } = 0.06;

    /// <summary>投影强度（0~1）。0 = 不画投影。</summary>
    public double Shadow { get; set; } = 0.35;

    // ── 存盘 ────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>存盘位置：out/glass.json</summary>
    public static string DefaultPath
    {
        get
        {
            string exeDir = AppContext.BaseDirectory;
            // bin/Debug/net8.0-windows/ → 往上找到项目目录下的 out/
            string root = exeDir;
            for (int i = 0; i < 5; i++)
            {
                string candidate = Path.Combine(root, "out");
                if (Directory.Exists(candidate)) return Path.Combine(candidate, "glass.json");
                var parent = Directory.GetParent(root);
                if (parent is null) break;
                root = parent.FullName;
            }
            return Path.Combine(exeDir, "glass.json");
        }
    }

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
        $"模糊{BlurRadius}px×{BlurPasses} 降采样{DowsampleSafe}× "
        + $"饱和{Saturation:0.##} 不透明{BackgroundOpacity:0.##} "
        + $"折射{Refraction:0.#} 色散{ChromaticAberration:0.##} "
        + $"圆角{CornerRadius:0.#} 高光{EdgeHighlight:0.##}";

    private int DowsampleSafe => Math.Max(1, Downsample);

    /// <summary>各参数的中文名 + 范围，调参面板照着这个建滑块。</summary>
    public static IReadOnlyList<(string Label, string Prop, double Min, double Max, double Step)> Specs { get; }
        = new (string, string, double, double, double)[]
        {
            ("模糊半径",    nameof(BlurRadius),           0,   40,  1),
            ("模糊遍数",    nameof(BlurPasses),           1,    5,  1),
            ("降采样",      nameof(Downsample),           1,    8,  1),
            ("饱和度",      nameof(Saturation),           0,    3,  0.05),
            ("亮度",        nameof(Brightness),           0.3,  2,  0.05),
            ("★ 背景不透明度", nameof(BackgroundOpacity), 0,    1,  0.02),
            ("压暗",        nameof(Dim),                  0,    0.6, 0.02),
            ("★ 折射强度",  nameof(Refraction),           0,  120,  1),
            ("色散",        nameof(ChromaticAberration),  0,    5,  0.1),
            ("折射带宽度",  nameof(RefractionBand),       0.05, 0.45, 0.01),
            ("圆角",        nameof(CornerRadius),         0,   80,  1),
            ("边框高光",    nameof(EdgeHighlight),        0,    1,  0.02),
            ("边框宽度",    nameof(EdgeWidth),            0,    4,  0.5),
            ("内发光",      nameof(InnerGlow),            0,    1,  0.02),
            ("底色不透明度", nameof(TintOpacity),         0,    0.4, 0.01),
            ("投影",        nameof(Shadow),               0,    1,  0.05),
        };

    /// <summary>按名字读一个数值参数（调参面板用）</summary>
    public double Get(string prop) => prop switch
    {
        nameof(BlurRadius) => BlurRadius,
        nameof(BlurPasses) => BlurPasses,
        nameof(Downsample) => Downsample,
        nameof(Saturation) => Saturation,
        nameof(Brightness) => Brightness,
        nameof(BackgroundOpacity) => BackgroundOpacity,
        nameof(Dim) => Dim,
        nameof(Refraction) => Refraction,
        nameof(ChromaticAberration) => ChromaticAberration,
        nameof(RefractionBand) => RefractionBand,
        nameof(CornerRadius) => CornerRadius,
        nameof(EdgeHighlight) => EdgeHighlight,
        nameof(EdgeWidth) => EdgeWidth,
        nameof(InnerGlow) => InnerGlow,
        nameof(TintOpacity) => TintOpacity,
        nameof(Shadow) => Shadow,
        _ => 0,
    };

    /// <summary>按名字写一个数值参数（调参面板用）</summary>
    public void Set(string prop, double value)
    {
        switch (prop)
        {
            case nameof(BlurRadius): BlurRadius = (int)Math.Round(value); break;
            case nameof(BlurPasses): BlurPasses = (int)Math.Round(value); break;
            case nameof(Downsample): Downsample = (int)Math.Round(value); break;
            case nameof(Saturation): Saturation = value; break;
            case nameof(Brightness): Brightness = value; break;
            case nameof(BackgroundOpacity): BackgroundOpacity = value; break;
            case nameof(Dim): Dim = value; break;
            case nameof(Refraction): Refraction = value; break;
            case nameof(ChromaticAberration): ChromaticAberration = value; break;
            case nameof(RefractionBand): RefractionBand = value; break;
            case nameof(CornerRadius): CornerRadius = value; break;
            case nameof(EdgeHighlight): EdgeHighlight = value; break;
            case nameof(EdgeWidth): EdgeWidth = value; break;
            case nameof(InnerGlow): InnerGlow = value; break;
            case nameof(TintOpacity): TintOpacity = value; break;
            case nameof(Shadow): Shadow = value; break;
        }
    }
}
