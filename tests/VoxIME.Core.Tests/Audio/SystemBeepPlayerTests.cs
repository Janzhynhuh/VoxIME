using NAudio.Wave;
using VoxIME.Core.Abstractions;
using VoxIME.Core.Audio;

namespace VoxIME.Core.Tests.Audio;

/// <summary>提示音合成与播放器测试：只验证 WAV 字节，不依赖声卡是否可用。</summary>
public class SystemBeepPlayerTests
{
    [Theory]
    [InlineData(AudioCue.Start, 80)]
    [InlineData(AudioCue.Stop, 80)]
    [InlineData(AudioCue.Error, 120)]
    [InlineData(AudioCue.Success, 90)]
    public void 各提示音生成合法WAV且时长正确(AudioCue cue, int expectedMs)
    {
        var wav = BeepToneFactory.CreateWav(cue);
        using var reader = new WaveFileReader(new MemoryStream(wav));

        Assert.Equal(44100, reader.WaveFormat.SampleRate);
        Assert.Equal(1, reader.WaveFormat.Channels);
        Assert.Equal(16, reader.WaveFormat.BitsPerSample);
        Assert.InRange(reader.TotalTime.TotalMilliseconds, expectedMs - 2, expectedMs + 2);

        var (maxAmplitude, _) = Analyze(reader);
        Assert.True(maxAmplitude > 1000, "提示音不应是静音。");
    }

    [Fact]
    public void 不同提示音使用不同音高()
    {
        var start = ReadPcm(BeepToneFactory.CreateWav(AudioCue.Start));
        var stop = ReadPcm(BeepToneFactory.CreateWav(AudioCue.Stop));

        var startCrossings = CountZeroCrossings(start);
        var stopCrossings = CountZeroCrossings(stop);

        // 开始提示音（880Hz）过零次数应明显多于结束提示音（520Hz）。
        Assert.True(startCrossings > stopCrossings * 1.4,
            $"start={startCrossings}, stop={stopCrossings}");
    }

    [Fact]
    public void 播放器Play立即返回且不抛异常()
    {
        var player = new SystemBeepPlayer();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        player.Play(AudioCue.Start);
        player.Play(AudioCue.Success);

        stopwatch.Stop();
        Assert.True(stopwatch.ElapsedMilliseconds < 100, "Play 必须是非阻塞的。");

        // 给后台播放线程留出时间，确认异步路径不会把异常抛到进程。
        player.Play(AudioCue.Stop);
        player.Play(AudioCue.Error);
        Thread.Sleep(500);
    }

    private static (int MaxAmplitude, int SampleCount) Analyze(WaveFileReader reader)
    {
        var buffer = new byte[reader.Length];
        var total = 0;
        int read;
        while ((read = reader.Read(buffer, total, buffer.Length - total)) > 0)
        {
            total += read;
        }

        var max = 0;
        for (var i = 0; i + 1 < total; i += 2)
        {
            var value = (short)(buffer[i] | (buffer[i + 1] << 8));
            max = Math.Max(max, Math.Abs(value));
        }

        return (max, total / 2);
    }

    private static short[] ReadPcm(byte[] wav)
    {
        using var reader = new WaveFileReader(new MemoryStream(wav));
        var buffer = new byte[reader.Length];
        var total = 0;
        int read;
        while ((read = reader.Read(buffer, total, buffer.Length - total)) > 0)
        {
            total += read;
        }

        var samples = new short[total / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(buffer[i * 2] | (buffer[i * 2 + 1] << 8));
        }

        return samples;
    }

    private static int CountZeroCrossings(short[] samples)
    {
        var crossings = 0;
        var previous = samples.Length > 0 ? samples[0] : (short)0;
        foreach (var sample in samples)
        {
            if ((previous < 0 && sample >= 0) || (previous >= 0 && sample < 0))
            {
                crossings++;
            }

            previous = sample;
        }

        return crossings;
    }
}
