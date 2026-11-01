using VoxIME.Core.Configuration;

namespace VoxIME.Core.Abstractions;

public sealed class TtsOptions
{
    public string? VoiceName { get; init; }
    /// <summary>-10..10，SAPI Rate。</summary>
    public int Rate { get; init; }
    /// <summary>0..100。</summary>
    public int Volume { get; init; } = 90;
}

/// <summary>语音合成（Windows SAPI），支持朗读识别结果并输出播放音量用于 Live2D 口型驱动。</summary>
public interface ITtsService : IDisposable
{
    bool IsAvailable { get; }
    IReadOnlyList<string> GetInstalledVoices();

    event EventHandler<AudioLevelEventArgs>? LevelChanged;
    event EventHandler? SpeakingStarted;
    event EventHandler? SpeakingCompleted;
    event EventHandler<string>? Error;

    Task SpeakAsync(string text, TtsOptions options, CancellationToken cancellationToken = default);
    void Stop();
}
