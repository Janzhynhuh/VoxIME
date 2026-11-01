using NAudio.Wave;
using VoxIME.Core.Abstractions;
using VoxIME.Core.Audio;

namespace VoxIME.Core.Tests.Audio;

/// <summary>采集数据管道（解码/下混/重采样）测试：使用合成字节流，不打开真实麦克风。</summary>
public class CapturePipelineTests
{
    [Fact]
    public void 十六k单声道PCM16直通且电平正确()
    {
        var source = AudioClip.CreateTone(TimeSpan.FromSeconds(1), 440, 0.5).Pcm16;
        using var pipeline = new CapturePipeline(new WaveFormat(16000, 16, 1));

        pipeline.AddSamples(source, source.Length);
        var output = pipeline.ReadAvailable();

        Assert.InRange(output.Length, 15990, 16010);
        Assert.InRange(Rms(output), 0.34, 0.37);
        Assert.Equal(16000, pipeline.TargetFormat.SampleRate);
        Assert.Equal(1, pipeline.TargetFormat.Channels);
    }

    [Fact]
    public void 四十八k立体声Float重采样为十六k单声道()
    {
        const int sourceRate = 48000;
        const int frames = sourceRate;
        var interleaved = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var value = (float)(Math.Sin(2 * Math.PI * 440 * i / sourceRate) * 0.5);
            interleaved[i * 2] = value;
            interleaved[i * 2 + 1] = value;
        }

        var bytes = ToBytes(interleaved);
        using var pipeline = new CapturePipeline(WaveFormat.CreateIeeeFloatWaveFormat(sourceRate, 2));

        var output = new List<float>();
        var bytesPerChunk = 4800 * 2 * 4; // 100ms
        for (var offset = 0; offset < bytes.Length; offset += bytesPerChunk)
        {
            var length = Math.Min(bytesPerChunk, bytes.Length - offset);
            pipeline.AddSamples(bytes.AsSpan(offset, length).ToArray(), length);
            output.AddRange(pipeline.ReadAvailable());
        }

        Assert.InRange(output.Count, 15000, 16100);
        Assert.InRange(Rms(output), 0.25, 0.45);
        Assert.Equal(16000, pipeline.TargetFormat.SampleRate);
        Assert.Equal(1, pipeline.TargetFormat.Channels);
    }

    [Fact]
    public void 立体声下混为左右平均()
    {
        using var pipeline = new CapturePipeline(WaveFormat.CreateIeeeFloatWaveFormat(16000, 2));
        var interleaved = new float[1600 * 2];
        for (var i = 0; i < 1600; i++)
        {
            interleaved[i * 2] = 0.25f;
            interleaved[i * 2 + 1] = -0.25f;
        }

        var bytes = ToBytes(interleaved);
        pipeline.AddSamples(bytes, bytes.Length);
        var output = pipeline.ReadAvailable();

        Assert.Equal(1600, output.Length);
        Assert.InRange(Math.Abs(Rms(output)), 0, 0.001);
    }

    [Fact]
    public void 多声道下混为全声道平均()
    {
        using var pipeline = new CapturePipeline(WaveFormat.CreateIeeeFloatWaveFormat(16000, 4));
        var interleaved = new float[1600 * 4];
        for (var i = 0; i < interleaved.Length; i++)
        {
            interleaved[i] = 0.2f;
        }

        var bytes = ToBytes(interleaved);
        pipeline.AddSamples(bytes, bytes.Length);
        var output = pipeline.ReadAvailable();

        Assert.Equal(1600, output.Length);
        Assert.InRange(Rms(output), 0.19, 0.21);
    }

    [Fact]
    public void 静音输入输出全零()
    {
        using var pipeline = new CapturePipeline(new WaveFormat(16000, 16, 1));
        var bytes = new byte[3200];

        pipeline.AddSamples(bytes, bytes.Length);
        var output = pipeline.ReadAvailable();

        Assert.Equal(1600, output.Length);
        Assert.All(output, sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void 没有数据时读取返回空数组()
    {
        using var pipeline = new CapturePipeline(new WaveFormat(16000, 16, 1));

        Assert.Empty(pipeline.ReadAvailable());
        Assert.Equal(16000, CapturePipeline.TargetSampleRate);
    }

    private static byte[] ToBytes(float[] samples)
    {
        var bytes = new byte[samples.Length * 4];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static double Rms(IReadOnlyList<float> samples)
    {
        if (samples.Count == 0)
        {
            return 0;
        }

        var sum = 0d;
        foreach (var sample in samples)
        {
            sum += (double)sample * sample;
        }

        return Math.Sqrt(sum / samples.Count);
    }
}
