namespace VoxIME.Core.Appearance;

/// <summary>
/// 把一套语义色展开成 WPF 资源字典使用的完整颜色键值表。
/// 键名约定：普通颜色以 <c>Color</c> 结尾（<c>BgColor</c>、<c>AccentColor</c>…），
/// UI 层会为每个颜色键自动生成同名画刷（<c>BgBrush</c>、<c>AccentBrush</c>…）；
/// 渐变端点单独用 <c>AccentGradient*</c> / <c>HeroGradient*</c> 键。
/// </summary>
public static class ThemeTokens
{
    /// <summary>生成全部颜色键。新增派生色时只改这里，所有配色自动生效。</summary>
    public static IReadOnlyDictionary<string, string> Build(ThemeColorSet colors, bool isLight)
    {
        ArgumentNullException.ThrowIfNull(colors);

        var accent = colors.Accent;
        var background = colors.Background;

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // ---- 基础面 ----
            ["BgColor"] = background,
            ["CardColor"] = colors.Surface,
            ["CardHoverColor"] = colors.SurfaceHover,
            ["InputColor"] = colors.Input,
            ["PopupColor"] = colors.Popup,

            // ---- 强调色 ----
            ["AccentColor"] = accent,
            ["AccentHoverColor"] = isLight ? ThemeColorMath.Darken(accent, 0.16) : ThemeColorMath.Lighten(accent, 0.10),
            ["AccentSoftColor"] = ThemeColorMath.Blend(background, accent, isLight ? 0.12 : 0.22),
            ["AccentSecondaryColor"] = colors.AccentSecondary,
            ["AccentForegroundColor"] = PickAccentForeground(accent),

            // ---- 文字 ----
            ["TextColor"] = colors.Text,
            ["SubtleTextColor"] = colors.TextSubtle,
            ["MutedTextColor"] = colors.TextMuted,

            // ---- 边框 ----
            ["BorderColor"] = colors.Border,
            ["BorderStrongColor"] = colors.BorderStrong,

            // ---- 状态 ----
            ["SuccessColor"] = colors.Success,
            ["DangerColor"] = colors.Danger,
            ["WarningColor"] = colors.Warning,

            // ---- 交互遮罩（浅色用黑色蒙层，深色用白色蒙层） ----
            ["GhostHoverColor"] = isLight ? "#14000000" : "#1FFFFFFF",
            ["GhostPressedColor"] = isLight ? "#24000000" : "#33FFFFFF",
            ["DangerHoverColor"] = ThemeColorMath.WithAlpha(colors.Danger, 0x26),
            ["DangerPressedColor"] = ThemeColorMath.WithAlpha(colors.Danger, 0x40),
            ["ToggleThumbColor"] = isLight ? "#A9A6BC" : "#9A9AB0",

            // ---- 滚动条 / 导航选中 ----
            ["ScrollThumbColor"] = isLight
                ? ThemeColorMath.Blend(colors.Border, colors.Text, 0.25)
                : ThemeColorMath.Blend(colors.Border, "#FFFFFF", 0.10),
            ["ScrollThumbHoverColor"] = isLight
                ? ThemeColorMath.Blend(colors.Border, colors.Text, 0.42)
                : ThemeColorMath.Blend(colors.Border, "#FFFFFF", 0.22),
            ["NavHoverColor"] = ThemeColorMath.Blend(background, accent, isLight ? 0.04 : 0.07),
            ["NavSelectedColor"] = ThemeColorMath.Blend(background, accent, isLight ? 0.08 : 0.13),

            // ---- HUD 半透明卡片 ----
            ["HudCardColor"] = ThemeColorMath.WithAlpha(
                isLight ? colors.Surface : ThemeColorMath.Blend(background, accent, 0.04), 0xF2),
            ["HudBorderColor"] = isLight ? "#26000000" : "#26FFFFFF",
            ["HudHandleColor"] = isLight ? "#33000000" : "#33FFFFFF",
            ["HudTrackColor"] = isLight ? "#1F000000" : "#1FFFFFFF",
            ["HudRingTrackColor"] = colors.Border,

            // ---- 渐变端点 ----
            ["AccentGradientStart"] = isLight ? ThemeColorMath.Lighten(accent, 0.18) : ThemeColorMath.Lighten(accent, 0.12),
            ["AccentGradientMid"] = accent,
            ["AccentGradientEnd"] = isLight ? ThemeColorMath.Darken(accent, 0.22) : ThemeColorMath.Darken(accent, 0.20),
            ["AccentGradientSecondary"] = colors.AccentSecondary,
            ["HeroGradientStart"] = ThemeColorMath.Blend(background, accent, isLight ? 0.14 : 0.22),
            ["HeroGradientMid"] = ThemeColorMath.Blend(background, colors.Surface, 0.7),
            ["HeroGradientEnd"] = background
        };
    }

    /// <summary>
    /// 强调色上的文字颜色：在白色和深色之间按 WCAG 相对亮度选对比度更高的一个。
    /// 浅色强调色（Nord Frost、Rosé Pine Rose 等）上用白字会看不清，必须随强调色明暗切换。
    /// </summary>
    private static string PickAccentForeground(string accent)
    {
        const string dark = "#000000";
        var accentLuminance = RelativeLuminance(accent);
        var whiteContrast = 1.05 / (accentLuminance + 0.05);
        var darkContrast = (accentLuminance + 0.05) / (RelativeLuminance(dark) + 0.05);
        return whiteContrast >= darkContrast ? "#FFFFFF" : dark;
    }

    /// <summary>WCAG 2.x 相对亮度（sRGB gamma 解码后加权），用于计算文字对比度。</summary>
    private static double RelativeLuminance(string hex)
    {
        var (_, red, green, blue) = ThemeColorMath.Parse(hex);
        return (0.2126 * Linearize(red)) + (0.7152 * Linearize(green)) + (0.0722 * Linearize(blue));

        static double Linearize(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
    }
}
