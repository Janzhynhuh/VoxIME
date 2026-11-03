using VoxIME.Core.Appearance;

namespace VoxIME.Core.Configuration;

/// <summary>
/// 设置中心等「低频窗口」的自定义背景（PCL2 风格）：主题色 / 纯色 / 渐变 / 本地图片。
/// 只作用于设置、历史、关于、欢迎这类窗口；Live2D 桌宠与 HUD 保持透明/轻量，不使用该背景。
/// </summary>
public sealed class BackgroundConfig
{
    /// <summary>主题色（默认，跟随当前主题背景）。</summary>
    public const string ModeTheme = "theme";

    /// <summary>纯色。</summary>
    public const string ModeSolid = "solid";

    /// <summary>渐变。</summary>
    public const string ModeGradient = "gradient";

    /// <summary>本地图片。</summary>
    public const string ModeImage = "image";

    /// <summary>背景模式：theme / solid / gradient / image。</summary>
    public string Mode { get; set; } = ModeTheme;

    /// <summary>
    /// 图片路径。选择图片时会复制到 <c>%APPDATA%\VoxIME\backgrounds</c>，
    /// 避免源文件移动/删除后背景失效；空表示未选择。
    /// </summary>
    public string? ImagePath { get; set; }

    /// <summary>图片透明度 0.2–1.0。</summary>
    public double ImageOpacity { get; set; } = 0.92;

    /// <summary>预模糊半径 0–60（像素，按约 980 宽渲染后缩放）。</summary>
    public double BlurRadius { get; set; } = 22;

    /// <summary>暗化遮罩强度 0–0.95，用于保证文字对比度。</summary>
    public double Dim { get; set; } = 0.42;

    /// <summary>填充方式：cover（等比铺满）/ contain（完整显示）/ fill（拉伸铺满）。</summary>
    public string Fit { get; set; } = "cover";

    /// <summary>纯色模式颜色（#RRGGBB）；空=跟随主题卡片色。</summary>
    public string? SolidColor { get; set; }

    /// <summary>渐变起始色（#RRGGBB）；空=跟随主题强调色。</summary>
    public string? GradientStart { get; set; }

    /// <summary>渐变结束色（#RRGGBB）；空=跟随主题背景色。</summary>
    public string? GradientEnd { get; set; }

    /// <summary>
    /// 玻璃强度：使用自定义背景时卡片/输入框的半透明度（0.35–1.0，1=完全不透明）。
    /// 主题色模式下忽略（保持现有不透明卡片）。
    /// </summary>
    public double SurfaceOpacity { get; set; } = 0.82;

    /// <summary>是否为自定义背景（非主题色模式）。</summary>
    public bool IsCustom => !string.Equals(Mode, ModeTheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>规范化模式/填充方式/路径/颜色与数值范围（幂等）。</summary>
    public void Normalize()
    {
        Mode = NormalizeMode(Mode);
        Fit = NormalizeFit(Fit);
        ImagePath = string.IsNullOrWhiteSpace(ImagePath) ? null : ImagePath.Trim();
        ImageOpacity = Math.Clamp(ImageOpacity, 0.2, 1.0);
        BlurRadius = Math.Clamp(BlurRadius, 0, 60);
        Dim = Math.Clamp(Dim, 0, 0.95);
        SurfaceOpacity = Math.Clamp(SurfaceOpacity, 0.35, 1.0);
        SolidColor = NormalizeColor(SolidColor);
        GradientStart = NormalizeColor(GradientStart);
        GradientEnd = NormalizeColor(GradientEnd);
    }

    /// <summary>规范化背景模式；非法值回退主题色。</summary>
    public static string NormalizeMode(string? value)
    {
        var text = value?.Trim();
        if (string.Equals(text, ModeSolid, StringComparison.OrdinalIgnoreCase))
        {
            return ModeSolid;
        }

        if (string.Equals(text, ModeGradient, StringComparison.OrdinalIgnoreCase))
        {
            return ModeGradient;
        }

        if (string.Equals(text, ModeImage, StringComparison.OrdinalIgnoreCase))
        {
            return ModeImage;
        }

        return ModeTheme;
    }

    /// <summary>规范化填充方式；非法值回退 cover。</summary>
    public static string NormalizeFit(string? value)
    {
        var text = value?.Trim();
        if (string.Equals(text, "contain", StringComparison.OrdinalIgnoreCase))
        {
            return "contain";
        }

        if (string.Equals(text, "fill", StringComparison.OrdinalIgnoreCase))
        {
            return "fill";
        }

        return "cover";
    }

    /// <summary>把颜色规范化为 <c>#RRGGBB</c>/<c>#AARRGGBB</c>；非法/空返回 null（表示跟随主题）。</summary>
    public static string? NormalizeColor(string? value)
    {
        if (!ThemeColorMath.TryParse(value, out var color))
        {
            return null;
        }

        return ThemeColorMath.ToHex(color);
    }
}
