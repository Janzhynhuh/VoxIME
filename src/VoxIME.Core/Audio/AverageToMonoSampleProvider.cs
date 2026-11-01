using NAudio.Wave;

namespace VoxIME.Core.Audio;

/// <summary>
/// 把多声道（&gt;2）样本平均下混为单声道。NAudio 自带的 StereoToMonoSampleProvider 只支持双声道，
/// 声卡偶尔会报 4/6/8 声道格式，这里做一个通用兜底。
/// </summary>
internal sealed class AverageToMonoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private float[] _sourceBuffer = Array.Empty<float>();

    public AverageToMonoSampleProvider(ISampleProvider source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.WaveFormat.Channels <= 0)
        {
            throw new ArgumentException("源采样提供程序没有声道。", nameof(source));
        }

        _source = source;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var channels = _source.WaveFormat.Channels;
        var needed = count * channels;
        if (_sourceBuffer.Length < needed)
        {
            _sourceBuffer = new float[needed];
        }

        var read = _source.Read(_sourceBuffer, 0, needed);
        var frames = read / channels;
        for (var frame = 0; frame < frames; frame++)
        {
            var sum = 0f;
            var baseIndex = frame * channels;
            for (var channel = 0; channel < channels; channel++)
            {
                sum += _sourceBuffer[baseIndex + channel];
            }

            buffer[offset + frame] = sum / channels;
        }

        return frames;
    }
}
