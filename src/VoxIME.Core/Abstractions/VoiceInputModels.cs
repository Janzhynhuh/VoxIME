using VoxIME.Core.Abstractions;
using VoxIME.Core.Configuration;

namespace VoxIME.Core.Abstractions;

public enum AppState
{
    Idle,
    Listening,
    Recognizing,
    Speaking,
    Error,
    Disabled
}

public sealed class AppStateChangedEventArgs : EventArgs
{
    public AppStateChangedEventArgs(AppState state, AppState previousState, string? statusText)
    {
        State = state;
        PreviousState = previousState;
        StatusText = statusText;
    }

    public AppState State { get; }
    public AppState PreviousState { get; }
    public string? StatusText { get; }
}

/// <summary>一次语音输入的完整记录（历史面板/统计）。</summary>
public sealed class TranscriptionRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public string ProviderName { get; init; } = "";
    public string? ProviderTypeId { get; init; }
    public TimeSpan AudioDuration { get; init; }
    public TimeSpan Latency { get; init; }
    public bool Inserted { get; set; }
    public string? Error { get; set; }
    public string? AudioFilePath { get; set; }

    public string DurationLabel => $"{AudioDuration.TotalSeconds:0.0}s";
    public string LocalTimeLabel => CreatedAt.ToLocalTime().ToString("HH:mm:ss");
}

/// <summary>
/// 语音输入主控制器：热键→录音→识别→后处理→插入→朗读→桌宠状态联动，全部在这里编排。
/// UI 只依赖本接口。
/// </summary>
public interface IVoiceInputController : IAsyncDisposable
{
    AppState State { get; }
    string? StatusText { get; }
    AppConfig Config { get; }
    ILive2DController Live2D { get; }

    /// <summary>托盘"暂停"状态：为 true 时不响应热键。</summary>
    bool IsPaused { get; set; }

    event EventHandler<AppStateChangedEventArgs>? StateChanged;
    event EventHandler<TranscriptionRecord>? TranscriptionReceived;
    event EventHandler<AudioLevelEventArgs>? MicrophoneLevelChanged;
    event EventHandler<string>? ErrorOccurred;

    /// <summary>加载配置、启动热键服务、初始化桌宠。App 启动时调用一次。</summary>
    void Initialize();

    /// <summary>设置窗口保存后调用：热键、音频参数、桌宠、TTS 立即生效。</summary>
    void ApplyConfig(AppConfig config);

    /// <summary>开始录音；繁忙/不可用时返回 false。</summary>
    bool StartRecording();

    /// <summary>结束录音并识别、插入、记录历史；无有效语音时返回 null。</summary>
    Task<TranscriptionRecord?> StopAndTranscribeAsync(CancellationToken cancellationToken = default);

    /// <summary>免按键模式：未录音则开始，录音中则结束并识别。</summary>
    Task<TranscriptionRecord?> ToggleRecordingAsync(CancellationToken cancellationToken = default);

    /// <summary>取消当前录音/识别。</summary>
    void Cancel();

    /// <summary>用指定配置识别给定音频（设置页/自检用）。</summary>
    Task<AsrResult> TranscribeAsync(AudioClip clip, ProviderProfile profile, CancellationToken cancellationToken = default);

    /// <summary>录 N 秒并用指定配置识别，用于设置页"测试识别"。</summary>
    Task<AsrResult> TestProviderAsync(ProviderProfile profile, int seconds = 3, CancellationToken cancellationToken = default);

    Task<InsertionResult> InsertTextAsync(string text, CancellationToken cancellationToken = default);

    Task SpeakAsync(string text, CancellationToken cancellationToken = default);

    IReadOnlyList<AudioDeviceInfo> GetInputDevices();
    IReadOnlyList<string> GetTtsVoices();
}
