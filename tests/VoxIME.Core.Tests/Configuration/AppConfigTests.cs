using System.Text.Json;
using VoxIME.Core;
using VoxIME.Core.Configuration;

namespace VoxIME.Core.Tests.Configuration;

public sealed class AppConfigTests
{
    [Fact]
    public void CreateDefault_HasUsableMockProfile()
    {
        var config = AppConfig.CreateDefault();
        config.Normalize();

        Assert.NotEmpty(config.ProviderProfiles);
        Assert.Equal("mock", config.ActiveProviderProfileId);
        Assert.NotNull(config.GetActiveProfile());
        Assert.Equal(AppInfo.DefaultHotkeyPushToTalk, config.Hotkeys.PushToTalk);
    }

    [Fact]
    public void Normalize_ClampsOutOfRangeValues()
    {
        var config = new AppConfig
        {
            Audio = new AudioConfig { MaxDurationSeconds = 9999, SilenceTimeoutMs = 1, VadThreshold = 42 },
            Live2D = new Live2DConfig { Width = 10, Height = 9000, Opacity = 5, Scale = 0, MouthSensitivity = 100, EyeFollowFps = 999 },
            Tts = new TtsConfig { Rate = 99, Volume = -5 },
            Ui = new UiConfig { HistoryLimit = 0 },
            Logging = new LoggingConfig { RetainDays = 0, MaxFileSizeKb = 1 }
        };

        config.Normalize();

        Assert.Equal(600, config.Audio.MaxDurationSeconds);
        Assert.Equal(200, config.Audio.SilenceTimeoutMs);
        Assert.Equal(0.2, config.Audio.VadThreshold);
        Assert.Equal(160, config.Live2D.Width);
        Assert.Equal(1600, config.Live2D.Height);
        Assert.Equal(1.0, config.Live2D.Opacity);
        Assert.Equal(0.2, config.Live2D.Scale);
        Assert.Equal(5.0, config.Live2D.MouthSensitivity);
        Assert.Equal(60, config.Live2D.EyeFollowFps);
        Assert.Equal(10, config.Tts.Rate);
        Assert.Equal(0, config.Tts.Volume);
        Assert.Equal(10, config.Ui.HistoryLimit);
        Assert.Equal(1, config.Logging.RetainDays);
        Assert.Equal(64, config.Logging.MaxFileSizeKb);
    }

    [Fact]
    public void Normalize_ClampsEyeFollowFpsToValidRange()
    {
        var low = new AppConfig { Live2D = new Live2DConfig { EyeFollowFps = -3 } };
        low.Normalize();
        Assert.Equal(5, low.Live2D.EyeFollowFps);

        var high = new AppConfig { Live2D = new Live2DConfig { EyeFollowFps = 1000 } };
        high.Normalize();
        Assert.Equal(60, high.Live2D.EyeFollowFps);

        var normal = new AppConfig { Live2D = new Live2DConfig { EyeFollowFps = 45 } };
        normal.Normalize();
        Assert.Equal(45, normal.Live2D.EyeFollowFps);
    }

    [Fact]
    public void Normalize_DeduplicatesProfileIds_AndFixesMissingActiveProfile()
    {
        var config = new AppConfig
        {
            ActiveProviderProfileId = "missing",
            ProviderProfiles = new List<ProviderProfile>
            {
                new() { Id = "same", Name = "A", TypeId = "mock" },
                new() { Id = "same", Name = "B", TypeId = "mock" }
            }
        };

        config.Normalize();

        Assert.Equal(2, config.ProviderProfiles.Count);
        Assert.Equal(2, config.ProviderProfiles.Select(p => p.Id).Distinct().Count());
        Assert.NotNull(config.GetActiveProfile());
        Assert.Contains(config.ProviderProfiles, p => p.Id == config.ActiveProviderProfileId);
    }

    [Fact]
    public void Normalize_ReplacesEmptyProviderList()
    {
        var config = new AppConfig { ProviderProfiles = new List<ProviderProfile>() };

        config.Normalize();

        Assert.Single(config.ProviderProfiles);
        Assert.Equal("mock", config.ProviderProfiles[0].TypeId);
    }

    [Fact]
    public void Serialization_UsesCamelCaseAndStringEnums()
    {
        var config = AppConfig.CreateDefault();
        config.Insertion.Mode = InsertionMode.SimulateTyping;

        var json = JsonSerializer.Serialize(config, ConfigJson.Options);

        Assert.Contains("\"mode\": \"simulateTyping\"", json);
        Assert.Contains("\"activeProviderProfileId\"", json);
        // 敏感明文不得落盘（仅 Protected 密文字段允许出现）
        Assert.DoesNotContain("\"apiKey\":", json);
        Assert.DoesNotContain("\"apiSecret\":", json);
    }

    [Fact]
    public void Clone_IsDeepCopy()
    {
        var config = AppConfig.CreateDefault();
        var clone = config.Clone();

        clone.Hotkeys.PushToTalk = "F9";
        clone.ProviderProfiles[0].Name = "changed";

        Assert.NotEqual("F9", config.Hotkeys.PushToTalk);
        Assert.NotEqual("changed", config.ProviderProfiles[0].Name);
    }

    [Fact]
    public void GetActiveProfile_SkipsDisabledProfiles()
    {
        var config = new AppConfig
        {
            ActiveProviderProfileId = "disabled",
            ProviderProfiles = new List<ProviderProfile>
            {
                new() { Id = "disabled", Name = "禁用", TypeId = "mock", Enabled = false },
                new() { Id = "enabled", Name = "启用", TypeId = "mock", Enabled = true }
            }
        };
        config.Normalize();

        Assert.Equal("enabled", config.GetActiveProfile()!.Id);
    }
}
