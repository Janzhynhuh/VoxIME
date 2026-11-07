using System.Text.Json;
using VoxIME.Core.Appearance;
using VoxIME.Core.Configuration;

namespace VoxIME.Core.Tests.Appearance;

public sealed class MotionAndBackgroundConfigTests
{
    [Theory]
    [InlineData("expressive", "expressive")]
    [InlineData("Subtle", "subtle")]
    [InlineData(" OFF ", "off")]
    [InlineData("", "expressive")]
    [InlineData("crazy", "expressive")]
    public void MotionModes_Normalize(string input, string expected)
    {
        Assert.Equal(expected, MotionModes.Normalize(input));
    }

    [Theory]
    [InlineData("expressive", true, false)]
    [InlineData("subtle", true, true)]
    [InlineData("off", false, false)]
    public void MotionModes_Flags(string mode, bool enabled, bool subtle)
    {
        Assert.Equal(enabled, MotionModes.IsEnabled(mode));
        Assert.Equal(subtle, MotionModes.IsSubtle(mode));
    }

    [Fact]
    public void AppConfig_CreateDefault_HasExpressiveMotionAndThemeBackground()
    {
        var config = AppConfig.CreateDefault();

        Assert.Equal(MotionModes.Expressive, config.Ui.Motion);
        Assert.NotNull(config.Ui.Background);
        Assert.Equal(BackgroundConfig.ModeTheme, config.Ui.Background.Mode);
        Assert.False(config.Ui.Background.IsCustom);
        Assert.NotNull(config.Ui.HudBackground);
        Assert.Equal(BackgroundConfig.ModeTheme, config.Ui.HudBackground.Mode);
        Assert.False(config.Ui.HudBackground.IsCustom);
        Assert.Equal(1.0, config.Ui.HudScale);
        Assert.Equal(18, config.Ui.HudCornerRadius);
        Assert.Equal(1.0, config.Ui.HudCardOpacity);
        Assert.Null(config.Ui.HudBorderColor);
        Assert.Equal(1.0, config.Ui.HudBorderThickness);
        Assert.Equal(42, config.Ui.HudBottomMargin);
        Assert.Equal(0, config.Ui.HudHorizontalOffset);
    }

    [Fact]
    public void Normalize_RepairsMotionAndBackground()
    {
        var config = new AppConfig
        {
            Ui = new UiConfig
            {
                Motion = "wobble",
                Background = new BackgroundConfig
                {
                    Mode = "not-a-mode",
                    Fit = "stretchy",
                    ImagePath = "   D:\\pics\\bg.png   ",
                    ImageOpacity = 9,
                    BlurRadius = -5,
                    Dim = 3,
                    SurfaceOpacity = 0,
                    SolidColor = "not-a-color",
                    GradientStart = "#a1b2c3",
                    GradientEnd = ""
                },
                HudBackground = new BackgroundConfig
                {
                    Mode = "bogus",
                    Fit = "zap",
                    ImageOpacity = -1,
                    BlurRadius = 999,
                    Dim = -2,
                    SolidColor = "#zzzzzz"
                },
                HudScale = 9,
                HudCornerRadius = -5,
                HudCardOpacity = 0,
                HudBorderColor = "red",
                HudBorderThickness = 9,
                HudBottomMargin = 999,
                HudHorizontalOffset = -999
            }
        };

        config.Normalize();

        Assert.Equal(MotionModes.Expressive, config.Ui.Motion);
        var background = config.Ui.Background;
        Assert.Equal(BackgroundConfig.ModeTheme, background.Mode);
        Assert.Equal("cover", background.Fit);
        Assert.Equal("D:\\pics\\bg.png", background.ImagePath);
        Assert.Equal(1.0, background.ImageOpacity);
        Assert.Equal(0, background.BlurRadius);
        Assert.Equal(0.95, background.Dim);
        Assert.Equal(0.35, background.SurfaceOpacity);
        Assert.Null(background.SolidColor);
        Assert.Equal("#A1B2C3", background.GradientStart);
        Assert.Null(background.GradientEnd);

        var hudBackground = config.Ui.HudBackground;
        Assert.Equal(BackgroundConfig.ModeTheme, hudBackground.Mode);
        Assert.Equal("cover", hudBackground.Fit);
        Assert.Equal(0.2, hudBackground.ImageOpacity);
        Assert.Equal(60, hudBackground.BlurRadius);
        Assert.Equal(0, hudBackground.Dim);
        Assert.Null(hudBackground.SolidColor);

        Assert.Equal(1.5, config.Ui.HudScale);
        Assert.Equal(0, config.Ui.HudCornerRadius);
        Assert.Equal(0.35, config.Ui.HudCardOpacity);
        Assert.Null(config.Ui.HudBorderColor);
        Assert.Equal(4, config.Ui.HudBorderThickness);
        Assert.Equal(300, config.Ui.HudBottomMargin);
        Assert.Equal(-400, config.Ui.HudHorizontalOffset);
    }

    [Fact]
    public void Normalize_KeepsValidCustomBackground()
    {
        var config = new AppConfig
        {
            Ui = new UiConfig
            {
                Motion = MotionModes.Subtle,
                Background = new BackgroundConfig
                {
                    Mode = "image",
                    Fit = "contain",
                    ImagePath = "C:\\pics\\wall.jpg",
                    ImageOpacity = 0.7,
                    BlurRadius = 30,
                    Dim = 0.5,
                    SurfaceOpacity = 0.9
                }
            }
        };

        config.Normalize();

        Assert.Equal(MotionModes.Subtle, config.Ui.Motion);
        Assert.True(config.Ui.Background.IsCustom);
        Assert.Equal("C:\\pics\\wall.jpg", config.Ui.Background.ImagePath);
        Assert.Equal(0.7, config.Ui.Background.ImageOpacity);
        Assert.Equal(30, config.Ui.Background.BlurRadius);
    }

    [Fact]
    public void Normalize_DefaultsNullBackground()
    {
        var config = new AppConfig { Ui = new UiConfig { Background = null!, HudBackground = null! } };

        config.Normalize();

        Assert.NotNull(config.Ui.Background);
        Assert.Equal(BackgroundConfig.ModeTheme, config.Ui.Background.Mode);
        Assert.NotNull(config.Ui.HudBackground);
        Assert.Equal(BackgroundConfig.ModeTheme, config.Ui.HudBackground.Mode);
    }

    [Fact]
    public void Serialization_RoundTripsMotionAndBackground()
    {
        var config = AppConfig.CreateDefault();
        config.Ui.Motion = MotionModes.Subtle;
        config.Ui.Background = new BackgroundConfig
        {
            Mode = "gradient",
            GradientStart = "#112233",
            GradientEnd = "#445566",
            SurfaceOpacity = 0.7
        };
        config.Ui.HudBackground = new BackgroundConfig
        {
            Mode = "solid",
            SolidColor = "#AABBCC",
            Dim = 0.6
        };
        config.Ui.HudScale = 1.25;
        config.Ui.HudCornerRadius = 6;
        config.Ui.HudCardOpacity = 0.8;
        config.Ui.HudBorderColor = "#112233";
        config.Ui.HudBorderThickness = 2;
        config.Ui.HudBottomMargin = 80;
        config.Ui.HudHorizontalOffset = -120;

        var json = JsonSerializer.Serialize(config, ConfigJson.Options);
        Assert.Contains("\"motion\": \"subtle\"", json);
        Assert.Contains("\"background\"", json);
        Assert.Contains("\"hudBackground\"", json);
        Assert.Contains("\"mode\": \"gradient\"", json);
        Assert.Contains("\"mode\": \"solid\"", json);

        var loaded = JsonSerializer.Deserialize<AppConfig>(json, ConfigJson.Options)!;
        loaded.Normalize();
        Assert.Equal(MotionModes.Subtle, loaded.Ui.Motion);
        Assert.Equal("gradient", loaded.Ui.Background.Mode);
        Assert.Equal("#112233", loaded.Ui.Background.GradientStart);
        Assert.Equal(0.7, loaded.Ui.Background.SurfaceOpacity);
        Assert.Equal("solid", loaded.Ui.HudBackground.Mode);
        Assert.Equal("#AABBCC", loaded.Ui.HudBackground.SolidColor);
        Assert.Equal(0.6, loaded.Ui.HudBackground.Dim);
        Assert.Equal(1.25, loaded.Ui.HudScale);
        Assert.Equal(6, loaded.Ui.HudCornerRadius);
        Assert.Equal(0.8, loaded.Ui.HudCardOpacity);
        Assert.Equal("#112233", loaded.Ui.HudBorderColor);
        Assert.Equal(2, loaded.Ui.HudBorderThickness);
        Assert.Equal(80, loaded.Ui.HudBottomMargin);
        Assert.Equal(-120, loaded.Ui.HudHorizontalOffset);
    }

    [Fact]
    public void ColorMath_Luminance_ReflectsBrightness()
    {
        Assert.Equal(0, ThemeColorMath.Luminance("#000000"), 3);
        Assert.Equal(1, ThemeColorMath.Luminance("#FFFFFF"), 3);
        Assert.True(ThemeColorMath.Luminance("#F6F5FB") > 0.9);
        Assert.True(ThemeColorMath.Luminance("#12121A") < 0.15);
        Assert.True(ThemeColorMath.Luminance("#FF6B6B") > ThemeColorMath.Luminance("#12121A"));
    }
    [Fact]
    public void BackgroundConfig_NormalizeColor_ReturnsNullForInvalid()
    {
        Assert.Null(BackgroundConfig.NormalizeColor(""));
        Assert.Null(BackgroundConfig.NormalizeColor("#12"));
        Assert.Null(BackgroundConfig.NormalizeColor("red"));
        Assert.Equal("#FF8800", BackgroundConfig.NormalizeColor("ff8800"));
    }
}
