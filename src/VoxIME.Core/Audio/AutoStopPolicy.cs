using VoxIME.Core.Abstractions;

namespace VoxIME.Core.Audio;

/// <summary>录音自动停止原因。</summary>
internal enum AutoStopReason
{
    /// <summary>不需要停止。</summary>
    None,

    /// <summary>达到 RecordingOptions.MaxDurationSeconds。</summary>
    MaxDuration,

    /// <summary>检测到语音之后持续静音达到 RecordingOptions.SilenceTimeoutMs。</summary>
    Silence
}

/// <summary>
/// 录音自动停止策略（纯逻辑，供 <see cref="WasapiAudioCaptureService"/> 与单元测试共用）。
/// </summary>
/// <remarks>
/// <para>规则一：已采集音频时长达到 <see cref="RecordingOptions.MaxDurationSeconds"/> 一定请求停止。</para>
/// <para>规则二：静音自动停止只在本轮"已检测到至少 MinSpeechMs 语音"（hasSpeech）之后才启用 ——
/// 用户按下热键后可能需要 1~2 秒组织语言，没有语音时不应被静音超时提前截断。</para>
/// <para>所有时间都以"已采集到的音频时长"为准（而不是墙钟），避免设备启动延迟造成误差。</para>
/// </remarks>
internal static class AutoStopPolicy
{
    /// <summary>根据当前录音状态判断是否需要请求自动停止。</summary>
    /// <param name="capturedMs">已采集到的音频时长（毫秒）。</param>
    /// <param name="lastVoiceMs">最近一次检测到语音时的音频时间（毫秒）；从未检测到语音时传负数。</param>
    /// <param name="hasSpeech">本轮是否已累计检测到至少 MinSpeechMs 语音（VAD.HasSpeech）。</param>
    /// <param name="options">录音参数。</param>
    public static AutoStopReason Evaluate(double capturedMs, double lastVoiceMs, bool hasSpeech, RecordingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!double.IsFinite(capturedMs) || capturedMs < 0)
        {
            return AutoStopReason.None;
        }

        var maxDurationMs = Math.Clamp(options.MaxDurationSeconds, 1, 600) * 1000.0;
        if (capturedMs >= maxDurationMs)
        {
            return AutoStopReason.MaxDuration;
        }

        if (options.AutoStopOnSilence
            && hasSpeech
            && double.IsFinite(lastVoiceMs)
            && lastVoiceMs >= 0)
        {
            var timeoutMs = Math.Clamp(options.SilenceTimeoutMs, 100, 60_000);
            if (capturedMs - lastVoiceMs >= timeoutMs)
            {
                return AutoStopReason.Silence;
            }
        }

        return AutoStopReason.None;
    }
}
