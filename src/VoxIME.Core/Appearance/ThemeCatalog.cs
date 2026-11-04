namespace VoxIME.Core.Appearance;

/// <summary>
/// 内置配色方案目录。新增配色只需在这里加一条（给浅色/深色语义色），
/// 设置页、主题解析和全部派生色会自动生效；不需要改任何 XAML。
/// </summary>
public static class ThemeCatalog
{
    /// <summary>Catppuccin 系列 ID 前缀（用于失配时同族回退）。</summary>
    public const string CatppuccinPrefix = "catppuccin-";

    /// <summary>默认/兜底配色方案 ID（同时提供浅色与深色，能适配任意主题模式）。</summary>
    public const string DefaultColorSchemeId = "voxime";

    private static readonly ColorScheme Voxime = new(
        "voxime",
        "VoxIME 紫罗兰",
        "原版紫罗兰强调色，深浅双主题",
        light: new ThemeColorSet
        {
            Background = "#F6F5FB",
            Surface = "#FFFFFF",
            SurfaceHover = "#F1EEF9",
            Input = "#EFEDF8",
            Popup = "#FFFFFF",
            Accent = "#6D4BFF",
            AccentSecondary = "#2F9BFF",
            Text = "#1D1B2D",
            TextSubtle = "#5E5A76",
            TextMuted = "#8B87A0",
            Border = "#E3E0F0",
            BorderStrong = "#CEC9E6",
            Success = "#1E9E57",
            Danger = "#D6224B",
            Warning = "#B07A00"
        },
        dark: new ThemeColorSet
        {
            Background = "#12121A",
            Surface = "#1C1C27",
            SurfaceHover = "#232331",
            Input = "#15151F",
            Popup = "#1E1E2B",
            Accent = "#7C5CFF",
            AccentSecondary = "#4ECBFF",
            Text = "#E8E8F0",
            TextSubtle = "#8A8AA0",
            TextMuted = "#5F5F74",
            Border = "#2A2A3A",
            BorderStrong = "#3A3A52",
            Success = "#4ADE80",
            Danger = "#FF6B6B",
            Warning = "#FBBF24"
        });

    private static readonly ColorScheme CatppuccinLatte = new(
        "catppuccin-latte",
        "Catppuccin Latte",
        "浅色 · 奶油底 Mauve 强调",
        light: new ThemeColorSet
        {
            Background = "#E6E9EF",
            Surface = "#EFF1F5",
            SurfaceHover = "#DCE0E8",
            Input = "#DCE0E8",
            Popup = "#EFF1F5",
            Accent = "#8839EF",
            AccentSecondary = "#1E66F5",
            Text = "#4C4F69",
            TextSubtle = "#6C6F85",
            TextMuted = "#9CA0B0",
            Border = "#CCD0DA",
            BorderStrong = "#BCC0CC",
            Success = "#40A02B",
            Danger = "#D20F39",
            Warning = "#DF8E1D"
        },
        dark: null);

    private static readonly ColorScheme CatppuccinFrappe = new(
        "catppuccin-frappe",
        "Catppuccin Frappé",
        "深色 · 低对比冷调",
        light: null,
        dark: new ThemeColorSet
        {
            Background = "#292C3C",
            Surface = "#303446",
            SurfaceHover = "#414559",
            Input = "#232634",
            Popup = "#303446",
            Accent = "#CA9EE6",
            AccentSecondary = "#8CAAEE",
            Text = "#C6D0F5",
            TextSubtle = "#A5ADCE",
            TextMuted = "#838BA7",
            Border = "#414559",
            BorderStrong = "#51576D",
            Success = "#A6D189",
            Danger = "#E78284",
            Warning = "#E5C890"
        });

    private static readonly ColorScheme CatppuccinMacchiato = new(
        "catppuccin-macchiato",
        "Catppuccin Macchiato",
        "深色 · 中对比冷调",
        light: null,
        dark: new ThemeColorSet
        {
            Background = "#1E2030",
            Surface = "#24273A",
            SurfaceHover = "#363A4F",
            Input = "#181926",
            Popup = "#24273A",
            Accent = "#C6A0F6",
            AccentSecondary = "#8AADF4",
            Text = "#CAD3F5",
            TextSubtle = "#A5ADCB",
            TextMuted = "#8087A2",
            Border = "#363A4F",
            BorderStrong = "#494D64",
            Success = "#A6DA95",
            Danger = "#ED8796",
            Warning = "#EED49F"
        });

    private static readonly ColorScheme CatppuccinMocha = new(
        "catppuccin-mocha",
        "Catppuccin Mocha",
        "深色 · 高对比经典",
        light: null,
        dark: new ThemeColorSet
        {
            Background = "#181825",
            Surface = "#1E1E2E",
            SurfaceHover = "#313244",
            Input = "#11111B",
            Popup = "#1E1E2E",
            Accent = "#CBA6F7",
            AccentSecondary = "#89B4FA",
            Text = "#CDD6F4",
            TextSubtle = "#A6ADC8",
            TextMuted = "#7F849C",
            Border = "#313244",
            BorderStrong = "#45475A",
            Success = "#A6E3A1",
            Danger = "#F38BA8",
            Warning = "#F9E2AF"
        });

    private static readonly ColorScheme Nord = new(
        "nord",
        "Nord",
        "深色 · 极地冷调 Frost 蓝",
        light: null,
        dark: new ThemeColorSet
        {
            Background = "#2E3440",
            Surface = "#3B4252",
            SurfaceHover = "#434C5E",
            Input = "#2E3440",
            Popup = "#3B4252",
            Accent = "#88C0D0",
            AccentSecondary = "#81A1C1",
            Text = "#ECEFF4",
            TextSubtle = "#D8DEE9",
            TextMuted = "#8A97AB",
            Border = "#434C5E",
            BorderStrong = "#4C566A",
            Success = "#A3BE8C",
            Danger = "#BF616A",
            Warning = "#EBCB8B"
        });

    private static readonly ColorScheme RosePine = new(
        "rose-pine",
        "Rosé Pine",
        "浅色 Dawn / 深色 柔雾玫瑰",
        light: new ThemeColorSet
        {
            Background = "#FAF4ED",
            Surface = "#F2E9E1",
            SurfaceHover = "#F4EDE8",
            Input = "#FFFDF8",
            Popup = "#FFFDF8",
            Accent = "#D7827E",
            AccentSecondary = "#907AA9",
            Text = "#575279",
            TextSubtle = "#797593",
            TextMuted = "#9893A5",
            Border = "#DFDAD9",
            BorderStrong = "#CECACD",
            Success = "#286983",
            Danger = "#B4637A",
            Warning = "#EA9D34"
        },
        dark: new ThemeColorSet
        {
            Background = "#191724",
            Surface = "#1F1D2E",
            SurfaceHover = "#26233A",
            Input = "#26233A",
            Popup = "#1F1D2E",
            Accent = "#EBBCBA",
            AccentSecondary = "#C4A7E7",
            Text = "#E0DEF4",
            TextSubtle = "#908CAA",
            TextMuted = "#6E6A86",
            Border = "#403D52",
            BorderStrong = "#524F67",
            Success = "#31748F",
            Danger = "#EB6F92",
            Warning = "#F6C177"
        });

    private static readonly IReadOnlyList<ColorScheme> AllSchemes = new[]
    {
        Voxime,
        Nord,
        RosePine,
        CatppuccinLatte,
        CatppuccinFrappe,
        CatppuccinMacchiato,
        CatppuccinMocha
    };

    private static readonly IReadOnlyList<ColorScheme> LightSchemes = AllSchemes.Where(s => s.SupportsLight).ToArray();
    private static readonly IReadOnlyList<ColorScheme> DarkSchemes = AllSchemes.Where(s => s.SupportsDark).ToArray();

    /// <summary>全部配色方案（设置页展示顺序）。</summary>
    public static IReadOnlyList<ColorScheme> Schemes => AllSchemes;

    /// <summary>默认配色方案（同时支持浅色与深色）。</summary>
    public static ColorScheme Default => Voxime;

    /// <summary>按 ID 查找（大小写不敏感、忽略空白）；找不到返回 null。</summary>
    public static ColorScheme? Find(string? id)
    {
        var key = id?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        return AllSchemes.FirstOrDefault(s => string.Equals(s.Id, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>把任意字符串规范化为合法方案 ID；无法识别时回退默认方案。</summary>
    public static string NormalizeId(string? id) => Find(id)?.Id ?? DefaultColorSchemeId;

    /// <summary>指定明暗模式下可用的配色方案（设置页按模式过滤）。</summary>
    public static IReadOnlyList<ColorScheme> ForMode(bool isLight) => isLight ? LightSchemes : DarkSchemes;
}
