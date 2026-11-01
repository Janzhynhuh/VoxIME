using System.Buffers.Binary;
using System.Text;

namespace VoxIME.Core.Tts;

/// <summary>
/// 解析 TTS 输出的 WAV 字节并生成"音量包络曲线"，用于驱动 Live2D 口型。
/// 纯计算逻辑，不依赖音频设备，可离线单测。
/// </summary>
internal static class TtsAudioAnalyzer
{
    public const int DefaultIntervalMs = 33;

    /// <summary>按固定时间窗口计算每个窗口的 RMS 电平（0..1，带 2 倍感知增益），空/非法 WAV 返回空数组。</summary>
    public static float[] BuildLevelCurve(byte[]? wavBytes, int intervalMs = DefaultIntervalMs)
    {
        if (wavBytes is null || wavBytes.Length < 44 || intervalMs <= 0)
        {
            return Array.Empty<float>();
        }

        if (!TryReadFormat(wavBytes, out var format))
        {
            return Array.Empty<float>();
        }

        var bytesPerFrame = format.Channels * (format.BitsPerSample / 8);
        if (bytesPerFrame <= 0 || format.DataLength < bytesPerFrame)
        {
            return Array.Empty<float>();
        }

        var frames = format.DataLength / bytesPerFrame;
        var windowFrames = Math.Max(1, format.SampleRate * intervalMs / 1000);
        var windowCount = (frames + windowFrames - 1) / windowFrames;
        var curve = new float[windowCount];
        var span = wavBytes.AsSpan(format.DataOffset, format.DataLength);

        for (var window = 0; window < windowCount; window++)
        {
            var startFrame = window * windowFrames;
            var endFrame = Math.Min(frames, startFrame + windowFrames);
            var sumSquares = 0d;
            var sampleCount = 0;
            for (var frame = startFrame; frame < endFrame; frame++)
            {
                for (var channel = 0; channel < format.Channels; channel++)
                {
                    var sampleOffset = (frame * format.Channels + channel) * (format.BitsPerSample / 8);
                    var sample = ReadSample(span, sampleOffset, format);
                    sumSquares += (double)sample * sample;
                    sampleCount++;
                }
            }

            var rms = sampleCount > 0 ? Math.Sqrt(sumSquares / sampleCount) : 0d;
            curve[window] = (float)Math.Clamp(rms * 2.0, 0d, 1d);
        }

        return curve;
    }

    private static float ReadSample(ReadOnlySpan<byte> data, int offset, in WavFormat format)
    {
        if (format.IsFloat)
        {
            return format.BitsPerSample == 64
                ? (float)BinaryPrimitives.ReadDoubleLittleEndian(data.Slice(offset, 8))
                : BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset, 4));
        }

        return format.BitsPerSample switch
        {
            8 => (data[offset] - 128) / 128f,
            16 => BinaryPrimitives.ReadInt16LittleEndian(data.Slice(offset, 2)) / 32768f,
            24 => ReadPcm24(data, offset) / 8388608f,
            32 => BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4)) / 2147483648f,
            _ => 0f
        };
    }

    private static int ReadPcm24(ReadOnlySpan<byte> data, int offset)
    {
        var value = data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16);
        if ((value & 0x800000) != 0)
        {
            value |= ~0xFFFFFF;
        }

        return value;
    }

    /// <summary>读取 WAV 的格式与音频数据位置。</summary>
    public static bool TryReadFormat(byte[]? wavBytes, out WavFormat format)
    {
        format = default;
        if (wavBytes is null || wavBytes.Length < 44)
        {
            return false;
        }

        var span = wavBytes.AsSpan();
        if (!IsChunkId(span, 0, "RIFF") || !IsChunkId(span, 8, "WAVE"))
        {
            return false;
        }

        var offset = 12;
        int? sampleRate = null;
        int? channels = null;
        int? bitsPerSample = null;
        int? formatTag = null;
        var dataOffset = -1;
        var dataLength = 0;

        while (offset + 8 <= span.Length)
        {
            var chunkId = span.Slice(offset, 4);
            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset + 4, 4));
            offset += 8;
            if (chunkSize < 0)
            {
                break;
            }

            var chunkEnd = Math.Min(span.Length, offset + chunkSize);
            if (chunkEnd < offset)
            {
                break;
            }

            if (IsChunkId(chunkId, 0, "fmt "))
            {
                if (chunkSize >= 16)
                {
                    var tag = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset, 2));
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset + 2, 2));
                    sampleRate = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset + 4, 4));
                    bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset + 14, 2));

                    if (tag == 0xFFFE && chunkSize >= 40)
                    {
                        // WAVE_FORMAT_EXTENSIBLE：真正的格式在 SubFormat GUID 前两个字节。
                        tag = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset + 24, 2));
                    }

                    formatTag = tag;
                }
            }
            else if (IsChunkId(chunkId, 0, "data"))
            {
                dataOffset = offset;
                dataLength = chunkEnd - offset;
            }

            offset = chunkEnd + (chunkSize & 1);
        }

        if (formatTag is null || sampleRate is null or <= 0 || channels is null or <= 0 || bitsPerSample is null || dataOffset < 0 || dataLength <= 0)
        {
            return false;
        }

        format = new WavFormat(
            sampleRate.Value,
            channels.Value,
            bitsPerSample.Value,
            formatTag.Value,
            dataOffset,
            dataLength,
            formatTag.Value == 3 && bitsPerSample.Value >= 32);
        return true;
    }

    private static bool IsChunkId(ReadOnlySpan<byte> span, int offset, string id)
    {
        if (span.Length < offset + 4 || id.Length != 4)
        {
            return false;
        }

        return span[offset] == (byte)id[0]
               && span[offset + 1] == (byte)id[1]
               && span[offset + 2] == (byte)id[2]
               && span[offset + 3] == (byte)id[3];
    }

    /// <summary>WAV 格式信息。</summary>
    public readonly record struct WavFormat(
        int SampleRate,
        int Channels,
        int BitsPerSample,
        int FormatTag,
        int DataOffset,
        int DataLength,
        bool IsFloat)
    {
        public bool IsPcm16 => !IsFloat && BitsPerSample == 16 && FormatTag == 1;
    }
}
