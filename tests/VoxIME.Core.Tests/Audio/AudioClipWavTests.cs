using System.Text;
using VoxIME.Core.Abstractions;

namespace VoxIME.Core.Tests.Audio;

/// <summary>WAV 编码测试：直接校验 44 字节 PCM WAV 头与采样数据（不依赖音频设备）。</summary>
public class AudioClipWavTests
{
    [Fact]
    public void ToWav生成标准44字节PCM头()
    {
        var clip = AudioClip.CreateTone(TimeSpan.FromMilliseconds(120), 440, 0.5);
        var wav = clip.ToWav();

        Assert.Equal(44 + clip.Pcm16.Length, wav.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(36 + clip.Pcm16.Length, BitConverter.ToInt32(wav, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal(16, BitConverter.ToInt32(wav, 16));
        Assert.Equal(1, BitConverter.ToInt16(wav, 20)); // PCM
        Assert.Equal(1, BitConverter.ToInt16(wav, 22)); // 单声道
        Assert.Equal(16000, BitConverter.ToInt32(wav, 24));
        Assert.Equal(32000, BitConverter.ToInt32(wav, 28)); // byte rate
        Assert.Equal(2, BitConverter.ToInt16(wav, 32)); // block align
        Assert.Equal(16, BitConverter.ToInt16(wav, 34));
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(clip.Pcm16.Length, BitConverter.ToInt32(wav, 40));
    }

    [Fact]
    public void 采样帧数与时长计算正确()
    {
        var clip = AudioClip.CreateTone(TimeSpan.FromSeconds(1.5), 440, 0.3);
        var silence = AudioClip.CreateSilence(TimeSpan.FromMilliseconds(500));

        Assert.Equal(24000, clip.FrameCount);
        Assert.Equal(TimeSpan.FromSeconds(1.5), clip.Duration);
        Assert.Equal(8000, silence.FrameCount);
        Assert.Equal(0.5, silence.Duration.TotalSeconds, 3);
        Assert.Equal(32000, clip.BytesPerSecond);
    }

    [Fact]
    public void 静音片段全部为0()
    {
        var clip = AudioClip.CreateSilence(TimeSpan.FromMilliseconds(100));

        Assert.Equal(3200, clip.Pcm16.Length);
        Assert.All(clip.Pcm16, b => Assert.Equal(0, b));
        Assert.Equal(0, AudioClip.Empty.FrameCount);
    }

    [Fact]
    public void 正弦波的RMS接近理论值()
    {
        var clip = AudioClip.CreateTone(TimeSpan.FromMilliseconds(500), 440, 0.5, 16000);
        var sum = 0d;
        var count = 0;
        for (var i = 0; i < clip.Pcm16.Length; i += 2)
        {
            var sample = (short)(clip.Pcm16[i] | (clip.Pcm16[i + 1] << 8)) / 32768.0;
            sum += sample * sample;
            count++;
        }

        var rms = Math.Sqrt(sum / count);

        // 振幅 0.5 的正弦波 RMS 理论值约 0.3536。
        Assert.InRange(rms, 0.34, 0.37);
    }
}
