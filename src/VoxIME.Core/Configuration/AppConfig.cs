using System.Text.Json;
using System.Text.Json.Serialization;
using VoxIME.Core.Appearance;

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

    /// <summary>
    /// 克隆 AppConfig，并保留各 ProviderProfile 的明文凭据。
    /// 配置现在直接保存明文 ApiKey/ApiSecret/SecretId，便于导入导出；
    /// *Protected 字段仅用于读取旧版 DPAPI 配置并在加载时迁移。
    /// </summary>
    public static AppConfig CloneAppConfig(AppConfig source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var clone = Clone(source);
        var originById = source.ProviderProfiles.ToDictionary(p => p.Id, StringComparer.Ordinal);
        foreach (var target in clone.ProviderProfiles)
        {
            if (!originById.TryGetValue(target.Id, out var origin))
            {
                continue;
            }

            target.ApiKey = origin.ApiKey;
            target.ApiSecret = origin.ApiSecret;
            target.SecretId = origin.SecretId;
            target.ApiKeyProtected = origin.ApiKeyProtected;
            target.ApiSecretProtected = origin.ApiSecretProtected;
            target.SecretIdProtected = origin.SecretIdProtected;
        }

        return clone;
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

/// <summary>识别时附带的前台应用上下文级别。</summary>
public enum AppContextMode
{
    /// <summary>不读取、不发送应用上下文。</summary>
    Off,

    /// <summary>只发送应用分类和进程名。</summary>
    AppOnly,

    /// <summary>发送应用分类、进程名和窗口标题（标题会发送给 ASR 服务）。</summary>
    AppAndWindowTitle
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
    public AppConfig Clone() => ConfigJson.CloneAppConfig(this);

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
        Hotkeys.PushToTalkGestures = NormalizeGestureList(
            Hotkeys.PushToTalkGestures, Hotkeys.PushToTalk, AppInfo.DefaultHotkeyPushToTalk);
        Hotkeys.ToggleGestures = NormalizeGestureList(
            Hotkeys.ToggleGestures, Hotkeys.Toggle, AppInfo.DefaultHotkeyToggle);
        Hotkeys.CancelGestures = NormalizeGestureList(Hotkeys.CancelGestures, Hotkeys.Cancel, "Esc");
        // 旧字段保留为“第一个热键”，兼容旧版本读取。
        Hotkeys.PushToTalk = Hotkeys.PushToTalkGestures[0];
        Hotkeys.Toggle = Hotkeys.ToggleGestures[0];
        Hotkeys.Cancel = Hotkeys.CancelGestures[0];

        // “按一下开始/结束”模式依赖切换热键；如果旧配置里禁用着切换热键，
        // 会出现“模式选对了但热键完全没反应”，这里按默认模式自动启用对应热键。
        if (Hotkeys.DefaultMode == RecordingMode.Toggle)
        {
            Hotkeys.EnableToggle = true;
        }
        else
        {
            Hotkeys.EnablePushToTalk = true;
        }

        Audio ??= new AudioConfig();
        Audio.SampleRate = 16000;
        if (!Enum.IsDefined(Audio.AppContextMode))
        {
            Audio.AppContextMode = AppContextMode.Off;
        }

        Audio.MaxDurationSeconds = Math.Clamp(Audio.MaxDurationSeconds, 5, 600);
        Audio.SilenceTimeoutMs = Math.Clamp(Audio.SilenceTimeoutMs, 200, 5000);
        Audio.MinSpeechMs = Math.Clamp(Audio.MinSpeechMs, 100, 3000);
        Audio.VadThreshold = Math.Clamp(Audio.VadThreshold, 0.001, 0.2);
        // 旧版本默认 0.014 对笔记本内置麦克风偏高；仅在仍是旧默认值时迁移到新的 0.003，
        // 用户手动调过的其它值保持不变。
        if (Math.Abs(Audio.VadThreshold - 0.014) < 1e-9)
        {
            Audio.VadThreshold = 0.003;
        }

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
        Live2D.ModelOffsetX = Math.Clamp(Live2D.ModelOffsetX, -1.0, 1.0);
        Live2D.ModelOffsetY = Math.Clamp(Live2D.ModelOffsetY, -1.0, 1.0);
        Live2D.MouthSensitivity = Math.Clamp(Live2D.MouthSensitivity, 0.1, 5.0);
        Live2D.EyeFollowFps = Math.Clamp(Live2D.EyeFollowFps, 5, 300);
        Live2D.EyeFollowInvertYByModel ??= new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        Live2D.ExpressionByModel ??= new Dictionary<string, Live2DExpressionMap>(StringComparer.OrdinalIgnoreCase);
        Live2D.MotionByModel ??= new Dictionary<string, Live2DMotionMap>(StringComparer.OrdinalIgnoreCase);
        Live2D.ModelLibraryDirectory = string.IsNullOrWhiteSpace(Live2D.ModelLibraryDirectory)
            ? null
            : Live2D.ModelLibraryDirectory.Trim();
        // 待机动作组也支持多选：默认值 "Idle" 不种入列表（空列表 = 自动找名字含 idle 的组，
        // 兼容模型没有 Idle 组的情况），旧字段回写 "Idle" 供旧版本读取。
        Live2D.IdleMotionGroups = NormalizeStoredMotionGroups(
            Live2D.IdleMotionGroups,
            Live2DConfig.IsDefaultIdleGroup(Live2D.IdleMotionGroup) ? null : Live2D.IdleMotionGroup);
        Live2D.IdleMotionGroup = Live2D.IdleMotionGroups?.FirstOrDefault() ?? "Idle";

        // 状态动作映射从单选升级为多选：新列表字段优先，旧单数字段作为兼容来源；
        // 规范化后回写旧字段的第一项，旧版本仍能读到“一个动作组”。
        Live2D.ListeningMotionGroups = NormalizeStoredMotionGroups(Live2D.ListeningMotionGroups, Live2D.ListeningMotionGroup);
        Live2D.ListeningMotionGroup = Live2D.ListeningMotionGroups?.FirstOrDefault();
        Live2D.ThinkingMotionGroups = NormalizeStoredMotionGroups(Live2D.ThinkingMotionGroups, Live2D.ThinkingMotionGroup);
        Live2D.ThinkingMotionGroup = Live2D.ThinkingMotionGroups?.FirstOrDefault();
        Live2D.SpeakingMotionGroups = NormalizeStoredMotionGroups(Live2D.SpeakingMotionGroups, Live2D.SpeakingMotionGroup);
        Live2D.SpeakingMotionGroup = Live2D.SpeakingMotionGroups?.FirstOrDefault();
        Live2D.ErrorMotionGroups = NormalizeStoredMotionGroups(Live2D.ErrorMotionGroups, Live2D.ErrorMotionGroup);
        Live2D.ErrorMotionGroup = Live2D.ErrorMotionGroups?.FirstOrDefault();

        foreach (var map in Live2D.MotionByModel.Values)
        {
            if (map is null)
            {
                continue;
            }

            map.IdleGroups = Live2DConfig.NormalizeMotionGroups(
                map.IdleGroups,
                Live2DConfig.IsDefaultIdleGroup(map.IdleGroup) ? null : map.IdleGroup);
            map.IdleGroup = map.IdleGroups.FirstOrDefault() ?? "Idle";
            map.Listening = Live2DConfig.NormalizeMotionGroups(map.Listening);
            map.Thinking = Live2DConfig.NormalizeMotionGroups(map.Thinking);
            map.Speaking = Live2DConfig.NormalizeMotionGroups(map.Speaking);
            map.Error = Live2DConfig.NormalizeMotionGroups(map.Error);
        }

        Tts ??= new TtsConfig();
        Tts.Rate = Math.Clamp(Tts.Rate, -10, 10);
        Tts.Volume = Math.Clamp(Tts.Volume, 0, 100);

        Ui ??= new UiConfig();
        Ui.HistoryLimit = Math.Clamp(Ui.HistoryLimit, 10, 2000);
        Ui.Theme = ThemeModes.Normalize(Ui.Theme);
        Ui.ColorScheme = ThemeCatalog.NormalizeId(Ui.ColorScheme);
        Ui.Motion = MotionModes.Normalize(Ui.Motion);
        Ui.Background ??= new BackgroundConfig();
        Ui.Background.Normalize();
        Ui.HudBackground ??= new BackgroundConfig();
        Ui.HudBackground.Normalize();
        Ui.HudScale = Math.Clamp(Ui.HudScale, 0.7, 1.5);
        Ui.HudCornerRadius = Math.Clamp(Ui.HudCornerRadius, 0, 28);
        Ui.HudCardOpacity = Math.Clamp(Ui.HudCardOpacity, 0.35, 1.0);
        Ui.HudBottomMargin = Math.Clamp(Ui.HudBottomMargin, 0, 300);
        Ui.HudHorizontalOffset = Math.Clamp(Ui.HudHorizontalOffset, -400, 400);

        Overlay ??= new OverlayConfig();
        Overlay.AutoHideMs = Math.Clamp(Overlay.AutoHideMs, 500, 30000);

        Logging ??= new LoggingConfig();
        Logging.RetainDays = Math.Clamp(Logging.RetainDays, 1, 60);
        Logging.MaxFileSizeKb = Math.Clamp(Logging.MaxFileSizeKb, 64, 10240);
    }

    private static string NormalizeGesture(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static List<string> NormalizeGestureList(List<string>? values, string legacy, string fallback)
    {
        var result = new List<string>();
        if (values is not null)
        {
            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var trimmed = value.Trim();
                if (!result.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(trimmed);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(legacy))
        {
            var legacyTrimmed = legacy.Trim();
            if (result.Count == 0)
            {
                result.Add(legacyTrimmed);
            }
            else if (!string.Equals(result[0], legacyTrimmed, StringComparison.OrdinalIgnoreCase))
            {
                // 兼容旧代码/旧测试只改 PushToTalk 等单值字段的情况：单值字段视为新的第一热键。
                result.RemoveAll(value => string.Equals(value, legacyTrimmed, StringComparison.OrdinalIgnoreCase));
                result.Insert(0, legacyTrimmed);
            }
        }

        if (result.Count == 0)
        {
            result.Add(string.IsNullOrWhiteSpace(legacy) ? fallback : legacy.Trim());
        }

        return result.Take(8).ToList();
    }

    /// <summary>规范化并落盘用的动作组列表：空列表写成 null，避免配置文件里出现空数组。</summary>
    private static List<string>? NormalizeStoredMotionGroups(List<string>? values, string? legacy = null)
    {
        var normalized = Live2DConfig.NormalizeMotionGroups(values, legacy);
        return normalized.Count == 0 ? null : normalized;
    }
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

    // ---- 敏感凭据（明文保存，便于导入导出；请勿分享 config.json） ----
    public string ApiKey { get; set; } = "";
    public string ApiSecret { get; set; } = "";
    public string SecretId { get; set; } = "";

    /// <summary>仅兼容旧版 DPAPI 配置；加载时迁移为明文并清空。</summary>
    public string? ApiKeyProtected { get; set; }
    /// <summary>仅兼容旧版 DPAPI 配置；加载时迁移为明文并清空。</summary>
    public string? ApiSecretProtected { get; set; }
    /// <summary>仅兼容旧版 DPAPI 配置；加载时迁移为明文并清空。</summary>
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

    /// <summary>按住说话可配置多个热键；旧配置（只有 <see cref="PushToTalk"/>）会自动迁移到这里。</summary>
    public List<string> PushToTalkGestures { get; set; } = new();

    /// <summary>切换录音可配置多个热键。</summary>
    public List<string> ToggleGestures { get; set; } = new();

    /// <summary>取消录音可配置多个热键。</summary>
    public List<string> CancelGestures { get; set; } = new();

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
    /// <summary>
    /// 语音检测绝对阈值（RMS，0.001–0.2）。默认 0.003：笔记本内置麦克风/远场输入通常较安静，
    /// 旧默认 0.014 容易把正常说话判成"没有检测到语音"。
    /// </summary>
    public double VadThreshold { get; set; } = 0.003;
    public bool PlayCues { get; set; } = true;

    /// <summary>识别请求附带的前台应用上下文级别；默认关闭。</summary>
    public AppContextMode AppContextMode { get; set; } = AppContextMode.Off;
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
    /// <summary>模型库根目录（「导入文件夹/ZIP」复制的目标位置）；null/空 → %APPDATA%\VoxIME\models。</summary>
    public string? ModelLibraryDirectory { get; set; }
    public double Width { get; set; } = 380;
    public double Height { get; set; } = 560;
    /// <summary>切换模型时按模型原始宽高比自动调整窗口尺寸（以当前高度为基准）。</summary>
    public bool AutoFitWindow { get; set; } = true;
    /// <summary>启动/切换模型时自动吸附到屏幕右下角；关闭后使用拖动保存的位置或屏幕居中。</summary>
    public bool SnapToEdge { get; set; } = true;
    /// <summary>贴边时窗口下边缘保持在任务栏上方的工作区内。</summary>
    public bool KeepAboveTaskbar { get; set; } = true;
    /// <summary>模型在窗口内的水平偏移（-1..1，按窗口宽度比例；正=向右）。</summary>
    public double ModelOffsetX { get; set; }
    /// <summary>模型在窗口内的垂直偏移（-1..1，按窗口高度比例；正=向下）。</summary>
    public double ModelOffsetY { get; set; }
    /// <summary>-1 表示自动吸附屏幕右下角。</summary>
    public double Left { get; set; } = -1;
    public double Top { get; set; } = -1;
    public double Opacity { get; set; } = 0.96;
    public double Scale { get; set; } = 1.0;
    public bool ClickThrough { get; set; } = true;
    public bool AlwaysOnTop { get; set; } = true;
    public bool EyeFollow { get; set; } = true;
    /// <summary>眼动跟随的采样帧率（Hz，5–300，默认 30）；采样在后台线程完成，实际渲染上限为显示器刷新率。</summary>
    public int EyeFollowFps { get; set; } = 30;
    /// <summary>眼动上下反转（当前生效值，保存时由 <see cref="EyeFollowInvertYByModel"/> 按当前模型解析）。</summary>
    public bool EyeFollowInvertY { get; set; }
    /// <summary>按模型目录记住的“眼动上下反转”偏好（不同模型的骨骼绑定可能相反）。</summary>
    public Dictionary<string, bool> EyeFollowInvertYByModel { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IdleMotion { get; set; } = true;
    public bool MouthSync { get; set; } = true;
    public double MouthSensitivity { get; set; } = 1.0;
    public bool ShowBubble { get; set; } = true;
    /// <summary>旧字段：待机动作组（单个）；仅为兼容旧配置保留，保存时同步为 <see cref="IdleMotionGroups"/> 的第一项。</summary>
    public string IdleMotionGroup { get; set; } = "Idle";

    /// <summary>待机动作组（可多选，各自循环叠加播放；空=自动找名字含 idle 的组）。</summary>
    [JsonConverter(typeof(StringListJsonConverter))]
    public List<string>? IdleMotionGroups { get; set; }
    public string? ListeningExpression { get; set; }
    public string? ThinkingExpression { get; set; }
    public string? SpeakingExpression { get; set; }
    public string? ErrorExpression { get; set; }

    /// <summary>聆听状态对应的动作组（可多选，切换状态时同时叠加播放；空=自动同名匹配，<see cref="Live2DMotionMap.NoneMarker"/>=不播放）。</summary>
    [JsonConverter(typeof(StringListJsonConverter))]
    public List<string>? ListeningMotionGroups { get; set; }

    /// <summary>识别中状态对应的动作组（可多选，规则同 <see cref="ListeningMotionGroups"/>）。</summary>
    [JsonConverter(typeof(StringListJsonConverter))]
    public List<string>? ThinkingMotionGroups { get; set; }

    /// <summary>朗读状态对应的动作组（可多选，规则同 <see cref="ListeningMotionGroups"/>）。</summary>
    [JsonConverter(typeof(StringListJsonConverter))]
    public List<string>? SpeakingMotionGroups { get; set; }

    /// <summary>出错状态对应的动作组（可多选，规则同 <see cref="ListeningMotionGroups"/>）。</summary>
    [JsonConverter(typeof(StringListJsonConverter))]
    public List<string>? ErrorMotionGroups { get; set; }

    /// <summary>旧字段：聆听状态的单个动作组；仅为兼容旧配置保留，保存时同步为 <see cref="ListeningMotionGroups"/> 的第一项。</summary>
    public string? ListeningMotionGroup { get; set; }

    /// <summary>旧字段：识别中状态的单个动作组；仅为兼容旧配置保留。</summary>
    public string? ThinkingMotionGroup { get; set; }

    /// <summary>旧字段：朗读状态的单个动作组；仅为兼容旧配置保留。</summary>
    public string? SpeakingMotionGroup { get; set; }

    /// <summary>旧字段：出错状态的单个动作组；仅为兼容旧配置保留。</summary>
    public string? ErrorMotionGroup { get; set; }

    /// <summary>按模型目录记住的状态动作映射；旧配置没有此字段时回退到全局字段。</summary>
    public Dictionary<string, Live2DMotionMap> MotionByModel { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>按模型目录记住的状态表情映射；旧配置没有此字段时回退到上面四个全局字段。</summary>
    public Dictionary<string, Live2DExpressionMap> ExpressionByModel { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>取指定模型的表情映射；没有按模型记录时返回全局字段。</summary>
    public Live2DExpressionMap ResolveExpressionMap(string? modelDirectory)
    {
        var key = (modelDirectory ?? "").Trim();
        if (key.Length > 0 && ExpressionByModel.TryGetValue(key, out var map) && map is not null)
        {
            return map;
        }

        return new Live2DExpressionMap
        {
            Listening = ListeningExpression,
            Thinking = ThinkingExpression,
            Speaking = SpeakingExpression,
            Error = ErrorExpression
        };
    }

    /// <summary>取指定模型的动作映射；没有按模型记录时返回全局字段。</summary>
    public Live2DMotionMap ResolveMotionMap(string? modelDirectory)
    {
        var key = (modelDirectory ?? "").Trim();
        if (key.Length > 0 && MotionByModel.TryGetValue(key, out var map) && map is not null)
        {
            var idleGroups = NormalizeMotionGroups(
                map.IdleGroups,
                IsDefaultIdleGroup(map.IdleGroup) ? null : map.IdleGroup);
            return new Live2DMotionMap
            {
                IdleGroups = idleGroups,
                IdleGroup = idleGroups.FirstOrDefault() ?? "Idle",
                Listening = NormalizeMotionGroups(map.Listening),
                Thinking = NormalizeMotionGroups(map.Thinking),
                Speaking = NormalizeMotionGroups(map.Speaking),
                Error = NormalizeMotionGroups(map.Error)
            };
        }

        var globalIdleGroups = NormalizeMotionGroups(
            IdleMotionGroups,
            IsDefaultIdleGroup(IdleMotionGroup) ? null : IdleMotionGroup);
        return new Live2DMotionMap
        {
            IdleGroups = globalIdleGroups,
            IdleGroup = globalIdleGroups.FirstOrDefault() ?? "Idle",
            Listening = NormalizeMotionGroups(ListeningMotionGroups, ListeningMotionGroup),
            Thinking = NormalizeMotionGroups(ThinkingMotionGroups, ThinkingMotionGroup),
            Speaking = NormalizeMotionGroups(SpeakingMotionGroups, SpeakingMotionGroup),
            Error = NormalizeMotionGroups(ErrorMotionGroups, ErrorMotionGroup)
        };
    }

    /// <summary>默认值 / 空值 = “自动”，不种入多选列表（模型可能没有名为 Idle 的组）。</summary>
    public static bool IsDefaultIdleGroup(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || string.Equals(value.Trim(), "Idle", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 规范化一个状态的动作组列表：合并旧单数字段、trim、去空、忽略大小写去重、最多 <see cref="Live2DMotionMap.MaxGroups"/> 个；
    /// 一旦出现 <see cref="Live2DMotionMap.NoneMarker"/> 就只保留“不播放”。返回空列表表示“自动同名匹配”。
    /// </summary>
    public static List<string> NormalizeMotionGroups(List<string>? values, string? legacy = null)
    {
        var candidates = new List<string>();
        if (values is not null)
        {
            candidates.AddRange(values);
        }

        if (candidates.Count == 0 && !string.IsNullOrWhiteSpace(legacy))
        {
            candidates.Add(legacy);
        }

        var result = new List<string>(Math.Min(candidates.Count, Live2DMotionMap.MaxGroups));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var trimmed = candidate.Trim();
            if (string.Equals(trimmed, Live2DMotionMap.NoneMarker, StringComparison.Ordinal))
            {
                return new List<string> { Live2DMotionMap.NoneMarker };
            }

            if (result.Count >= Live2DMotionMap.MaxGroups)
            {
                break;
            }

            if (!seen.Add(trimmed))
            {
                continue;
            }

            result.Add(trimmed);
        }

        return result;
    }

    public string FallbackAvatarTheme { get; set; } = "violet";
}

/// <summary>一个模型对应的待机与四个状态动作组（每个状态可同时叠加多个动作组）。</summary>
public sealed class Live2DMotionMap
{
    /// <summary>每个状态最多同时叠加的动作组数量；超出部分在规范化时截断。</summary>
    public const int MaxGroups = 4;

    /// <summary>表示“该状态不播放动作”的保留值。</summary>
    public const string NoneMarker = "__none__";

    /// <summary>旧字段：待机动作组（单个）；仅为兼容旧配置保留。</summary>
    public string? IdleGroup { get; set; }

    /// <summary>待机动作组（可多选，各自循环叠加播放；空=自动找名字含 idle 的组）。</summary>
    [JsonConverter(typeof(StringListJsonConverter))]
    public List<string> IdleGroups { get; set; } = new();

    /// <summary>聆听状态叠加的动作组；空=自动同名匹配，<see cref="NoneMarker"/>=不播放。</summary>
    [JsonConverter(typeof(StringListJsonConverter))]
    public List<string> Listening { get; set; } = new();

    /// <summary>识别中状态叠加的动作组；空=自动同名匹配。</summary>
    [JsonConverter(typeof(StringListJsonConverter))]
    public List<string> Thinking { get; set; } = new();

    /// <summary>朗读状态叠加的动作组；空=自动同名匹配。</summary>
    [JsonConverter(typeof(StringListJsonConverter))]
    public List<string> Speaking { get; set; } = new();

    /// <summary>出错状态叠加的动作组；空=自动同名匹配。</summary>
    [JsonConverter(typeof(StringListJsonConverter))]
    public List<string> Error { get; set; } = new();
}

/// <summary>一个模型对应的四个状态表情名。</summary>
public sealed class Live2DExpressionMap
{
    public string? Listening { get; set; }
    public string? Thinking { get; set; }
    public string? Speaking { get; set; }
    public string? Error { get; set; }
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

    /// <summary>明暗主题模式：<c>system</c>（跟随 Windows 应用模式）/ <c>light</c> / <c>dark</c>。</summary>
    public string Theme { get; set; } = ThemeModes.Dark;

    /// <summary>配色方案 ID（见 <see cref="ThemeCatalog"/>，如 voxime / catppuccin-mocha）。</summary>
    public string ColorScheme { get; set; } = ThemeCatalog.DefaultColorSchemeId;

    /// <summary>界面动效档位：expressive（明显，默认）/ subtle（克制）/ off（关闭）。</summary>
    public string Motion { get; set; } = MotionModes.Expressive;

    /// <summary>设置中心/欢迎/历史等窗口的自定义背景（主题色/纯色/渐变/图片）。</summary>
    public BackgroundConfig Background { get; set; } = new();

    /// <summary>录音悬浮窗（HUD）的自定义背景；默认主题模式（沿用 HUD 半透明卡片）。</summary>
    public BackgroundConfig HudBackground { get; set; } = new();

    /// <summary>录音悬浮窗（HUD）整体缩放（0.7–1.5，1=原尺寸；字体/间距一起缩放）。</summary>
    public double HudScale { get; set; } = 1.0;

    /// <summary>录音悬浮窗（HUD）圆角半径（0–28 DIP，默认 18）。</summary>
    public double HudCornerRadius { get; set; } = 18;

    /// <summary>录音悬浮窗卡片的整体不透明度（0.35–1.0，默认 1.0）。</summary>
    public double HudCardOpacity { get; set; } = 1.0;

    /// <summary>HUD 默认位置：距工作区底部距离（0–300 DIP，默认 42）；手动拖动之后以拖动坐标为准。</summary>
    public double HudBottomMargin { get; set; } = 42;

    /// <summary>HUD 默认位置：水平偏移（-400–400 DIP，0=居中）。</summary>
    public double HudHorizontalOffset { get; set; } = 0;

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
