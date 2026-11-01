using System.Diagnostics;
using VoxIME.Core.Abstractions;
using VoxIME.Core.Configuration;
using VoxIME.Core.Logging;

namespace VoxIME.Core.Asr.Providers;

/// <summary>
/// 完全离线的 Mock 识别器：模拟 300-800ms 网络延迟，返回可预测的中文文本
/// （包含音频秒数，便于 UI 调试、演示与自动化测试）。可用 ExtraJson {"text":"..."} 覆盖文本。
/// </summary>
public sealed class MockAsrProvider : IAsrProvider
{
    private readonly ProviderProfile _profile;
    private readonly IAppLogger _logger;

    public MockAsrProvider(ProviderProfile profile, HttpClient httpClient, IAppLogger logger)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        ArgumentNullException.ThrowIfNull(httpClient);
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public AsrProviderDescriptor Descriptor { get; } =
        AsrProviderCatalog.GetDescriptor(AsrProviderTypeIds.Mock)
        ?? throw new InvalidOperationException("内置 ASR 目录缺少 mock 描述。");

    /// <summary>最小模拟延迟（默认 300ms；测试可设为 0）。</summary>
    public TimeSpan MinDelay { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>最大模拟延迟（默认 800ms；测试可设为 0）。</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromMilliseconds(800);

    /// <inheritdoc />
    public async Task<AsrResult> TranscribeAsync(
        AudioClip clip,
        AsrCallOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clip);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new AsrCallOptions();

        var stopwatch = Stopwatch.StartNew();
        var delay = NextDelay();
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        var extra = AsrHttpSupport.MergeExtraJson(_profile.ExtraJson, options.ExtraJson, Descriptor.TypeId);
        var customized = AsrHttpSupport.GetString(extra["text"]);
        var text = string.IsNullOrWhiteSpace(customized)
            ? $"你好，这是 VoxIME Mock 识别结果，音频约 {clip.Duration.TotalSeconds:F1} 秒。"
            : customized;
        stopwatch.Stop();

        _logger.Debug($"ASR[{Descriptor.DisplayName}] Mock 识别完成，模拟延迟 {delay.TotalMilliseconds:F0} ms。");
        return new AsrResult(text, Descriptor.TypeId, stopwatch.Elapsed, clip.Duration, "zh");
    }

    private TimeSpan NextDelay()
    {
        var min = (long)Math.Max(0, MinDelay.TotalMilliseconds);
        var max = (long)Math.Max(min, MaxDelay.TotalMilliseconds);
        if (max == min)
        {
            return TimeSpan.FromMilliseconds(min);
        }

        return TimeSpan.FromMilliseconds(Random.Shared.NextInt64(min, max + 1));
    }
}
