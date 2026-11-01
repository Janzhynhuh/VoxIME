using NAudio.Wave;
using VoxIME.Core.Abstractions;

namespace VoxIME.Core.Audio;

/// <summary>
/// 用 NAudio 合成一段短提示音的 WAV 字节（44 字节头 + 16bit PCM 单声道）。
/// 直接生成字节而不依赖音频设备，方便单元测试验证格式与波形。
/// </summary>
internal static class BeepToneFactory
{
    public const int SampleRate = 44100;

    /// <summary>提示音各段（频率 Hz，时长 ms）。</summary>
    private static readonly (double FrequencyHz, int DurationMs)[] StartTones = { (880, 80) };
    private static readonly (double FrequencyHz, int DurationMs)[] StopTones = { (520, 80) };
    private static readonly (double FrequencyHz, int DurationMs)[] ErrorTones = { (660, 60), (440, 60) };
    private static readonly (double FrequencyHz, int DurationMs)[] SuccessTones = { (1040, 90) };

    /// <summary>按提示音类型生成完整 WAV 文件字节。</summary>
    public static byte[] CreateWav(AudioCue cue)
    {
        var segments = cue switch
        {
            AudioCue.Start => StartTones,
            AudioCue.Stop => StopTones,
            AudioCue.Error => ErrorTones,
            AudioCue.Success => SuccessTones,
            _ => StartTones
        };

        var pcm = new List<byte>(256);
        const double amplitude = 0.28;
        foreach (var (frequency, durationMs) in segments)
        {
            var frames = SampleRate * durationMs / 1000;
            var fadeFrames = Math.Max(1, SampleRate * 6 / 1000); // 6ms 淡入淡出，避免爆音
            for (var i = 0; i < frames; i++)
            {
                var envelope = 1.0;
                if (i < fadeFrames)
                {
                    envelope = i / (double)fadeFrames;
                }
                else if (i > frames - fadeFrames)
                {
                    envelope = Math.Max(0, (frames - i) / (double)fadeFrames);
                }

                var sample = Math.Sin(2 * Math.PI * frequency * i / SampleRate) * amplitude * envelope;
                var value = (short)Math.Round(Math.Clamp(sample, -1, 1) * short.MaxValue, MidpointRounding.AwayFromZero);
                pcm.Add((byte)(value & 0xFF));
                pcm.Add((byte)((value >> 8) & 0xFF));
            }
        }

        return AudioClip.FromPcm16(pcm.ToArray(), SampleRate, 1).ToWav();
    }

    /// <summary>提示音总时长（测试/调试用）。</summary>
    public static int TotalDurationMs(AudioCue cue)
    {
        var segments = cue switch
        {
            AudioCue.Start => StartTones,
            AudioCue.Stop => StopTones,
            AudioCue.Error => ErrorTones,
            AudioCue.Success => SuccessTones,
            _ => StartTones
        };

        var total = 0;
        foreach (var (_, durationMs) in segments)
        {
            total += durationMs;
        }

        return total;
    }
}
