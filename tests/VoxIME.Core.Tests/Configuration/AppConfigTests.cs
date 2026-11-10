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
    public void Normalize_MigratesLegacyHotkeyAndPreservesMultipleGestures()
    {
        var config = AppConfig.CreateDefault();
        config.Hotkeys.PushToTalk = "F2";
        config.Hotkeys.PushToTalkGestures.Clear();

        config.Normalize();

        Assert.Equal(new[] { "F2" }, config.Hotkeys.PushToTalkGestures);
        Assert.Equal("F2", config.Hotkeys.PushToTalk);

        config.Hotkeys.PushToTalkGestures = new List<string> { "F2", "Ctrl+Shift+Space" };
        config.Hotkeys.PushToTalk = "F2";
        config.Normalize();

        Assert.Equal(new[] { "F2", "Ctrl+Shift+Space" }, config.Hotkeys.PushToTalkGestures);
        Assert.Equal("F2", config.Hotkeys.PushToTalk);
    }

    [Fact]
    public void Normalize_EnablesHotkeyRequiredByRecordingMode()
    {
        var config = AppConfig.CreateDefault();
        config.Hotkeys.DefaultMode = RecordingMode.Toggle;
        config.Hotkeys.EnableToggle = false;
        config.Normalize();
        Assert.True(config.Hotkeys.EnableToggle);

        config = AppConfig.CreateDefault();
        config.Hotkeys.DefaultMode = RecordingMode.PushToTalk;
        config.Hotkeys.EnablePushToTalk = false;
        config.Normalize();
        Assert.True(config.Hotkeys.EnablePushToTalk);
    }

    [Fact]
    public void ExpressionMap_ResolvesPerModelWithGlobalFallback()
    {
        var config = AppConfig.CreateDefault();
        config.Live2D.ListeningExpression = "global-listening";
        config.Live2D.ExpressionByModel["E:\\models\\a"] = new Live2DExpressionMap
        {
            Listening = "a-listening",
            Thinking = "a-thinking"
        };

        Assert.Equal("a-listening", config.Live2D.ResolveExpressionMap("E:\\models\\a").Listening);
        Assert.Equal("global-listening", config.Live2D.ResolveExpressionMap("E:\\models\\b").Listening);
        Assert.Null(config.Live2D.ResolveExpressionMap(null).Speaking);
    }

    [Fact]
    public void MotionMap_ResolvesPerModelWithGlobalFallback()
    {
        var config = AppConfig.CreateDefault();
        config.Live2D.IdleMotionGroup = "Idle";
        config.Live2D.ListeningMotionGroups = new List<string> { "global-listening" };
        config.Live2D.MotionByModel["E:\\models\\a"] = new Live2DMotionMap
        {
            IdleGroup = "a-idle",
            Listening = new List<string> { "a-listening", "a-listening-2" },
            Error = new List<string> { "__none__" }
        };

        var a = config.Live2D.ResolveMotionMap("E:\\models\\a");
        Assert.Equal("a-idle", a.IdleGroup);
        Assert.Equal(new[] { "a-listening", "a-listening-2" }, a.Listening);
        Assert.Equal(new[] { "__none__" }, a.Error);

        var b = config.Live2D.ResolveMotionMap("E:\\models\\b");
        Assert.Equal("Idle", b.IdleGroup);
        Assert.Equal(new[] { "global-listening" }, b.Listening);
        Assert.Empty(b.Thinking);
    }

    [Fact]
    public void Normalize_ClampsOutOfRangeValues()
    {
        var config = new AppConfig
        {
            Audio = new AudioConfig { MaxDurationSeconds = 9999, SilenceTimeoutMs = 1, VadThreshold = 42 },
            Live2D = new Live2DConfig { Width = 10, Height = 9000, Opacity = 5, Scale = 0, MouthSensitivity = 100, EyeFollowFps = 999, ModelOffsetX = 5, ModelOffsetY = -9 },
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
        Assert.Equal(1.0, config.Live2D.ModelOffsetX);
        Assert.Equal(-1.0, config.Live2D.ModelOffsetY);
        Assert.Equal(5.0, config.Live2D.MouthSensitivity);
        Assert.Equal(300, config.Live2D.EyeFollowFps);
        Assert.Equal(10, config.Tts.Rate);
        Assert.Equal(0, config.Tts.Volume);
        Assert.Equal(10, config.Ui.HistoryLimit);
        Assert.Equal(1, config.Logging.RetainDays);
        Assert.Equal(64, config.Logging.MaxFileSizeKb);
    }

    [Fact]
    public void Normalize_TrimsModelLibraryDirectory_AndTreatsWhitespaceAsNull()
    {
        var config = new AppConfig
        {
            Live2D = new Live2DConfig { ModelLibraryDirectory = "  D:\\VoxIMEModels  " }
        };
        config.Normalize();
        Assert.Equal("D:\\VoxIMEModels", config.Live2D.ModelLibraryDirectory);

        var blank = new AppConfig { Live2D = new Live2DConfig { ModelLibraryDirectory = "   " } };
        blank.Normalize();
        Assert.Null(blank.Live2D.ModelLibraryDirectory);
    }

    [Fact]
    public void CloneAppConfig_PreservesInMemorySecrets()
    {
        var config = AppConfig.CreateDefault();
        config.ProviderProfiles[0].ApiKey = "sk-keep-me";
        config.ProviderProfiles[0].ApiSecret = "secret-keep-me";

        var clone = ConfigJson.CloneAppConfig(config);

        Assert.Equal("sk-keep-me", clone.ProviderProfiles[0].ApiKey);
        Assert.Equal("secret-keep-me", clone.ProviderProfiles[0].ApiSecret);
    }

    [Fact]
    public void Normalize_InitializesEyeFollowInvertMap()
    {
        var config = new AppConfig
        {
            Live2D = new Live2DConfig { EyeFollowInvertY = true, EyeFollowInvertYByModel = null! }
        };

        config.Normalize();

        Assert.NotNull(config.Live2D.EyeFollowInvertYByModel);
        Assert.Empty(config.Live2D.EyeFollowInvertYByModel);
        Assert.True(config.Live2D.EyeFollowInvertY);
    }

    [Fact]
    public void Normalize_ClampsEyeFollowFpsToValidRange()
    {
        var low = new AppConfig { Live2D = new Live2DConfig { EyeFollowFps = -3 } };
        low.Normalize();
        Assert.Equal(5, low.Live2D.EyeFollowFps);

        var high = new AppConfig { Live2D = new Live2DConfig { EyeFollowFps = 1000 } };
        high.Normalize();
        Assert.Equal(300, high.Live2D.EyeFollowFps);

        var normal = new AppConfig { Live2D = new Live2DConfig { EyeFollowFps = 45 } };
        normal.Normalize();
        Assert.Equal(45, normal.Live2D.EyeFollowFps);
    }

    [Fact]
    public void Normalize_MigratesLegacyVadThresholdAndKeepsCustomValue()
    {
        var legacy = new AppConfig { Audio = new AudioConfig { VadThreshold = 0.014 } };
        legacy.Normalize();
        Assert.Equal(0.003, legacy.Audio.VadThreshold);

        var custom = new AppConfig { Audio = new AudioConfig { VadThreshold = 0.02 } };
        custom.Normalize();
        Assert.Equal(0.02, custom.Audio.VadThreshold);
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
        // 新版配置直接明文保存 Key，便于导入导出。
        Assert.Contains("\"apiKey\":", json);
        Assert.DoesNotContain("apiKeyProtected", json);
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
    public void MotionLists_DeserializeLegacyStringValues()
    {
        const string json = """
        {
          "live2D": {
            "listeningMotionGroup": "TapBody",
            "thinkingMotionGroups": [ "Think", "Breath" ],
            "motionByModel": {
              "E:\\models\\a": { "idleGroup": "Idle", "speaking": "Speak" }
            }
          }
        }
        """;

        var config = JsonSerializer.Deserialize<AppConfig>(json, ConfigJson.Options);
        Assert.NotNull(config);
        Assert.Equal("TapBody", config!.Live2D.ListeningMotionGroup);
        Assert.Equal(new[] { "Think", "Breath" }, config.Live2D.ThinkingMotionGroups);
        Assert.Equal(new[] { "Speak" }, config.Live2D.MotionByModel["E:\\models\\a"].Speaking);
    }

    [Fact]
    public void MotionLists_NormalizeDedupeTrimAndCap()
    {
        var config = AppConfig.CreateDefault();
        config.Live2D.SpeakingMotionGroups = new List<string> { " A ", "a", "", "B", "C", "D", "E" };
        config.Normalize();

        Assert.Equal(new[] { "A", "B", "C", "D" }, config.Live2D.SpeakingMotionGroups);
        Assert.Equal("A", config.Live2D.SpeakingMotionGroup);
    }

    [Fact]
    public void MotionLists_NoneMarkerWins()
    {
        var config = AppConfig.CreateDefault();
        config.Live2D.ErrorMotionGroups = new List<string> { "Shock", "__none__", "Sad" };
        config.Normalize();

        Assert.Equal(new[] { "__none__" }, config.Live2D.ErrorMotionGroups);
        Assert.Equal("__none__", config.Live2D.ErrorMotionGroup);
    }

    [Fact]
    public void MotionLists_LegacySingleFieldBecomesList()
    {
        var config = AppConfig.CreateDefault();
        config.Live2D.ThinkingMotionGroup = "Think";
        config.Normalize();

        Assert.Equal(new[] { "Think" }, config.Live2D.ThinkingMotionGroups);
        Assert.Equal("Think", config.Live2D.ThinkingMotionGroup);
    }

    [Fact]
    public void MotionLists_SerializeAsArrayAndClonePreserves()
    {
        var config = AppConfig.CreateDefault();
        config.Live2D.ListeningMotionGroups = new List<string> { "Idle", "TapBody" };
        config.Normalize();

        var json = JsonSerializer.Serialize(config, ConfigJson.Options);
        Assert.Contains("\"listeningMotionGroups\": [", json);
        Assert.Contains("\"Idle\"", json);
        Assert.Contains("\"TapBody\"", json);

        var clone = config.Clone();
        Assert.Equal(new[] { "Idle", "TapBody" }, clone.Live2D.ListeningMotionGroups);
    }

    [Fact]
    public void ResolveMotionMap_FallsBackToGlobalListsAndLegacyField()
    {
        var config = AppConfig.CreateDefault();
        config.Live2D.ListeningMotionGroups = new List<string> { "GlobalA", "GlobalB" };
        config.Live2D.ThinkingMotionGroup = "LegacyThink";

        var resolved = config.Live2D.ResolveMotionMap("E:\\models\\none");

        Assert.Equal(new[] { "GlobalA", "GlobalB" }, resolved.Listening);
        Assert.Equal(new[] { "LegacyThink" }, resolved.Thinking);
        Assert.Empty(resolved.Speaking);
    }

    [Fact]
    public void Normalize_ClampsPerModelMotionMapLists()
    {
        var config = AppConfig.CreateDefault();
        config.Live2D.MotionByModel["E:\\models\\a"] = new Live2DMotionMap
        {
            IdleGroup = "  Idle  ",
            Listening = new List<string> { "A", "A", "B", "C", "D", "E" },
            Speaking = new List<string> { "" }
        };
        config.Normalize();

        var map = config.Live2D.MotionByModel["E:\\models\\a"];
        Assert.Equal("Idle", map.IdleGroup);
        Assert.Equal(new[] { "A", "B", "C", "D" }, map.Listening);
        Assert.Empty(map.Speaking);
    }

    [Fact]
    public void IdleMotionLists_DeserializeLegacySingleGroup()
    {
        const string json = """
        {
          "live2D": {
            "idleMotionGroup": "Animation/IDLE",
            "motionByModel": {
              "E:\\models\\a": { "idleGroup": "a-idle" }
            }
          }
        }
        """;

        var config = JsonSerializer.Deserialize<AppConfig>(json, ConfigJson.Options);
        Assert.NotNull(config);
        config!.Normalize();

        Assert.Equal(new[] { "Animation/IDLE" }, config.Live2D.IdleMotionGroups);
        Assert.Equal("Animation/IDLE", config.Live2D.IdleMotionGroup);
        Assert.Equal(new[] { "a-idle" }, config.Live2D.MotionByModel["E:\\models\\a"].IdleGroups);
        Assert.Equal("a-idle", config.Live2D.MotionByModel["E:\\models\\a"].IdleGroup);
    }

    [Fact]
    public void IdleMotionLists_NormalizeDedupeTrimAndCap()
    {
        var config = AppConfig.CreateDefault();
        config.Live2D.IdleMotionGroups = new List<string> { " A ", "a", "", "B", "C", "D", "E" };
        config.Normalize();

        Assert.Equal(new[] { "A", "B", "C", "D" }, config.Live2D.IdleMotionGroups);
        Assert.Equal("A", config.Live2D.IdleMotionGroup);
    }

    [Fact]
    public void IdleMotionLists_DefaultIdleIsAuto()
    {
        var config = AppConfig.CreateDefault();
        config.Normalize();

        Assert.Null(config.Live2D.IdleMotionGroups);
        Assert.Equal("Idle", config.Live2D.IdleMotionGroup);
    }

    [Fact]
    public void ResolveMotionMap_ReturnsIdleGroupsAndLegacyFirst()
    {
        var config = AppConfig.CreateDefault();
        config.Live2D.IdleMotionGroups = new List<string> { "Animation/IDLE", "Animation/Swim" };

        var resolved = config.Live2D.ResolveMotionMap(null);
        Assert.Equal(new[] { "Animation/IDLE", "Animation/Swim" }, resolved.IdleGroups);
        Assert.Equal("Animation/IDLE", resolved.IdleGroup);

        var defaultResolved = AppConfig.CreateDefault().Live2D.ResolveMotionMap(null);
        Assert.Empty(defaultResolved.IdleGroups);
        Assert.Equal("Idle", defaultResolved.IdleGroup);
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
