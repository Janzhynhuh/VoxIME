using System.Text;
using VoxIME.Core.Abstractions;

namespace VoxIME.Core.Tests.Configuration;

public sealed class AudioClipTests
{
    [Fact]
    public void ToWav_ProducesValidPcmHeader()
    {
        var clip = AudioClip.CreateTone(TimeSpan.FromSeconds(0.5), 440);
        var wav = clip.ToWav();

        Assert.Equal(44 + clip.Pcm16.Length, wav.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal(16, BitConverter.ToInt32(wav, 16));
        Assert.Equal(1, BitConverter.ToInt16(wav, 20)); // PCM
        Assert.Equal(1, BitConverter.ToInt16(wav, 22)); // mono
        Assert.Equal(16000, BitConverter.ToInt32(wav, 24));
        Assert.Equal(32000, BitConverter.ToInt32(wav, 28)); // byte rate
        Assert.Equal(2, BitConverter.ToInt16(wav, 32));
        Assert.Equal(16, BitConverter.ToInt16(wav, 34));
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(clip.Pcm16.Length, BitConverter.ToInt32(wav, 40));
    }

    [Fact]
    public void Duration_IsComputedFromPcmLength()
    {
        var clip = AudioClip.CreateSilence(TimeSpan.FromSeconds(2));
        Assert.Equal(2.0, clip.Duration.TotalSeconds, 3);
        Assert.Equal(32000, clip.FrameCount);
    }

    [Fact]
    public void CreateTone_IsNotSilent()
    {
        var clip = AudioClip.CreateTone(TimeSpan.FromSeconds(0.1), 440);
        Assert.Contains(clip.Pcm16, b => b != 0);
        Assert.NotEmpty(clip.Pcm16);
    }

    [Fact]
    public void StereoClip_ComputesFramesCorrectly()
    {
        var clip = AudioClip.FromPcm16(new byte[16000 * 2 * 2], 16000, channels: 2);
        Assert.Equal(1.0, clip.Duration.TotalSeconds, 3);
    }

    [Fact]
    public void EmptyClip_HasZeroDuration()
    {
        Assert.True(AudioClip.Empty.Duration <= TimeSpan.Zero);
        Assert.Empty(AudioClip.Empty.ToWav().Skip(44));
    }
}
