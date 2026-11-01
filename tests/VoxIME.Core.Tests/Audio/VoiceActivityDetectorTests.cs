using VoxIME.Core.Audio;

namespace VoxIME.Core.Tests.Audio;

/// <summary>VAD 纯算法测试：全部使用合成数据，不依赖任何音频设备。</summary>
public class VoiceActivityDetectorTests
{
    private const double LoudAmplitude = 0.3;

    [Fact]
    public void 静音输入不判定为语音()
    {
        var vad = new VoiceActivityDetector(0.014, 300);

        var speaking = FeedTone(vad, frames: 100, amplitude: 0);

        Assert.False(speaking);
        Assert.False(vad.HasSpeech);
        Assert.Equal(0, vad.LastLevel);
        Assert.Equal(TimeSpan.Zero, vad.SpeechDuration);
        Assert.True(vad.ProcessedFrames >= 100);
    }

    [Fact]
    public void 响亮音频判定为语音()
    {
        var vad = new VoiceActivityDetector(0.014, 300);

        var speaking = FeedTone(vad, frames: 30, amplitude: LoudAmplitude);

        Assert.True(speaking);
        Assert.True(vad.IsSpeaking);
        Assert.True(vad.HasSpeech);
        Assert.True(vad.SpeechDuration >= TimeSpan.FromMilliseconds(300));
        Assert.True(vad.LastLevel > 0.1);
    }

    [Fact]
    public void 最短语音时长边界精确生效()
    {
        var vad = new VoiceActivityDetector(0.014, minSpeechMs: 300);
        var frameSamples = vad.FrameSamples;
        var frame = BuildSine(frameSamples, LoudAmplitude);

        // 20ms/帧：14 帧 = 280ms，还差一点。
        for (var i = 0; i < 14; i++)
        {
            vad.Process(frame);
        }

        Assert.False(vad.HasSpeech);
        Assert.Equal(TimeSpan.FromMilliseconds(280), vad.SpeechDuration);

        vad.Process(frame);

        Assert.True(vad.HasSpeech);
        Assert.Equal(TimeSpan.FromMilliseconds(300), vad.SpeechDuration);
    }

    [Fact]
    public void 短促脉冲不算语音()
    {
        var vad = new VoiceActivityDetector(0.014, minSpeechMs: 300);

        FeedTone(vad, frames: 10, amplitude: LoudAmplitude); // 200ms
        FeedTone(vad, frames: 50, amplitude: 0);

        Assert.False(vad.HasSpeech);
        Assert.False(vad.IsSpeaking);
    }

    [Fact]
    public void 噪声底自适应会抬高阈值()
    {
        // 持续环境噪声（RMS 约 0.012，低于绝对阈值 0.014）会让噪声底逐步抬升。
        var vad = new VoiceActivityDetector(0.014, minSpeechMs: 100);
        var random = new Random(20261101);
        var frameSamples = vad.FrameSamples;

        for (var i = 0; i < 200; i++)
        {
            var noise = new float[frameSamples];
            for (var j = 0; j < noise.Length; j++)
            {
                noise[j] = (float)((random.NextDouble() * 2 - 1) * 0.0208);
            }

            vad.Process(noise);
        }

        Assert.False(vad.IsSpeaking);
        Assert.True(vad.NoiseFloor > 0.005, $"噪声底应已自适应抬升，实际 {vad.NoiseFloor}");
        Assert.False(vad.HasSpeech);

        // 比绝对阈值大、但低于"噪声底 * 2.5"的轻声，应被自适应阈值滤掉。
        FeedTone(vad, frames: 30, amplitude: 0.028);

        Assert.False(vad.IsSpeaking);
        Assert.False(vad.HasSpeech);

        // 明显高于噪声底的正常说话仍然能检测到。
        FeedTone(vad, frames: 30, amplitude: LoudAmplitude);

        Assert.True(vad.IsSpeaking);
        Assert.True(vad.HasSpeech);
    }

    [Fact]
    public void Reset会清空全部状态()
    {
        var vad = new VoiceActivityDetector(0.014, minSpeechMs: 200);
        FeedTone(vad, frames: 30, amplitude: LoudAmplitude);
        Assert.True(vad.HasSpeech);

        vad.Reset();

        Assert.False(vad.HasSpeech);
        Assert.False(vad.IsSpeaking);
        Assert.Equal(TimeSpan.Zero, vad.SpeechDuration);
        Assert.Equal(0, vad.ProcessedFrames);
        Assert.Equal(0, vad.SpeechFrames);
        Assert.Equal(0, vad.LastLevel);
    }

    [Fact]
    public void 任意长度分块输入与整块输入等价()
    {
        var total = BuildSine(16000 / 2, LoudAmplitude); // 0.5s
        const int minSpeechMs = 300;

        var single = new VoiceActivityDetector(0.014, minSpeechMs);
        single.Process(total);
        single.Flush();

        var chunked = new VoiceActivityDetector(0.014, minSpeechMs);
        var chunkSize = 480; // 1.5 帧，故意制造跨帧边界
        for (var offset = 0; offset < total.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, total.Length - offset);
            chunked.Process(total.AsSpan(offset, length));
        }

        chunked.Flush();

        Assert.True(single.HasSpeech);
        Assert.True(chunked.HasSpeech);
        Assert.Equal(single.SpeechDuration, chunked.SpeechDuration);
        Assert.Equal(single.ProcessedFrames, chunked.ProcessedFrames);
    }

    [Fact]
    public void Flush会处理不足一帧的尾部()
    {
        var vad = new VoiceActivityDetector(0.014, minSpeechMs: 10);
        var tone = BuildSine(100, LoudAmplitude); // 6.25ms

        vad.Process(tone);
        vad.Flush();
        Assert.False(vad.HasSpeech);

        vad.Process(tone);
        vad.Flush();
        Assert.True(vad.HasSpeech);
    }

    [Fact]
    public void 空输入与非法采样不会抛异常()
    {
        var vad = new VoiceActivityDetector();

        Assert.False(vad.Process(ReadOnlySpan<float>.Empty));
        Assert.False(vad.Process(new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity }));
        vad.Flush();

        Assert.False(vad.HasSpeech);
        Assert.False(vad.IsSpeaking);
    }

    [Fact]
    public void 构造函数会对非法参数做兜底()
    {
        var vad = new VoiceActivityDetector(double.NaN, -5, 123);

        Assert.InRange(vad.SampleRate, 8000, 48000);
        Assert.True(vad.FrameSamples > 0);
        Assert.Equal(TimeSpan.FromMilliseconds(20), vad.FrameDuration);
        Assert.False(vad.Process(new float[64]));
    }

    private static bool FeedTone(VoiceActivityDetector vad, int frames, double amplitude)
    {
        var frame = BuildSine(vad.FrameSamples, amplitude);
        var speaking = vad.IsSpeaking;
        for (var i = 0; i < frames; i++)
        {
            speaking = vad.Process(frame);
        }

        return speaking;
    }

    private static float[] BuildSine(int samples, double amplitude, double frequencyHz = 440, int sampleRate = 16000)
    {
        var data = new float[samples];
        for (var i = 0; i < samples; i++)
        {
            data[i] = (float)(Math.Sin(2 * Math.PI * frequencyHz * i / sampleRate) * amplitude);
        }

        return data;
    }
}
