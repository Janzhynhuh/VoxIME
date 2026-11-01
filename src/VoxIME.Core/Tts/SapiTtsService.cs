using VoxIME.Core.Abstractions;
using VoxIME.Core.Logging;

namespace VoxIME.Core.Tts;

/// <summary>
/// 基于 Windows SAPI（System.Speech）的语音合成服务：文本 → 内存 WAV → NAudio 播放。
/// </summary>
/// <remarks>
/// <para>播放过程中约 30Hz 通过 <see cref="LevelChanged"/> 上报音量包络，供 Live2D 口型驱动。</para>
/// <para>没有安装语音包时 <see cref="IsAvailable"/> 为 false，<see cref="SpeakAsync"/> 直接返回，绝不抛异常。</para>
/// <para>同一实例可重复调用；新的朗读请求会串行排队，<see cref="Stop"/> 可立即打断当前朗读。</para>
/// <para>事件在后台线程触发，UI 层需要自行 Dispatcher。</para>
/// </remarks>
public sealed class SapiTtsService : ITtsService
{
    private const int LevelIntervalMs = 33;

    private readonly IAppLogger _logger;
    private readonly ISpeechSynthesisEngine _engine;
    private readonly IWavPlayer _player;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();

    private CancellationTokenSource? _currentCts;
    private bool _disposed;

    public SapiTtsService(IAppLogger logger)
        : this(logger, new SapiSynthesisEngine(logger), new NAudioWavPlayerFactory())
    {
    }

    internal SapiTtsService(IAppLogger logger, ISpeechSynthesisEngine engine, IWavPlayerFactory playerFactory)
    {
        _logger = logger ?? NullLogger.Instance;
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        ArgumentNullException.ThrowIfNull(playerFactory);
        _player = playerFactory.Create();
    }

    public event EventHandler<AudioLevelEventArgs>? LevelChanged;

    public event EventHandler? SpeakingStarted;

    public event EventHandler? SpeakingCompleted;

    public event EventHandler<string>? Error;

    public bool IsAvailable
    {
        get
        {
            lock (_sync)
            {
                return !_disposed && _engine.IsAvailable;
            }
        }
    }

    public IReadOnlyList<string> GetInstalledVoices()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return Array.Empty<string>();
            }
        }

        return _engine.GetInstalledVoices();
    }

    public Task SpeakAsync(string text, TtsOptions options, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.CompletedTask;
        }

        if (options is null)
        {
            options = new TtsOptions();
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }
        }

        return SpeakCoreAsync(text, options, cancellationToken);
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_sync)
        {
            cts = _currentCts;
        }

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 朗读恰好已经结束，忽略。
        }

        try
        {
            _player.Stop();
        }
        catch (Exception ex)
        {
            _logger.Debug("停止 TTS 播放失败：" + ex.Message);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Stop();
        try
        {
            _player.Dispose();
        }
        catch (Exception ex)
        {
            _logger.Debug("释放 TTS 播放器失败：" + ex.Message);
        }

        try
        {
            _engine.Dispose();
        }
        catch (Exception ex)
        {
            _logger.Debug("释放 TTS 合成引擎失败：" + ex.Message);
        }

        _gate.Dispose();
    }

    private async Task SpeakCoreAsync(string text, TtsOptions options, CancellationToken cancellationToken)
    {
        if (!_engine.IsAvailable)
        {
            _logger.Warn("没有安装可用的 SAPI 语音包，跳过朗读。");
            return;
        }

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        CancellationTokenSource? linkedCts = null;
        try
        {
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _currentCts = linkedCts;
            }

            byte[] wav;
            try
            {
                wav = await Task.Run(() => _engine.SynthesizeToWav(text, options), linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.Error("语音合成失败", ex);
                RaiseError("语音合成失败：" + ex.Message);
                return;
            }

            if (wav.Length < 44)
            {
                _logger.Warn("语音合成返回了空音频，跳过播放。");
                return;
            }

            if (linkedCts.IsCancellationRequested)
            {
                return;
            }

            var curve = TtsAudioAnalyzer.BuildLevelCurve(wav, LevelIntervalMs);
            var volumeScale = (float)Math.Clamp(options.Volume / 100.0, 0.0, 1.0);

            try
            {
                _player.Start(wav, volumeScale);
            }
            catch (Exception ex)
            {
                // 没有音频输出设备（或设备被独占）时只报错，不抛给调用方。
                _logger.Error("播放语音失败", ex);
                RaiseError("播放语音失败：" + ex.Message);
                return;
            }

            RaiseSpeakingStarted();
            try
            {
                var index = 0;
                while (!linkedCts.IsCancellationRequested && _player.IsPlaying)
                {
                    var level = index < curve.Length ? curve[index] : 0f;
                    RaiseLevel((float)Math.Clamp(level * volumeScale, 0d, 1d));
                    index++;
                    try
                    {
                        await Task.Delay(LevelIntervalMs, linkedCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            finally
            {
                try
                {
                    _player.Stop();
                }
                catch (Exception ex)
                {
                    _logger.Debug("停止 TTS 播放失败：" + ex.Message);
                }

                RaiseSpeakingCompleted();
            }
        }
        catch (OperationCanceledException)
        {
            // 被 Stop/取消打断：静默结束。
        }
        catch (Exception ex)
        {
            _logger.Error("朗读文本失败", ex);
            RaiseError("朗读失败：" + ex.Message);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_currentCts, linkedCts))
                {
                    _currentCts = null;
                }
            }

            linkedCts?.Dispose();
            try
            {
                _gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // 服务恰好被释放，忽略。
            }
        }
    }

    private void RaiseLevel(float level)
    {
        var handler = LevelChanged;
        if (handler is null)
        {
            return;
        }

        var args = new AudioLevelEventArgs(level);
        foreach (var target in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<AudioLevelEventArgs>)target)(this, args);
            }
            catch (Exception ex)
            {
                _logger.Error("TTS 电平事件处理器抛出异常", ex);
            }
        }
    }

    private void RaiseSpeakingStarted()
    {
        var handler = SpeakingStarted;
        if (handler is null)
        {
            return;
        }

        foreach (var target in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler)target)(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _logger.Error("TTS 开始事件处理器抛出异常", ex);
            }
        }
    }

    private void RaiseSpeakingCompleted()
    {
        var handler = SpeakingCompleted;
        if (handler is null)
        {
            return;
        }

        foreach (var target in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler)target)(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _logger.Error("TTS 完成事件处理器抛出异常", ex);
            }
        }
    }

    private void RaiseError(string message)
    {
        var handler = Error;
        if (handler is null)
        {
            return;
        }

        foreach (var target in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<string>)target)(this, message);
            }
            catch (Exception ex)
            {
                _logger.Error("TTS 错误事件处理器抛出异常", ex);
            }
        }
    }
}
