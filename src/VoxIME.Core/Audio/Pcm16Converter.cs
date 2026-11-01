namespace VoxIME.Core.Audio;

/// <summary>float 归一化采样与 16bit PCM 之间的转换。</summary>
internal static class Pcm16Converter
{
    /// <summary>把 [-1,1] 的 float 采样写成小端 16bit PCM，并返回平方和（供电平统计复用，省一次遍历）。</summary>
    public static double WriteTo(Stream stream, ReadOnlySpan<float> samples)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (samples.IsEmpty)
        {
            return 0;
        }

        var bytes = new byte[samples.Length * 2];
        var sumSquares = 0d;
        for (var i = 0; i < samples.Length; i++)
        {
            var sample = samples[i];
            if (!float.IsFinite(sample))
            {
                sample = 0f;
            }

            sumSquares += (double)sample * sample;
            var scaled = Math.Clamp(sample, -1f, 1f) * short.MaxValue;
            var value = (short)Math.Round(scaled, MidpointRounding.AwayFromZero);
            bytes[i * 2] = (byte)(value & 0xFF);
            bytes[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }

        stream.Write(bytes, 0, bytes.Length);
        return sumSquares;
    }

    /// <summary>把 [-1,1] 的 float 采样编码为 16bit PCM 字节。</summary>
    public static byte[] ToBytes(ReadOnlySpan<float> samples)
    {
        using var ms = new MemoryStream(samples.Length * 2);
        WriteTo(ms, samples);
        return ms.ToArray();
    }

    /// <summary>把一个小端 16bit PCM 采样点转换为 [-1,1] 的 float。</summary>
    public static float FromPcm16(short sample) => sample / (float)short.MaxValue;
}
