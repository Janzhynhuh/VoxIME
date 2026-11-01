using VoxIME.Core.Abstractions;
using VoxIME.Core.Audio;

namespace VoxIME.Core.Tests.Audio;

/// <summary>自动停止策略测试：纯逻辑，离线确定性。</summary>
public class AutoStopPolicyTests
{
    private static RecordingOptions Options(
        int maxDurationSeconds = 120,
        bool autoStopOnSilence = true,
        int silenceTimeoutMs = 1200) => new()
    {
        MaxDurationSeconds = maxDurationSeconds,
        AutoStopOnSilence = autoStopOnSilence,
        SilenceTimeoutMs = silenceTimeoutMs
    };

    [Fact]
    public void 从未检测到语音时静音超时不提前停止()
    {
        // 按住热键后用户 1~2s 才开口：没有语音时静音超时不能截断录音。
        var options = Options(maxDurationSeconds: 120, autoStopOnSilence: true, silenceTimeoutMs: 300);

        var reason = AutoStopPolicy.Evaluate(capturedMs: 5000, lastVoiceMs: -1, hasSpeech: false, options);

        Assert.Equal(AutoStopReason.None, reason);
    }

    [Fact]
    public void 从未检测到语音时达到最大时长仍会停止()
    {
        var options = Options(maxDurationSeconds: 5, autoStopOnSilence: true, silenceTimeoutMs: 300);

        Assert.Equal(AutoStopReason.None, AutoStopPolicy.Evaluate(4999, -1, false, options));
        Assert.Equal(AutoStopReason.MaxDuration, AutoStopPolicy.Evaluate(5000, -1, false, options));
    }

    [Fact]
    public void 检测到语音之后持续静音达到阈值会停止()
    {
        var options = Options(autoStopOnSilence: true, silenceTimeoutMs: 1200);

        // 语音在 2.0s 结束，安静了 1.2s。
        Assert.Equal(AutoStopReason.Silence, AutoStopPolicy.Evaluate(3200, 2000, true, options));
        // 还差一点。
        Assert.Equal(AutoStopReason.None, AutoStopPolicy.Evaluate(3199, 2000, true, options));
    }

    [Fact]
    public void 检测到语音后开始说话前的空白不会被误算为静音()
    {
        var options = Options(autoStopOnSilence: true, silenceTimeoutMs: 500);

        // 前 2s 没说话（但因为 hasSpeech=false 不会触发），2s 时开始说话。
        var reason = AutoStopPolicy.Evaluate(capturedMs: 2100, lastVoiceMs: 2000, hasSpeech: true, options);

        Assert.Equal(AutoStopReason.None, reason);
    }

    [Fact]
    public void 关闭静音自动停止后只有最大时长会触发()
    {
        var options = Options(maxDurationSeconds: 10, autoStopOnSilence: false, silenceTimeoutMs: 300);

        Assert.Equal(AutoStopReason.None, AutoStopPolicy.Evaluate(5000, 1000, true, options));
        Assert.Equal(AutoStopReason.MaxDuration, AutoStopPolicy.Evaluate(10000, 1000, true, options));
    }

    [Fact]
    public void 静音超时边界按大于等于触发()
    {
        var options = Options(autoStopOnSilence: true, silenceTimeoutMs: 400);

        Assert.Equal(AutoStopReason.None, AutoStopPolicy.Evaluate(1399, 1000, true, options));
        Assert.Equal(AutoStopReason.Silence, AutoStopPolicy.Evaluate(1400, 1000, true, options));
    }

    [Fact]
    public void 静音超时会被钳制到最小100毫秒()
    {
        var options = Options(autoStopOnSilence: true, silenceTimeoutMs: 10);

        Assert.Equal(AutoStopReason.None, AutoStopPolicy.Evaluate(1099, 1000, true, options));
        Assert.Equal(AutoStopReason.Silence, AutoStopPolicy.Evaluate(1100, 1000, true, options));
    }

    [Fact]
    public void 非法采样时间不会触发停止()
    {
        var options = Options();

        Assert.Equal(AutoStopReason.None, AutoStopPolicy.Evaluate(double.NaN, 0, true, options));
        Assert.Equal(AutoStopReason.None, AutoStopPolicy.Evaluate(-1, 0, true, options));
        Assert.Equal(AutoStopReason.None, AutoStopPolicy.Evaluate(2000, double.NaN, true, options));
    }

    [Fact]
    public void 配置为空时抛参数异常()
    {
        Assert.Throws<ArgumentNullException>(() => AutoStopPolicy.Evaluate(1000, 0, true, null!));
    }
}
