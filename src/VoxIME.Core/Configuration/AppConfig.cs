using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoxIME.Core.Configuration;

/// <summary>配置序列化统一选项（camelCase、字符串枚举、可读缩进）。</summary>
public static class ConfigJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>单行序列化（JSONL 历史记录等场景）。</summary>
    public static readonly JsonSerializerOptions CompactOptions = new(Options)
    {
        WriteIndented = false
    };

    public static T Clone<T>(T value) where T : class
    {
        var json = JsonSerializer.Serialize(value, Options);
        return JsonSerializer.Deserialize<T>(json, Options)
               ?? throw new InvalidOperationException($"无法克隆类型 {typeof(T).Name}");
    }
}

public enum InsertionMode
{
    /// <summary>写入剪贴板后发送 Ctrl+V（兼容性最好）。</summary>
    ClipboardPaste,

    /// <summary>逐字 SendInput 模拟输入（不污染剪贴板，速度较慢）。</summary>
    SimulateTyping
}

public enum RecordingMode
{
    /// <summary>按住热键说话。</summary>
    PushToTalk,

    /// <summary>按一下开始、再按一下结束。</summary>
    Toggle
}

public sealed class TextReplacement
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

/// <summary>整套应用配置，序列化到 %APPDATA%\VoxIME\config.json。</summary>
public sealed class AppConfig
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string ActiveProviderProfileId { get; set; } = "";
    public List<ProviderProfile> ProviderProfiles { get; set; } = new();
    public HotkeyConfig Hotkeys { get; set; } = new();
    public AudioConfig Audio { get; set; } = new();
    public InsertionConfig Insertion { get; set; } = new();
    public Live2DConfig Live2D { get; set; } = new();
    public TtsConfig Tts { get; set; } = new();
    public UiConfig Ui { get; set; } = new();
    public OverlayConfig Overlay { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();

    public static AppConfig CreateDefault() => new()
    {
        ProviderProfiles =
        {
            new ProviderProfile
            {
                Id = "mock",
                Name = "Mock 离线演示",
                TypeId = Abstractions.AsrProviderTypeIds.Mock,
                Model = "mock",
                Language = "zh",
                Enabled = true
            }
        },
        ActiveProviderProfileId = "mock"
    };

    public ProviderProfile? GetActiveProfile()
    {
        var profile = ProviderProfiles.FirstOrDefault(p => p.Id == ActiveProviderProfileId && p.Enabled);
        return profile ?? ProviderProfiles.FirstOrDefault(p => p.Enabled);
    }

    /// <summary>深拷贝（设置窗口编辑时避免直接改到运行中的配置）。</summary>
    public AppConfig Clone() => ConfigJson.Clone(this);

    /// <summary>修正非法/缺失字段，保证配置可直接使用（幂等）。</summary>
    public void Normalize()
    {
        Version = CurrentVersion;
        ProviderProfiles ??= new List<ProviderProfile>();
        if (ProviderProfiles.Count == 0)
        {
            ProviderProfiles.Add(new ProviderProfile
            {
                Id = "mock",
                Name = "Mock 离线演示",
                TypeId = Abstractions.AsrProviderTypeIds.Mock,
                Model = "mock",
                Enabled = true
            });
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in ProviderProfiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Id) || !ids.Add(profile.Id))
            {
                profile.Id = Guid.NewGuid().ToString("N")[..8];
                ids.Add(profile.Id);
            }

            profile.Name = string.IsNullOrWhiteSpace(profile.Name) ? "未命名服务" : profile.Name.Trim();
            profile.TypeId = profile.TypeId?.Trim() ?? "";
            profile.BaseUrl = profile.BaseUrl?.Trim() ?? "";
            profile.Model = profile.Model?.Trim() ?? "";
            profile.Language = string.IsNullOrWhiteSpace(profile.Language) ? "zh" : profile.Language.Trim();
            profile.TimeoutSeconds = Math.Clamp(profile.TimeoutSeconds, 5, 600);
            profile.Command = profile.Command?.Trim();
            profile.CommandArguments = profile.CommandArguments?.Trim();
            profile.OutputFileTemplate = profile.OutputFileTemplate?.Trim();
            profile.OutputMode = string.IsNullOrWhiteSpace(profile.OutputMode) ? "stdout" : profile.OutputMode.Trim();
        }

        if (!ProviderProfiles.Any(p => p.Id == ActiveProviderProfileId))
        {
            ActiveProviderProfileId = (ProviderProfiles.FirstOrDefault(p => p.Enabled) ?? ProviderProfiles[0]).Id;
        }

        Hotkeys ??= new HotkeyConfig();
        Hotkeys.PushToTalk = NormalizeGesture(Hotkeys.PushToTalk, AppInfo.DefaultHotkeyPushToTalk);
        Hotkeys.Toggle = NormalizeGesture(Hotkeys.Toggle, AppInfo.DefaultHotkeyToggle);
        Hotkeys.Cancel = NormalizeGesture(Hotkeys.Cancel, "Esc");

        Audio ??= new AudioConfig();
        Audio.SampleRate = 16000;
        Audio.MaxDurationSeconds = Math.Clamp(Audio.MaxDurationSeconds, 5, 600);
        Audio.SilenceTimeoutMs = Math.Clamp(Audio.SilenceTimeoutMs, 200, 5000);
        Audio.MinSpeechMs = Math.Clamp(Audio.MinSpeechMs, 100, 3000);
        Audio.VadThreshold = Math.Clamp(Audio.VadThreshold, 0.001, 0.2);

        Insertion ??= new InsertionConfig();
        Insertion.PasteDelayMs = Math.Clamp(Insertion.PasteDelayMs, 0, 2000);
        Insertion.Replacements ??= new List<TextReplacement>();
        Insertion.Replacements = Insertion.Replacements
            .Where(r => !string.IsNullOrEmpty(r.From))
            .GroupBy(r => r.From, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        Live2D ??= new Live2DConfig();
        Live2D.Width = Math.Clamp(Live2D.Width, 160, 1600);
        Live2D.Height = Math.Clamp(Live2D.Height, 160, 1600);
        Live2D.Opacity = Math.Clamp(Live2D.Opacity, 0.2, 1.0);
        Live2D.Scale = Math.Clamp(Live2D.Scale, 0.2, 3.0);
        Live2D.MouthSensitivity = Math.Clamp(Live2D.MouthSensitivity, 0.1, 5.0);
        Live2D.EyeFollowFps = Math.Clamp(Live2D.EyeFollowFps, 5, 60);
        Live2D.IdleMotionGroup = string.IsNullOrWhiteSpace(Live2D.IdleMotionGroup) ? "Idle" : Live2D.IdleMotionGroup.Trim();

        Tts ??= new TtsConfig();
        Tts.Rate = Math.Clamp(Tts.Rate, -10, 10);
        Tts.Volume = Math.Clamp(Tts.Volume, 0, 100);

        Ui ??= new UiConfig();
        Ui.HistoryLimit = Math.Clamp(Ui.HistoryLimit, 10, 2000);

        Overlay ??= new OverlayConfig();
        Overlay.AutoHideMs = Math.Clamp(Overlay.AutoHideMs, 500, 30000);

        Logging ??= new LoggingConfig();
        Logging.RetainDays = Math.Clamp(Logging.RetainDays, 1, 60);
        Logging.MaxFileSizeKb = Math.Clamp(Logging.MaxFileSizeKb, 64, 10240);
    }

    private static string NormalizeGesture(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}

public sealed class ProviderProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string TypeId { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
    public string Language { get; set; } = "zh";
    public string? Prompt { get; set; }
    /// <summary>厂商特定请求参数（JSON 对象，会合入请求体）。</summary>
    public string? ExtraJson { get; set; }
    public int TimeoutSeconds { get; set; } = 60;
    public bool Enabled { get; set; } = true;

    // ---- 本地命令行适配器 ----
    public string? Command { get; set; }
    /// <summary>参数模板，支持 {wav} {out} {lang} 占位符。</summary>
    public string? CommandArguments { get; set; }
    /// <summary>stdout | file | json。</summary>
    public string OutputMode { get; set; } = "stdout";
    public string? OutputFileTemplate { get; set; }

    // ---- 厂商身份（非敏感） ----
    public string? AppId { get; set; }

    // ---- 敏感凭据（内存明文，落盘 DPAPI 加密） ----
    [JsonIgnore] public string ApiKey { get; set; } = "";
    [JsonIgnore] public string ApiSecret { get; set; } = "";
    [JsonIgnore] public string SecretId { get; set; } = "";

    public string? ApiKeyProtected { get; set; }
    public string? ApiSecretProtected { get; set; }
    public string? SecretIdProtected { get; set; }

    [JsonIgnore]
    public bool HasAnySecret =>
        !string.IsNullOrEmpty(ApiKey) || !string.IsNullOrEmpty(ApiSecret) || !string.IsNullOrEmpty(SecretId);

    public ProviderProfile Clone() => ConfigJson.Clone(this);
}

public sealed class HotkeyConfig
{
    /// <summary>按住说话的热键组合，如 "Ctrl+Alt"、"F2"、"Ctrl+Shift+Space"、"MouseX1"（鼠标侧键）。</summary>
    public string PushToTalk { get; set; } = AppInfo.DefaultHotkeyPushToTalk;
    /// <summary>切换录音的热键，如 "Ctrl+Alt+M"、"MouseX2"（鼠标侧键）。</summary>
    public string Toggle { get; set; } = AppInfo.DefaultHotkeyToggle;
    public string Cancel { get; set; } = "Esc";
    public bool EnablePushToTalk { get; set; } = true;
    public bool EnableToggle { get; set; } = true;
    public RecordingMode DefaultMode { get; set; } = RecordingMode.PushToTalk;
}

public sealed class AudioConfig
{
    public string? DeviceId { get; set; }
    public int SampleRate { get; set; } = 16000;
    public int MaxDurationSeconds { get; set; } = 120;
    public bool AutoStopOnSilence { get; set; } = true;
    public int SilenceTimeoutMs { get; set; } = 1800;
    public int MinSpeechMs { get; set; } = 300;
    public double VadThreshold { get; set; } = 0.014;
    public bool PlayCues { get; set; } = true;
}

public sealed class InsertionConfig
{
    public InsertionMode Mode { get; set; } = InsertionMode.ClipboardPaste;
    public bool AutoInsert { get; set; } = true;
    /// <summary>先显示识别结果供确认/编辑，再手动插入。</summary>
    public bool PreviewBeforeInsert { get; set; }
    public bool RestoreClipboard { get; set; } = true;
    public int PasteDelayMs { get; set; } = 200;
    public bool TrimWhitespace { get; set; } = true;
    public bool AppendSpace { get; set; }
    public bool PressEnterAfterInsert { get; set; }
    public List<TextReplacement> Replacements { get; set; } = new();
}

public sealed class Live2DConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>模型目录（包含 .model3.json 的文件夹）；为空时使用内置兜底形象。</summary>
    public string? ModelDirectory { get; set; }
    public double Width { get; set; } = 380;
    public double Height { get; set; } = 560;
    /// <summary>-1 表示自动吸附屏幕右下角。</summary>
    public double Left { get; set; } = -1;
    public double Top { get; set; } = -1;
    public double Opacity { get; set; } = 0.96;
    public double Scale { get; set; } = 1.0;
    public bool ClickThrough { get; set; } = true;
    public bool AlwaysOnTop { get; set; } = true;
    public bool EyeFollow { get; set; } = true;
    /// <summary>眼动跟随的轮询帧率（Hz，5–60，默认 30）；越高越跟手，略增 CPU/GPU 占用。</summary>
    public int EyeFollowFps { get; set; } = 30;
    public bool IdleMotion { get; set; } = true;
    public bool MouthSync { get; set; } = true;
    public double MouthSensitivity { get; set; } = 1.0;
    public bool ShowBubble { get; set; } = true;
    public string IdleMotionGroup { get; set; } = "Idle";
    public string? ListeningExpression { get; set; }
    public string? ThinkingExpression { get; set; }
    public string? SpeakingExpression { get; set; }
    public string? ErrorExpression { get; set; }
    public string FallbackAvatarTheme { get; set; } = "violet";
}

public sealed class TtsConfig
{
    public bool Enabled { get; set; }
    public bool AutoSpeakResult { get; set; } = true;
    public string? VoiceName { get; set; }
    public int Rate { get; set; }
    public int Volume { get; set; } = 90;
    public bool MouthSync { get; set; } = true;
}

public sealed class UiConfig
{
    public string Language { get; set; } = "zh-CN";
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool ShowTrayIcon { get; set; } = true;
    public bool ShowHistory { get; set; } = true;
    public int HistoryLimit { get; set; } = 100;
    public string Theme { get; set; } = "dark";
    public string AccentColor { get; set; } = "#7C5CFF";
    public bool ShowWelcomeOnStartup { get; set; } = true;
}

public sealed class OverlayConfig
{
    public bool ShowHud { get; set; } = true;
    public bool ShowLiveTranscript { get; set; } = true;
    public bool AutoInsert { get; set; } = true;
    public double Left { get; set; } = -1;
    public double Top { get; set; } = -1;
    public int AutoHideMs { get; set; } = 4000;
}

public sealed class LoggingConfig
{
    public string Level { get; set; } = "info";
    public string? Directory { get; set; }
    public int MaxFileSizeKb { get; set; } = 2048;
    public int RetainDays { get; set; } = 14;
}
