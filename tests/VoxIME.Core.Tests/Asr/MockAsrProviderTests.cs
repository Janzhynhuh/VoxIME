using VoxIME.Core.Abstractions;
using VoxIME.Core.Asr.Providers;
using VoxIME.Core.Configuration;
using VoxIME.Core.Logging;
using Xunit;

namespace VoxIME.Core.Tests.Asr;

public sealed class MockAsrProviderTests
{
    private static MockAsrProvider Create(ProviderProfile profile, FakeAppLogger? logger = null)
    {
        var handler = new FakeHttpMessageHandler(_ =>
            throw new InvalidOperationException("Mock 识别器不应发起网络请求。"));
        return new MockAsrProvider(profile, AsrTestData.HttpClient(handler), logger ?? new FakeAppLogger())
        {
            MinDelay = TimeSpan.Zero,
            MaxDelay = TimeSpan.Zero
        };
    }

    [Fact]
    public async Task 默认文本_包含Mock关键字与音频秒数()
    {
        var provider = Create(AsrTestData.MockProfile());
        var result = await provider.TranscribeAsync(AsrTestData.Silence(2.5), new AsrCallOptions());

        Assert.Contains("Mock", result.Text);
        Assert.Contains("2.5", result.Text);
        Assert.Equal(AsrProviderTypeIds.Mock, result.ProviderId);
        Assert.Equal(2.5, result.AudioDuration.TotalSeconds, 2);
        Assert.Equal("zh", result.DetectedLanguage);
        Assert.Equal(AsrProviderTypeIds.Mock, provider.Descriptor.TypeId);
    }

    [Fact]
    public async Task 服务级ExtraJson可覆盖文本()
    {
        var provider = Create(AsrTestData.MockProfile("{\"text\":\"服务级自定义文本\"}"));
        var result = await provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions());

        Assert.Equal("服务级自定义文本", result.Text);
    }

    [Fact]
    public async Task 调用级ExtraJson优先于服务级ExtraJson()
    {
        var provider = Create(AsrTestData.MockProfile("{\"text\":\"服务级\"}"));
        var result = await provider.TranscribeAsync(
            AsrTestData.Silence(1),
            new AsrCallOptions { ExtraJson = "{\"text\":\"调用级\"}" });

        Assert.Equal("调用级", result.Text);
    }

    [Fact]
    public async Task 默认延迟在300到800毫秒之间()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException());
        var provider = new MockAsrProvider(AsrTestData.MockProfile(), AsrTestData.HttpClient(handler), NullLogger.Instance);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions());
        stopwatch.Stop();

        Assert.InRange(stopwatch.ElapsedMilliseconds, 250, 5000);
    }

    [Fact]
    public async Task 支持取消()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException());
        var provider = new MockAsrProvider(AsrTestData.MockProfile(), AsrTestData.HttpClient(handler), NullLogger.Instance)
        {
            MinDelay = TimeSpan.FromSeconds(5),
            MaxDelay = TimeSpan.FromSeconds(5)
        };
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(100);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions(), cts.Token));
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 3000);
    }

    [Fact]
    public async Task 已取消的Token立即抛出()
    {
        var provider = Create(AsrTestData.MockProfile());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions(), cts.Token));
    }
}
