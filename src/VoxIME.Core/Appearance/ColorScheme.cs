namespace VoxIME.Core.Appearance;

/// <summary>
/// 一套「配色方案」：同一品牌色思路下的浅色/深色两套语义色。
/// 部分方案只提供单侧（例如 Catppuccin Latte 天生就是浅色，Frappé/Macchiato/Mocha 天生是深色），
/// 与当前主题模式不匹配时由 <see cref="ThemeResolver"/> 回退到同族方案。
/// </summary>
public sealed class ColorScheme
{
    public ColorScheme(string id, string displayName, string description, ThemeColorSet? light, ThemeColorSet? dark)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("配色方案 ID 不能为空。", nameof(id));
        }

        if (light is null && dark is null)
        {
            throw new ArgumentException("配色方案至少需要提供浅色或深色一组颜色。", nameof(light));
        }

        Id = id.Trim();
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? Id : displayName.Trim();
        Description = description?.Trim() ?? "";
        Light = light;
        Dark = dark;
    }

    /// <summary>稳定 ID（写入配置）。</summary>
    public string Id { get; }

    /// <summary>设置页显示名。</summary>
    public string DisplayName { get; }

    /// <summary>设置页说明文案。</summary>
    public string Description { get; }

    /// <summary>浅色模式下的颜色；null 表示该方案不支持浅色。</summary>
    public ThemeColorSet? Light { get; }

    /// <summary>深色模式下的颜色；null 表示该方案不支持深色。</summary>
    public ThemeColorSet? Dark { get; }

    /// <summary>是否可用于浅色模式。</summary>
    public bool SupportsLight => Light is not null;

    /// <summary>是否可用于深色模式。</summary>
    public bool SupportsDark => Dark is not null;

    /// <summary>指定明暗模式是否受支持。</summary>
    public bool Supports(bool isLight) => isLight ? SupportsLight : SupportsDark;

    /// <summary>取指定模式的颜色；不支持时返回 null。</summary>
    public ThemeColorSet? For(bool isLight) => isLight ? Light : Dark;
}
