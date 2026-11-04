using VoxIME.Core.Appearance;
using VoxIME.Core.Configuration;

namespace VoxIME.Core.Tests.Appearance;

public sealed class ThemeAppearanceTests
{
    private static readonly string[] ExpectedColorKeys =
    {
        "BgColor", "CardColor", "CardHoverColor", "InputColor", "PopupColor",
        "AccentColor", "AccentHoverColor", "AccentSoftColor", "AccentSecondaryColor", "AccentForegroundColor",
        "TextColor", "SubtleTextColor", "MutedTextColor",
        "BorderColor", "BorderStrongColor",
        "SuccessColor", "DangerColor", "WarningColor",
        "GhostHoverColor", "GhostPressedColor", "DangerHoverColor", "DangerPressedColor",
        "ToggleThumbColor", "ScrollThumbColor", "ScrollThumbHoverColor",
        "NavHoverColor", "NavSelectedColor",
        "HudCardColor", "HudBorderColor", "HudHandleColor", "HudTrackColor", "HudRingTrackColor",
        "AccentGradientStart", "AccentGradientMid", "AccentGradientEnd", "AccentGradientSecondary",
        "HeroGradientStart", "HeroGradientMid", "HeroGradientEnd"
    };

    [Theory]
    [InlineData("system", "system")]
    [InlineData("Light", "light")]
    [InlineData(" DARK ", "dark")]
    [InlineData("", "dark")]
    [InlineData("neon", "dark")]
    public void ThemeModes_Normalize(string input, string expected)
    {
        Assert.Equal(expected, ThemeModes.Normalize(input));
    }

    [Theory]
    [InlineData("light", false, true)]
    [InlineData("dark", true, false)]
    [InlineData("system", true, true)]
    [InlineData("system", false, false)]
    public void ThemeModes_IsLight(string mode, bool systemIsLight, bool expected)
    {
        Assert.Equal(expected, ThemeModes.IsLight(mode, systemIsLight));
    }

    [Fact]
    public void Catalog_HasSevenSchemes_WithNordRosePineAndCatppuccinFamily()
    {
        Assert.Equal(7, ThemeCatalog.Schemes.Count);
        Assert.Equal(4, ThemeCatalog.Schemes.Count(s => s.Id.StartsWith(ThemeCatalog.CatppuccinPrefix, StringComparison.Ordinal)));

        var voxime = ThemeCatalog.Default;
        Assert.Equal("voxime", voxime.Id);
        Assert.True(voxime.SupportsLight);
        Assert.True(voxime.SupportsDark);

        var nord = ThemeCatalog.Find("nord")!;
        Assert.False(nord.SupportsLight);
        Assert.True(nord.SupportsDark);

        var rosePine = ThemeCatalog.Find("rose-pine")!;
        Assert.True(rosePine.SupportsLight);
        Assert.True(rosePine.SupportsDark);

        Assert.True(ThemeCatalog.Find("catppuccin-latte")!.SupportsLight);
        Assert.False(ThemeCatalog.Find("catppuccin-latte")!.SupportsDark);
        foreach (var id in new[] { "catppuccin-frappe", "catppuccin-macchiato", "catppuccin-mocha" })
        {
            Assert.False(ThemeCatalog.Find(id)!.SupportsLight);
            Assert.True(ThemeCatalog.Find(id)!.SupportsDark);
        }
    }

    [Fact]
    public void Catalog_ForMode_OnlyReturnsSchemesThatSupportTheMode()
    {
        var light = ThemeCatalog.ForMode(isLight: true);
        var dark = ThemeCatalog.ForMode(isLight: false);
        Assert.All(light, s => Assert.True(s.SupportsLight));
        Assert.All(dark, s => Assert.True(s.SupportsDark));
        Assert.Contains(light, s => s.Id == "catppuccin-latte");
        Assert.Contains(light, s => s.Id == "voxime");
        Assert.Contains(dark, s => s.Id == "catppuccin-mocha");
        Assert.Contains(dark, s => s.Id == "nord");
        Assert.DoesNotContain(light, s => s.Id == "nord");
        Assert.Contains(light, s => s.Id == "rose-pine");
        Assert.Contains(dark, s => s.Id == "rose-pine");
        Assert.DoesNotContain(light, s => s.Id == "catppuccin-mocha");
        Assert.DoesNotContain(dark, s => s.Id == "catppuccin-latte");
    }

    [Fact]
    public void Catalog_NormalizeId_FallsBackToDefault()
    {
        Assert.Equal("catppuccin-mocha", ThemeCatalog.NormalizeId("  CATPPUCCIN-MOCHA "));
        Assert.Equal(ThemeCatalog.DefaultColorSchemeId, ThemeCatalog.NormalizeId("not-exist"));
        Assert.Equal(ThemeCatalog.DefaultColorSchemeId, ThemeCatalog.NormalizeId(null));
    }

    [Fact]
    public void Resolve_DefaultIsDarkVoxime()
    {
        var theme = ThemeResolver.Resolve(null, null, systemIsLight: true);

        Assert.Equal("dark", theme.ThemeMode);
        Assert.Equal("voxime", theme.ColorSchemeId);
        Assert.False(theme.IsLight);
        Assert.Equal("#12121A", theme.Colors.Background);
    }

    [Fact]
    public void Resolve_SystemModeFollowsSystemLight()
    {
        var light = ThemeResolver.Resolve("system", "voxime", systemIsLight: true);
        var dark = ThemeResolver.Resolve("system", "voxime", systemIsLight: false);

        Assert.True(light.IsLight);
        Assert.Equal("#F6F5FB", light.Colors.Background);
        Assert.False(dark.IsLight);
        Assert.Equal("#12121A", dark.Colors.Background);
    }

    [Fact]
    public void Resolve_LightModeUsesLightPalette()
    {
        var theme = ThemeResolver.Resolve("light", "catppuccin-latte", systemIsLight: false);

        Assert.True(theme.IsLight);
        Assert.Equal("catppuccin-latte", theme.ColorSchemeId);
        Assert.Equal("#E6E9EF", theme.Colors.Background);
    }

    [Fact]
    public void Resolve_DarkHeavySchemeFallsBackToSameFamilyLight()
    {
        // Mocha 只有深色；跑到浅色模式时应回退同族 Latte，而不是直接变回紫罗兰。
        var theme = ThemeResolver.Resolve("light", "catppuccin-mocha", systemIsLight: false);

        Assert.True(theme.IsLight);
        Assert.Equal("catppuccin-latte", theme.ColorSchemeId);
    }

    [Fact]
    public void Resolve_LightOnlySchemeFallsBackToSameFamilyDark()
    {
        var theme = ThemeResolver.Resolve("dark", "catppuccin-latte", systemIsLight: true);

        Assert.False(theme.IsLight);
        Assert.Equal("catppuccin-mocha", theme.ColorSchemeId);
    }

    [Fact]
    public void Resolve_UnknownSchemeUsesDefault()
    {
        var theme = ThemeResolver.Resolve("dark", "does-not-exist", systemIsLight: false);

        Assert.Equal("voxime", theme.ColorSchemeId);
        Assert.Equal("voxime", theme.Scheme.Id);
    }

    [Fact]
    public void Tokens_ContainAllExpectedKeys()
    {
        var tokens = ThemeTokens.Build(ThemeCatalog.Default.Dark!, isLight: false);

        Assert.Equal(ExpectedColorKeys.Length, tokens.Count);
        foreach (var key in ExpectedColorKeys)
        {
            Assert.True(tokens.ContainsKey(key), $"缺少颜色键 {key}");
            Assert.True(ThemeColorMath.IsValid(tokens[key]), $"{key} 不是合法颜色：{tokens[key]}");
        }

        // HUD 卡片必须保留半透明（#AARRGGBB）。
        Assert.Equal(9, tokens["HudCardColor"].Length);
    }

    [Fact]
    public void Tokens_OverlayDifferBetweenLightAndDark()
    {
        var dark = ThemeTokens.Build(ThemeCatalog.Default.Dark!, isLight: false);
        var light = ThemeTokens.Build(ThemeCatalog.Default.Light!, isLight: true);

        Assert.Equal("#1FFFFFFF", dark["GhostHoverColor"]);
        Assert.Equal("#14000000", light["GhostHoverColor"]);
        Assert.Equal("#33FFFFFF", dark["HudHandleColor"]);
        Assert.Equal("#33000000", light["HudHandleColor"]);
    }

    [Fact]
    public void Tokens_EverySchemeAndModeProducesValidColors()
    {
        foreach (var scheme in ThemeCatalog.Schemes)
        {
            foreach (var isLight in new[] { true, false })
            {
                var colors = scheme.For(isLight);
                if (colors is null)
                {
                    continue;
                }

                var tokens = ThemeTokens.Build(colors, isLight);
                Assert.Equal(ExpectedColorKeys.Length, tokens.Count);
                Assert.All(tokens.Values, hex => Assert.True(ThemeColorMath.IsValid(hex)));
            }
        }
    }

    [Fact]
    public void ColorMath_BlendLightenDarkenAndAlpha()
    {
        Assert.Equal("#808080", ThemeColorMath.Blend("#000000", "#FFFFFF", 0.5));
        Assert.Equal("#FFFFFF", ThemeColorMath.Lighten("#000000", 1));
        Assert.Equal("#000000", ThemeColorMath.Darken("#FFFFFF", 1));
        Assert.Equal("#26FF6B6B", ThemeColorMath.WithAlpha("#FF6B6B", 0x26));
        Assert.Equal("#FF0000", ThemeColorMath.ToHex(ThemeColorMath.Parse("#FF0000")));
        Assert.False(ThemeColorMath.IsValid("nope"));
        Assert.True(ThemeColorMath.IsValid("#A1B2C3"));
    }

    [Fact]
    public void Tokens_AccentForeground_MeetsContrast()
    {
        foreach (var scheme in ThemeCatalog.Schemes)
        {
            foreach (var isLight in new[] { true, false })
            {
                var colors = scheme.For(isLight);
                if (colors is null)
                {
                    continue;
                }

                var tokens = ThemeTokens.Build(colors, isLight);
                var contrast = ContrastRatio(tokens["AccentColor"], tokens["AccentForegroundColor"]);
                Assert.True(contrast >= 4.5, $"{scheme.Id}/{(isLight ? "light" : "dark")} 强调色前景对比度只有 {contrast:0.00}");
            }
        }
    }

    private static double ContrastRatio(string first, string second)
    {
        var a = RelativeLuminance(first);
        var b = RelativeLuminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

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
    [Fact]
    public void AppConfig_Normalize_RepairsThemeAndColorScheme()
    {
        var config = new AppConfig
        {
            Ui = new UiConfig { Theme = "neon", ColorScheme = "no-such-scheme", HistoryLimit = 500 }
        };

        config.Normalize();

        Assert.Equal(ThemeModes.Dark, config.Ui.Theme);
        Assert.Equal(ThemeCatalog.DefaultColorSchemeId, config.Ui.ColorScheme);
        Assert.Equal(500, config.Ui.HistoryLimit);
    }

    [Fact]
    public void AppConfig_Normalize_KeepsValidThemeAndColorScheme()
    {
        var config = new AppConfig
        {
            Ui = new UiConfig { Theme = "system", ColorScheme = "catppuccin-mocha" }
        };

        config.Normalize();

        Assert.Equal("system", config.Ui.Theme);
        Assert.Equal("catppuccin-mocha", config.Ui.ColorScheme);
    }

    [Fact]
    public void AppConfig_CreateDefault_UsesDarkVoximeTheme()
    {
        var config = AppConfig.CreateDefault();

        Assert.Equal(ThemeModes.Dark, config.Ui.Theme);
        Assert.Equal(ThemeCatalog.DefaultColorSchemeId, config.Ui.ColorScheme);
    }
}
