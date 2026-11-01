using VoxIME.Core.Abstractions;
using VoxIME.Core.Tts;

namespace VoxIME.Core.Tests.Tts;

/// <summary>TTS 音量包络分析测试：用合成 WAV，不依赖音频设备或语音包。</summary>
public class TtsAudioAnalyzerTests
{
    [Fact]
    public void 正弦WAV生成非零电平曲线()
    {
        var wav = AudioClip.CreateTone(TimeSpan.FromSeconds(1), 440, 0.5).ToWav();

        var curve = TtsAudioAnalyzer.BuildLevelCurve(wav, 50);

        Assert.Equal(20, curve.Length);
        Assert.All(curve, value => Assert.InRange(value, 0.6f, 0.8f)); // 0.3536 * 2 ≈ 0.707
    }

    [Fact]
    public void 前静音后语音的电平曲线分段正确()
    {
        var silence = AudioClip.CreateSilence(TimeSpan.FromMilliseconds(500)).Pcm16;
        var tone = AudioClip.CreateTone(TimeSpan.FromMilliseconds(500), 440, 0.5).Pcm16;
        var pcm = silence.Concat(tone).ToArray();
        var wav = AudioClip.FromPcm16(pcm).ToWav();

        var curve = TtsAudioAnalyzer.BuildLevelCurve(wav, 50);

        Assert.Equal(20, curve.Length);
        Assert.All(curve.Take(10), value => Assert.Equal(0f, value));
        Assert.All(curve.Skip(10), value => Assert.InRange(value, 0.6f, 0.8f));
    }

    [Fact]
    public void 非法WAV数据返回空曲线()
    {
        Assert.Empty(TtsAudioAnalyzer.BuildLevelCurve(null));
        Assert.Empty(TtsAudioAnalyzer.BuildLevelCurve(Array.Empty<byte>()));
        Assert.Empty(TtsAudioAnalyzer.BuildLevelCurve(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
        Assert.Empty(TtsAudioAnalyzer.BuildLevelCurve(new byte[100]));

        var tone = AudioClip.CreateTone(TimeSpan.FromMilliseconds(100)).ToWav();
        Assert.Empty(TtsAudioAnalyzer.BuildLevelCurve(tone, intervalMs: 0));
    }

    [Fact]
    public void WAV格式解析正确()
    {
        var wav = AudioClip.CreateTone(TimeSpan.FromMilliseconds(100), 440, 0.3).ToWav();

        Assert.True(TtsAudioAnalyzer.TryReadFormat(wav, out var format));
        Assert.Equal(16000, format.SampleRate);
        Assert.Equal(1, format.Channels);
        Assert.Equal(16, format.BitsPerSample);
        Assert.True(format.IsPcm16);
        Assert.Equal(3200, format.DataLength);
    }
}
