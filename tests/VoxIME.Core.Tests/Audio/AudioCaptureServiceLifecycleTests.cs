using VoxIME.Core.Abstractions;
using VoxIME.Core.Audio;
using VoxIME.Core.Logging;

namespace VoxIME.Core.Tests.Audio;

/// <summary>采集服务生命周期测试：不真正启动麦克风，只验证状态机/枚举/释放路径的健壮性。</summary>
public class AudioCaptureServiceLifecycleTests
{
    [Fact]
    public void 初始状态为空闲()
    {
        var service = new WasapiAudioCaptureService(NullLogger.Instance);

        Assert.Equal(AudioCaptureState.Idle, service.State);
        Assert.False(service.IsRecording);
    }

    [Fact]
    public async Task 未录音时停止返回空结果()
    {
        var service = new WasapiAudioCaptureService(NullLogger.Instance);

        var result = await service.StopAsync();

        Assert.NotNull(result);
        Assert.Empty(result.Clip.Pcm16);
        Assert.False(result.HasSpeech);
        Assert.Equal(TimeSpan.Zero, result.RecordingDuration);
        Assert.Equal(TimeSpan.Zero, result.SpeechDuration);
    }

    [Fact]
    public void 未录音时取消是安全的()
    {
        var service = new WasapiAudioCaptureService(NullLogger.Instance);

        service.Cancel();
        service.Cancel();

        Assert.Equal(AudioCaptureState.Idle, service.State);
    }

    [Fact]
    public void 枚举设备不抛异常且数据字段完整()
    {
        var service = new WasapiAudioCaptureService(NullLogger.Instance);

        var devices = service.GetDevices();

        Assert.NotNull(devices);
        Assert.All(devices, device =>
        {
            Assert.False(string.IsNullOrWhiteSpace(device.Id));
            Assert.False(string.IsNullOrWhiteSpace(device.Name));
        });
        Assert.True(devices.Count(d => d.IsDefault) <= 1);
    }

    [Fact]
    public async Task DisposeAsync可重复调用()
    {
        var service = new WasapiAudioCaptureService(NullLogger.Instance);

        await service.DisposeAsync();
        await service.DisposeAsync();

        Assert.Equal(AudioCaptureState.Idle, service.State);
    }

    [Fact]
    public void 空logger参数被兜底()
    {
        var service = new WasapiAudioCaptureService(null!);

        Assert.Equal(AudioCaptureState.Idle, service.State);
    }
}
