namespace VoxIME.Core.Abstractions;

public enum AudioCaptureState
{
    Idle,
    Starting,
    Capturing,
    Stopping,
    Failed
}

/// <summary>输入设备信息（麦克风）。</summary>
public sealed class AudioDeviceInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool IsDefault { get; init; }
    public override string ToString() => Name;
}

public sealed class AudioLevelEventArgs : EventArgs
{
    public AudioLevelEventArgs(float level) => Level = level;

    /// <summary>归一化音量 0..1（RMS）。</summary>
    public float Level { get; }
}

/// <summary>一段 16-bit 单声道/多声道 PCM 音频（默认 16kHz 单声道，适配各 ASR 厂商）。</summary>
public sealed class AudioClip
{
    public static AudioClip Empty { get; } = new()
    {
        Pcm16 = Array.Empty<byte>(),
        SampleRate = 16000,
        Channels = 1
    };

    public required byte[] Pcm16 { get; init; }
    public required int SampleRate { get; init; }
    public int Channels { get; init; } = 1;

    public int BytesPerSecond => SampleRate * Channels * 2;
    public int FrameCount => Pcm16.Length / (2 * Math.Max(1, Channels));
    public TimeSpan Duration => TimeSpan.FromSeconds(FrameCount / (double)Math.Max(1, SampleRate));

    /// <summary>编码为 44 字节头的标准 PCM WAV 文件字节。</summary>
    public byte[] ToWav()
    {
        using var ms = new MemoryStream(44 + Pcm16.Length);
        using var w = new BinaryWriter(ms);
        var dataSize = Pcm16.Length;
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataSize);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1); // PCM
        w.Write((short)Channels);
        w.Write(SampleRate);
        w.Write(SampleRate * Channels * 2);
        w.Write((short)(Channels * 2));
        w.Write((short)16);
        w.Write("data"u8.ToArray());
        w.Write(dataSize);
        w.Write(Pcm16);
        w.Flush();
        return ms.ToArray();
    }

    public static AudioClip FromPcm16(byte[] pcm16, int sampleRate = 16000, int channels = 1) =>
        new() { Pcm16 = pcm16, SampleRate = sampleRate, Channels = channels };

    /// <summary>生成静音片段（测试/兜底用）。</summary>
    public static AudioClip CreateSilence(TimeSpan duration, int sampleRate = 16000, int channels = 1)
    {
        var frames = (int)(duration.TotalSeconds * sampleRate);
        return FromPcm16(new byte[frames * channels * 2], sampleRate, channels);
    }

    /// <summary>生成正弦波片段（测试/自检用）。</summary>
    public static AudioClip CreateTone(TimeSpan duration, double frequencyHz = 440, double amplitude = 0.3, int sampleRate = 16000)
    {
        var frames = (int)(duration.TotalSeconds * sampleRate);
        var bytes = new byte[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var v = (short)(Math.Sin(2 * Math.PI * frequencyHz * i / sampleRate) * amplitude * short.MaxValue);
            bytes[i * 2] = (byte)(v & 0xFF);
            bytes[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        return FromPcm16(bytes, sampleRate);
    }
}

public sealed class RecordingOptions
{
    public string? DeviceId { get; set; }
    public int SampleRate { get; set; } = 16000;
    public int Channels { get; set; } = 1;
    public int MaxDurationSeconds { get; set; } = 120;

    /// <summary>检测到持续静音后自动请求停止（免按键连续录音场景）。</summary>
    public bool AutoStopOnSilence { get; set; } = true;
    public int SilenceTimeoutMs { get; set; } = 1200;
    public int MinSpeechMs { get; set; } = 300;
    public double VadThreshold { get; set; } = 0.003;
}

public sealed class RecordingResult
{
    public required AudioClip Clip { get; init; }
    public bool HasSpeech { get; init; }
    public TimeSpan RecordingDuration { get; init; }
    public TimeSpan SpeechDuration { get; init; }

    /// <summary>整段录音中最大的窗口 RMS 电平（0..1，与 VadThreshold 同一量纲），用于诊断输入太低/无声。</summary>
    public double PeakLevel { get; init; }
}

/// <summary>WASAPI 麦克风采集服务。Start 后持续采集并通过 LevelChanged 上报音量；StopAsync 返回整段 PCM。</summary>
public interface IAudioCaptureService : IAsyncDisposable
{
    AudioCaptureState State { get; }
    bool IsRecording { get; }

    event EventHandler<AudioLevelEventArgs>? LevelChanged;

    /// <summary>达到静音超时/最大时长时触发，上层应调用 StopAsync。</summary>
    event EventHandler? AutoStopRequested;

    IReadOnlyList<AudioDeviceInfo> GetDevices();
    void Start(RecordingOptions options);
    Task<RecordingResult> StopAsync(CancellationToken cancellationToken = default);
    void Cancel();
}

public enum AudioCue
{
    Start,
    Stop,
    Error,
    Success
}

/// <summary>录音提示音播放器。</summary>
public interface IBeepPlayer
{
    void Play(AudioCue cue);
}
