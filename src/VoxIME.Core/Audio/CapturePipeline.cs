using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VoxIME.Core.Audio;

/// <summary>
/// 麦克风采集数据管道：把设备原始格式（常见 48kHz 立体声 float）解码 → 下混单声道 → 重采样到 16kHz。
/// 以流式方式工作：每次 <see cref="AddSamples"/> 后可以用 <see cref="ReadAvailable"/> 取走新产生的 16kHz 采样。
/// </summary>
internal sealed class CapturePipeline : IDisposable
{
    public const int TargetSampleRate = 16000;

    private readonly BufferedWaveProvider _buffer;
    private readonly ISampleProvider _samples;
    private readonly float[] _readBuffer = new float[4096];
    private bool _disposed;

    public CapturePipeline(WaveFormat sourceFormat, int targetSampleRate = TargetSampleRate)
    {
        ArgumentNullException.ThrowIfNull(sourceFormat);
        if (targetSampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetSampleRate));
        }

        SourceFormat = sourceFormat.AsStandardWaveFormat();
        _buffer = new BufferedWaveProvider(SourceFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(20),
            DiscardOnBufferOverflow = true,
            ReadFully = false
        };

        ISampleProvider provider = _buffer.ToSampleProvider();
        if (provider.WaveFormat.Channels == 2)
        {
            provider = new StereoToMonoSampleProvider(provider);
        }
        else if (provider.WaveFormat.Channels > 2)
        {
            provider = new AverageToMonoSampleProvider(provider);
        }

        if (provider.WaveFormat.SampleRate != targetSampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, targetSampleRate);
        }

        _samples = provider;
        TargetFormat = _samples.WaveFormat;
    }

    /// <summary>设备原始采集格式（已归一化为标准 WaveFormat）。</summary>
    public WaveFormat SourceFormat { get; }

    /// <summary>输出格式：目标采样率单声道 IEEE float。</summary>
    public WaveFormat TargetFormat { get; }

    /// <summary>把一段设备原始字节送入管道。</summary>
    public void AddSamples(byte[] buffer, int bytesRecorded)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer is null || bytesRecorded <= 0)
        {
            return;
        }

        var count = Math.Min(bytesRecorded, buffer.Length);
        if (count <= 0)
        {
            return;
        }

        _buffer.AddSamples(buffer, 0, count);
    }

    /// <summary>取走当前所有可用的 16kHz 单声道 float 采样（没有新数据时返回空数组）。</summary>
    public float[] ReadAvailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = new List<float>(_readBuffer.Length);
        var guard = 0;
        while (guard++ < 128)
        {
            var read = _samples.Read(_readBuffer, 0, _readBuffer.Length);
            if (read <= 0)
            {
                break;
            }

            for (var i = 0; i < read; i++)
            {
                result.Add(_readBuffer[i]);
            }
        }

        return result.Count == 0 ? Array.Empty<float>() : result.ToArray();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
    }
}
