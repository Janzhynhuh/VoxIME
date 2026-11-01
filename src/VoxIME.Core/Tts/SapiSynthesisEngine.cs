using System.Speech.Synthesis;
using VoxIME.Core.Abstractions;
using VoxIME.Core.Logging;

namespace VoxIME.Core.Tts;

/// <summary>
/// 语音合成引擎抽象：把"文本 → WAV 字节"从播放逻辑里拆出来，
/// 便于在无语音包/无音频设备的机器上做确定性单元测试。
/// </summary>
internal interface ISpeechSynthesisEngine : IDisposable
{
    bool IsAvailable { get; }

    IReadOnlyList<string> GetInstalledVoices();

    /// <summary>同步合成到内存 WAV；失败抛异常（由上层转成 Error 事件）。</summary>
    byte[] SynthesizeToWav(string text, TtsOptions options);
}

/// <summary>基于 System.Speech（SAPI5）的合成引擎。</summary>
internal sealed class SapiSynthesisEngine : ISpeechSynthesisEngine
{
    private readonly IAppLogger _logger;
    private readonly IReadOnlyList<string> _voices;

    public SapiSynthesisEngine(IAppLogger logger)
    {
        _logger = logger ?? NullLogger.Instance;
        try
        {
            using var synthesizer = new SpeechSynthesizer();
            _voices = synthesizer.GetInstalledVoices()
                .Where(voice => voice.Enabled)
                .Select(voice => voice.VoiceInfo.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.Error("初始化 SAPI 语音合成失败（可能没有安装语音包）", ex);
            _voices = Array.Empty<string>();
        }
    }

    public bool IsAvailable => _voices.Count > 0;

    public IReadOnlyList<string> GetInstalledVoices() => _voices;

    public byte[] SynthesizeToWav(string text, TtsOptions options)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<byte>();
        }

        if (!IsAvailable)
        {
            throw new InvalidOperationException("没有安装可用的 SAPI 语音包。");
        }

        // 每次合成创建独立实例：SpeechSynthesizer 是基于 COM 的组件，避免跨线程复用带来的公寓问题。
        using var synthesizer = new SpeechSynthesizer();
        synthesizer.Rate = Math.Clamp(options.Rate, -10, 10);
        synthesizer.Volume = Math.Clamp(options.Volume, 0, 100);

        var voiceName = ResolveVoice(synthesizer, options.VoiceName);
        if (voiceName is not null)
        {
            synthesizer.SelectVoice(voiceName);
        }

        using var stream = new MemoryStream();
        try
        {
            synthesizer.SetOutputToWaveStream(stream);
            synthesizer.Speak(text);
            return stream.ToArray();
        }
        finally
        {
            try
            {
                synthesizer.SetOutputToNull();
            }
            catch (Exception ex)
            {
                _logger.Debug("重置 SAPI 输出目标失败：" + ex.Message);
            }
        }
    }

    public void Dispose()
    {
    }

    private string? ResolveVoice(SpeechSynthesizer synthesizer, string? requestedVoice)
    {
        if (string.IsNullOrWhiteSpace(requestedVoice))
        {
            return null;
        }

        var installed = synthesizer.GetInstalledVoices()
            .Where(voice => voice.Enabled)
            .Select(voice => voice.VoiceInfo.Name);
        foreach (var name in installed)
        {
            if (string.Equals(name, requestedVoice, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        _logger.Warn($"没有找到指定的语音包“{requestedVoice}”，使用系统默认音色。");
        return null;
    }
}
