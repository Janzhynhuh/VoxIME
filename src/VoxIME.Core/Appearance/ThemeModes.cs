namespace VoxIME.Core.Appearance;

/// <summary>
/// 明暗主题模式（与「配色方案」正交的一维设置）：
/// <c>system</c> 跟随 Windows 应用模式、<c>light</c> 浅色、<c>dark</c> 深色。
/// 具体颜色由 <see cref="ThemeCatalog"/> 里的配色方案提供。
/// </summary>
public static class ThemeModes
{
    /// <summary>跟随系统（读取 Windows 个性化设置里的「应用模式」）。</summary>
    public const string System = "system";

    /// <summary>浅色主题。</summary>
    public const string Light = "light";

    /// <summary>深色主题。</summary>
    public const string Dark = "dark";

    /// <summary>全部合法取值（设置页下拉/单选用）。</summary>
    public static IReadOnlyList<string> All { get; } = new[] { System, Light, Dark };

    /// <summary>把配置里的任意字符串规范化为合法主题模式；无法识别时回退深色（历史默认）。</summary>
    public static string Normalize(string? value)
    {
        var text = value?.Trim();
        if (string.Equals(text, Light, StringComparison.OrdinalIgnoreCase))
        {
            return Light;
        }

        if (string.Equals(text, System, StringComparison.OrdinalIgnoreCase))
        {
            return System;
        }

        return Dark;
    }

    /// <summary>解析当前实际应使用的明暗：system 取系统值，其余取字面值。</summary>
    public static bool IsLight(string? themeMode, bool systemIsLight)
    {
        var mode = Normalize(themeMode);
        return mode == Light || (mode == System && systemIsLight);
    }
}
