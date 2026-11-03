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
                }
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
        var config = new AppConfig { Ui = new UiConfig { Background = null! } };

        config.Normalize();

        Assert.NotNull(config.Ui.Background);
        Assert.Equal(BackgroundConfig.ModeTheme, config.Ui.Background.Mode);
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

        var json = JsonSerializer.Serialize(config, ConfigJson.Options);
        Assert.Contains("\"motion\": \"subtle\"", json);
        Assert.Contains("\"background\"", json);
        Assert.Contains("\"mode\": \"gradient\"", json);

        var loaded = JsonSerializer.Deserialize<AppConfig>(json, ConfigJson.Options)!;
        loaded.Normalize();
        Assert.Equal(MotionModes.Subtle, loaded.Ui.Motion);
        Assert.Equal("gradient", loaded.Ui.Background.Mode);
        Assert.Equal("#112233", loaded.Ui.Background.GradientStart);
        Assert.Equal(0.7, loaded.Ui.Background.SurfaceOpacity);
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
