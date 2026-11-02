namespace VoxIME.Core.Appearance;

/// <summary>主题 + 配色解析后的「当前生效外观」。</summary>
public sealed class ResolvedTheme
{
    public ResolvedTheme(string themeMode, string colorSchemeId, bool isLight, ThemeColorSet colors)
    {
        ThemeMode = themeMode;
        ColorSchemeId = colorSchemeId;
        IsLight = isLight;
        Colors = colors;
    }

    /// <summary>规范化后的主题模式（system/light/dark）。</summary>
    public string ThemeMode { get; }

    /// <summary>实际生效的配色方案 ID（可能因模式不匹配而回退）。</summary>
    public string ColorSchemeId { get; }

    /// <summary>实际使用的明暗。</summary>
    public bool IsLight { get; }

    /// <summary>实际使用的语义色。</summary>
    public ThemeColorSet Colors { get; }

    /// <summary>实际生效的配色方案。</summary>
    public ColorScheme Scheme => ThemeCatalog.Find(ColorSchemeId) ?? ThemeCatalog.Default;

    /// <summary>跟随系统时是否因系统明暗切换而重新解析过（仅信息用途）。</summary>
    public override string ToString() =>
        $"{ThemeMode}/{(IsLight ? "light" : "dark")}/{ColorSchemeId}";
}

/// <summary>
/// 主题解析：把「主题模式（跟随系统/浅色/深色）」+「配色方案 ID」+ 系统当前明暗
/// 解析成实际明暗与语义色。无 UI 依赖，便于离线单测。
/// </summary>
public static class ThemeResolver
{
    /// <summary>解析当前生效外观（幂等、不修改配置）。</summary>
    public static ResolvedTheme Resolve(string? themeMode, string? colorSchemeId, bool systemIsLight)
    {
        var mode = ThemeModes.Normalize(themeMode);
        var isLight = ThemeModes.IsLight(mode, systemIsLight);

        var scheme = ThemeCatalog.Find(colorSchemeId) ?? ThemeCatalog.Default;
        if (!scheme.Supports(isLight))
        {
            scheme = FallbackFor(scheme, isLight);
        }

        var colors = scheme.For(isLight) ?? ThemeCatalog.Default.For(isLight)
            ?? throw new InvalidOperationException("默认配色方案必须同时提供浅色与深色颜色。");

        return new ResolvedTheme(mode, scheme.Id, isLight, colors);
    }

    /// <summary>
    /// 模式不匹配时的回退策略：
    /// Catppuccin 系列优先换成同族的对应明暗口味（浅色 → Latte，深色 → Mocha），
    /// 其它情况回退到深浅双主题的默认方案。
    /// </summary>
    private static ColorScheme FallbackFor(ColorScheme scheme, bool isLight)
    {
        if (scheme.Id.StartsWith(ThemeCatalog.CatppuccinPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var sameFamily = ThemeCatalog.Find(isLight ? "catppuccin-latte" : "catppuccin-mocha");
            if (sameFamily is not null && sameFamily.Supports(isLight))
            {
                return sameFamily;
            }
        }

        return ThemeCatalog.Default;
    }
}
