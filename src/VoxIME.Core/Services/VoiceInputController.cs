using System.Diagnostics;
using VoxIME.Core.Abstractions;
using VoxIME.Core.Configuration;
using VoxIME.Core.History;
using VoxIME.Core.Logging;

namespace VoxIME.Core.Services;

/// <summary>
/// 语音输入主流程编排：热键 → 录音 → ASR → 后处理 → 上屏 → 历史 → 朗读 → 桌宠联动。
/// 所有对外事件可能在后台线程触发，UI 层需自行封送到 Dispatcher。
/// </summary>
public sealed class VoiceInputController : IVoiceInputController
{
    private readonly IConfigStore _configStore;
    private readonly IAudioCaptureService _audio;
    private readonly IAsrProviderRegistry _asrRegistry;
    private readonly ITextInjector _injector;
    private readonly ITextPostProcessor _postProcessor;
    private readonly ITtsService _tts;
    private readonly IHotkeyService _hotkeys;
    private readonly ILive2DController _live2d;
    private readonly IBeepPlayer _beep;
    private readonly IHistoryStore _history;
    private readonly IAppLogger _logger;

    private readonly object _lifecycleLock = new();
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _transcribeGate = new(1, 1);

    private AppState _state = AppState.Idle;
    private string? _statusText = "待机";
    private bool _initialized;
    private bool _disposed;
    private bool _isPaused;
    private double _lastModelWidth;
    private double _lastModelHeight;

    public VoiceInputController(
        IConfigStore configStore,
        IAudioCaptureService audio,
        IAsrProviderRegistry asrRegistry,
        ITextInjector injector,
        ITextPostProcessor postProcessor,
        ITtsService tts,
        IHotkeyService hotkeys,
        ILive2DController live2d,
        IBeepPlayer beep,
        IHistoryStore history,
        IAppLogger logger)
    {
        _configStore = configStore;
        _audio = audio;
        _asrRegistry = asrRegistry;
        _injector = injector;
        _postProcessor = postProcessor;
        _tts = tts;
        _hotkeys = hotkeys;
        _live2d = live2d;
        _beep = beep;
        _history = history;
        _logger = logger;
    }

    public AppState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public string? StatusText
    {
        get
        {
            lock (_stateLock)
            {
                return _statusText;
            }
        }
    }

    public AppConfig Config => _configStore.Current;
    public ILive2DController Live2D => _live2d;

    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (_isPaused == value)
            {
                return;
            }

            _isPaused = value;
            if (value)
            {
                if (State == AppState.Listening)
                {
                    Cancel();
                }

                try
                {
                    _hotkeys.Stop();
                }
                catch (Exception ex)
                {
                    _logger.Warn($"暂停热键失败：{ex.Message}");
                }

                SetState(AppState.Disabled, "已暂停 · 热键已关闭");
                _live2d.SetState(Live2DState.Idle);
            }
            else
            {
                try
                {
                    _hotkeys.Start(Config.Hotkeys);
                }
                catch (Exception ex)
                {
                    Fail($"恢复热键失败：{ex.Message}", ex);
                    return;
                }

                SetState(AppState.Idle, BuildIdleStatusText());
            }

            _logger.Info(value ? "已暂停语音输入" : "已恢复语音输入");
        }
    }

    public event EventHandler<AppStateChangedEventArgs>? StateChanged;
    public event EventHandler<TranscriptionRecord>? TranscriptionReceived;
    public event EventHandler<AudioLevelEventArgs>? MicrophoneLevelChanged;
    public event EventHandler<string>? ErrorOccurred;

    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _logger.Info("VoiceInputController 初始化");

        _hotkeys.HotkeyTriggered += OnHotkeyTriggered;
        _hotkeys.Error += OnHotkeyError;
        _audio.LevelChanged += OnMicrophoneLevel;
        _audio.AutoStopRequested += OnAutoStopRequested;
        _tts.LevelChanged += OnTtsLevelChanged;
        _tts.Error += OnTtsError;
        _live2d.ErrorOccurred += OnLive2DError;
        _live2d.Ready += OnLive2DReady;
        _live2d.ModelSizeAvailable += OnLive2DModelSizeAvailable;

        var config = _configStore.Current;
        config.Normalize();

        try
        {
            _live2d.Initialize(config.Live2D);
            _live2d.SetState(Live2DState.Idle);
            if (config.Live2D.Enabled)
            {
                _live2d.Show();
            }
            else
            {
                _live2d.Hide();
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Live2D 初始化失败：{ex.Message}");
        }

        try
        {
            _hotkeys.Start(config.Hotkeys);
        }
        catch (Exception ex)
        {
            _logger.Error("全局热键启动失败", ex);
            ErrorOccurred?.Invoke(this, $"全局热键不可用：{ex.Message}");
        }

        SetState(AppState.Idle, BuildIdleStatusText());
    }

    public void ApplyConfig(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Normalize();
        ApplyAutoFitWidth(config);

        try
        {
            if (!_isPaused)
            {
                _hotkeys.Update(config.Hotkeys);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"更新热键配置失败：{ex.Message}");
        }

        try
        {
            _live2d.ApplyConfig(config.Live2D);
            if (config.Live2D.Enabled)
            {
                _live2d.Show();
            }
            else
            {
                _live2d.Hide();
            }

            _live2d.SetState(ToLive2DState(State));
        }
        catch (Exception ex)
        {
            _logger.Warn($"应用 Live2D 配置失败：{ex.Message}");
        }

        SetStatus(StatusText, raiseEvent: true);
        _logger.Info("配置已应用");
    }

    public bool StartRecording()
    {
        if (_disposed)
        {
            return false;
        }

        lock (_lifecycleLock)
        {
            if (_isPaused)
            {
                return false;
            }

            if (State is AppState.Listening or AppState.Recognizing or AppState.Speaking)
            {
                return false;
            }

            if (!_audio.IsRecording)
            {
                try
                {
                    var audioConfig = Config.Audio;
                    var options = new RecordingOptions
                    {
                        DeviceId = audioConfig.DeviceId,
                        SampleRate = 16000,
                        Channels = 1,
                        MaxDurationSeconds = audioConfig.MaxDurationSeconds,
                        AutoStopOnSilence = audioConfig.AutoStopOnSilence,
                        SilenceTimeoutMs = audioConfig.SilenceTimeoutMs,
                        MinSpeechMs = audioConfig.MinSpeechMs,
                        VadThreshold = audioConfig.VadThreshold
                    };
                    _audio.Start(options);
                }
                catch (Exception ex)
                {
                    Fail($"无法启动麦克风：{ex.Message}", ex);
                    return false;
                }
            }

            if (Config.Audio.PlayCues)
            {
                SafeBeep(AudioCue.Start);
            }

            SetState(AppState.Listening, "聆听中…");
            _live2d.SetState(Live2DState.Listening);
            _live2d.SetMouthOpen(0);
            _live2d.SetBubbleText(null);
            return true;
        }
    }

    public async Task<TranscriptionRecord?> StopAndTranscribeAsync(CancellationToken cancellationToken = default)
    {
        if (State != AppState.Listening)
        {
            return null;
        }

        SetState(AppState.Recognizing, "识别中…");
        _live2d.SetState(Live2DState.Thinking);
        _live2d.SetMouthOpen(0);
        if (Config.Audio.PlayCues)
        {
            SafeBeep(AudioCue.Stop);
        }

        RecordingResult recording;
        try
        {
            recording = await _audio.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            SetState(AppState.Idle, BuildIdleStatusText());
            return null;
        }
        catch (Exception ex)
        {
            Fail($"结束录音失败：{ex.Message}", ex);
            return null;
        }

        if (recording.Clip.Pcm16.Length == 0)
        {
            SetState(AppState.Idle, "没有采集到音频");
            return null;
        }

        if (!recording.HasSpeech)
        {
            var threshold = Config.Audio.VadThreshold;
            SetState(AppState.Idle,
                $"没有检测到语音（输入峰值 {recording.PeakLevel:P1}，检测阈值 {threshold:P1}）；" +
                "请靠近麦克风再试，或在 设置 → 录音 里降低「语音检测阈值」");
            return null;
        }

        return await RecognizeAndInsertAsync(recording, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TranscriptionRecord?> ToggleRecordingAsync(CancellationToken cancellationToken = default)
    {
        if (State == AppState.Listening)
        {
            return await StopAndTranscribeAsync(cancellationToken).ConfigureAwait(false);
        }

        return StartRecording() ? null : null;
    }

    public void Cancel()
    {
        try
        {
            _audio.Cancel();
        }
        catch (Exception ex)
        {
            _logger.Warn($"取消录音失败：{ex.Message}");
        }

        try
        {
            _tts.Stop();
        }
        catch (Exception ex)
        {
            _logger.Warn($"停止朗读失败：{ex.Message}");
        }

        _live2d.SetMouthOpen(0);
        _live2d.SetBubbleText(null);
        if (State is AppState.Listening or AppState.Recognizing or AppState.Speaking)
        {
            SetState(AppState.Idle, BuildIdleStatusText());
            _logger.Info("已取消当前语音输入");
        }
    }

    public async Task<AsrResult> TranscribeAsync(AudioClip clip, ProviderProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(profile);

        if (clip.Pcm16.Length == 0)
        {
            throw new AsrException("音频为空，无法识别", profile.TypeId);
        }

        var provider = _asrRegistry.CreateOrThrow(profile);
        await _transcribeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(profile.TimeoutSeconds, 5, 600)));
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var options = new AsrCallOptions
                {
                    Language = string.IsNullOrWhiteSpace(profile.Language) ? "auto" : profile.Language,
                    Prompt = profile.Prompt,
                    ExtraJson = profile.ExtraJson
                };
                var result = await provider.TranscribeAsync(clip, options, timeoutCts.Token).ConfigureAwait(false);
                _logger.Info($"识别完成：{profile.Name}，用时 {stopwatch.ElapsedMilliseconds}ms，文本长度 {result.Text.Length}");
                return result with
                {
                    Latency = stopwatch.Elapsed,
                    AudioDuration = clip.Duration
                };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AsrException($"识别超时（{profile.TimeoutSeconds} 秒）", profile.TypeId);
            }
            catch (AsrException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new AsrException($"识别失败：{ex.Message}", profile.TypeId, innerException: ex);
            }
        }
        finally
        {
            _transcribeGate.Release();
        }
    }

    public async Task<AsrResult> TestProviderAsync(ProviderProfile profile, int seconds = 3, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (_audio.IsRecording)
        {
            throw new AsrException("当前正在录音，请先结束录音再测试", profile.TypeId);
        }

        var recordSeconds = Math.Clamp(seconds, 1, 10);
        SetState(AppState.Listening, $"测试录音 {recordSeconds} 秒…");
        try
        {
            _audio.Start(new RecordingOptions
            {
                DeviceId = Config.Audio.DeviceId,
                SampleRate = 16000,
                Channels = 1,
                MaxDurationSeconds = recordSeconds + 3,
                AutoStopOnSilence = false,
                SilenceTimeoutMs = Config.Audio.SilenceTimeoutMs,
                MinSpeechMs = Config.Audio.MinSpeechMs,
                VadThreshold = Config.Audio.VadThreshold
            });

            await Task.Delay(TimeSpan.FromSeconds(recordSeconds), cancellationToken).ConfigureAwait(false);
            var recording = await _audio.StopAsync(cancellationToken).ConfigureAwait(false);
            if (recording.Clip.Pcm16.Length == 0)
            {
                throw new AsrException("没有采集到音频，请检查麦克风设备和系统权限", profile.TypeId);
            }

            SetState(AppState.Recognizing, "测试识别中…");
            return await TranscribeAsync(recording.Clip, profile, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not AsrException)
        {
            throw new AsrException($"测试失败：{ex.Message}", profile.TypeId, innerException: ex);
        }
        finally
        {
            if (State is AppState.Listening or AppState.Recognizing)
            {
                SetState(AppState.Idle, BuildIdleStatusText());
            }
        }
    }

    public async Task<InsertionResult> InsertTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
        {
            return InsertionResult.Ok(0);
        }

        try
        {
            var result = await _injector.InsertAsync(text, Config.Insertion, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                _logger.Warn($"文本上屏失败：{result.Error}");
                ErrorOccurred?.Invoke(this, result.Error ?? "文本上屏失败");
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.Error("文本上屏异常", ex);
            return InsertionResult.Fail($"文本上屏异常：{ex.Message}");
        }
    }

    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!_tts.IsAvailable || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var previousState = State;
        SetState(AppState.Speaking, "朗读中…");
        _live2d.SetState(Live2DState.Speaking);
        _live2d.SetBubbleText(Truncate(text, 90));
        try
        {
            await _tts.SpeakAsync(text, new TtsOptions
            {
                VoiceName = Config.Tts.VoiceName,
                Rate = Config.Tts.Rate,
                Volume = Config.Tts.Volume
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 用户取消朗读。
        }
        catch (Exception ex)
        {
            _logger.Warn($"朗读失败：{ex.Message}");
        }
        finally
        {
            _live2d.SetMouthOpen(0);
            if (State == AppState.Speaking)
            {
                SetState(previousState == AppState.Speaking ? AppState.Idle : AppState.Idle, BuildIdleStatusText());
                _live2d.SetState(Live2DState.Idle);
            }
        }
    }

    public IReadOnlyList<AudioDeviceInfo> GetInputDevices()
    {
        try
        {
            return _audio.GetDevices();
        }
        catch (Exception ex)
        {
            _logger.Warn($"枚举麦克风失败：{ex.Message}");
            return Array.Empty<AudioDeviceInfo>();
        }
    }

    public IReadOnlyList<string> GetTtsVoices()
    {
        try
        {
            return _tts.GetInstalledVoices();
        }
        catch (Exception ex)
        {
            _logger.Warn($"枚举语音包失败：{ex.Message}");
            return Array.Empty<string>();
        }
    }

    private async Task<TranscriptionRecord?> RecognizeAndInsertAsync(RecordingResult recording, CancellationToken cancellationToken)
    {
        var config = Config;
        var profile = config.GetActiveProfile();
        if (profile is null)
        {
            Fail("尚未配置语音识别服务，请到「设置 → 服务」添加", null);
            return null;
        }

        AsrResult asrResult;
        try
        {
            _live2d.SetBubbleText("正在识别…");
            asrResult = await TranscribeAsync(recording.Clip, profile, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var failed = new TranscriptionRecord
            {
                Text = "",
                ProviderName = profile.Name,
                ProviderTypeId = profile.TypeId,
                AudioDuration = recording.Clip.Duration,
                Error = ex.Message
            };
            _history.Add(failed);
            Fail(DescribeFailure(ex), ex);
            return failed;
        }

        var text = _postProcessor.Process(asrResult.Text ?? "", config.Insertion);
        if (string.IsNullOrWhiteSpace(text))
        {
            SetState(AppState.Idle, "没有识别到内容");
            _live2d.SetState(Live2DState.Idle);
            _live2d.SetBubbleText(null);
            return null;
        }

        var record = new TranscriptionRecord
        {
            Text = text,
            ProviderName = profile.Name,
            ProviderTypeId = profile.TypeId,
            AudioDuration = asrResult.AudioDuration,
            Latency = asrResult.Latency
        };

        _live2d.SetBubbleText(Truncate(text, 90));

        try
        {
            TranscriptionReceived?.Invoke(this, record);
        }
        catch (Exception ex)
        {
            _logger.Warn($"TranscriptionReceived 事件处理异常：{ex.Message}");
        }

        if (config.Insertion.AutoInsert && !config.Insertion.PreviewBeforeInsert)
        {
            var insertion = await InsertTextAsync(text, cancellationToken).ConfigureAwait(false);
            record.Inserted = insertion.Success;
        }

        _history.Add(record);

        if (config.Tts.Enabled && config.Tts.AutoSpeakResult)
        {
            await SpeakAsync(text, cancellationToken).ConfigureAwait(false);
        }
        else if (State != AppState.Disabled)
        {
            SetState(AppState.Idle, BuildIdleStatusText());
            _live2d.SetState(Live2DState.Idle);
        }

        return record;
    }

    private void OnHotkeyTriggered(object? sender, HotkeyEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        var hotkeys = Config.Hotkeys;
        switch (e.Action)
        {
            case HotkeyAction.PushToTalkPressed when hotkeys.EnablePushToTalk:
                StartRecording();
                break;
            case HotkeyAction.PushToTalkReleased when hotkeys.EnablePushToTalk:
                if (State == AppState.Listening)
                {
                    _ = SafeStopAsync();
                }

                break;
            case HotkeyAction.TogglePressed when hotkeys.EnableToggle:
                if (State == AppState.Listening)
                {
                    _ = SafeStopAsync();
                }
                else
                {
                    StartRecording();
                }

                break;
            case HotkeyAction.CancelPressed when State is AppState.Listening or AppState.Recognizing or AppState.Speaking:
                Cancel();
                break;
        }
    }

    private void OnAutoStopRequested(object? sender, EventArgs e)
    {
        if (State == AppState.Listening)
        {
            _logger.Debug("检测到静音/超时，自动结束录音");
            _ = SafeStopAsync();
        }
    }

    private async Task SafeStopAsync()
    {
        try
        {
            await StopAndTranscribeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Fail(DescribeFailure(ex), ex);
        }
    }

    private void OnMicrophoneLevel(object? sender, AudioLevelEventArgs e)
    {
        try
        {
            MicrophoneLevelChanged?.Invoke(this, e);
        }
        catch (Exception ex)
        {
            _logger.Warn($"麦克风电平事件处理异常：{ex.Message}");
        }

        if (State == AppState.Listening && Config.Live2D.MouthSync)
        {
            _live2d.SetMouthOpen(ToMouthOpen(e.Level, 3.0));
        }
    }

    private void OnTtsLevelChanged(object? sender, AudioLevelEventArgs e)
    {
        if (State == AppState.Speaking && Config.Tts.MouthSync && Config.Live2D.MouthSync)
        {
            _live2d.SetMouthOpen(ToMouthOpen(e.Level, 1.6));
        }
    }

    private void OnHotkeyError(object? sender, string message)
    {
        _logger.Warn($"热键服务错误：{message}");
        ErrorOccurred?.Invoke(this, $"全局热键不可用：{message}");
    }

    private void OnTtsError(object? sender, string message)
    {
        _logger.Warn($"TTS 错误：{message}");
    }

    private void OnLive2DError(object? sender, string message)
    {
        _logger.Warn($"Live2D 错误：{message}");
    }

    private void OnLive2DReady(object? sender, EventArgs e)
    {
        _logger.Info("Live2D 已就绪");
        _live2d.SetState(ToLive2DState(State));
    }

    /// <summary>
    /// 模型加载后把窗口宽度收紧到模型显示宽度 + 左右各 5 DIP padding，
    /// 高度沿用当前配置（可通过开关关闭）。保存到配置并立即应用到窗口，重启后保持一致。
    /// </summary>
    private void OnLive2DModelSizeAvailable(object? sender, Live2DModelSizeEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _lastModelWidth = e.Width;
            _lastModelHeight = e.Height;
            var config = _configStore.Current;
            if (!config.Live2D.AutoFitWindow)
            {
                return;
            }

            var currentWidth = (int)Math.Round(config.Live2D.Width);
            var currentHeight = (int)Math.Round(config.Live2D.Height);
            var (width, height) = Live2DWindowSizing.FitToModel(
                currentWidth,
                currentHeight,
                e.Width,
                e.Height,
                config.Live2D.Scale);
            if (width == currentWidth && height == currentHeight)
            {
                return;
            }

            config.Live2D.Width = width;
            config.Live2D.Height = height;
            _configStore.Save(config);
            _live2d.ApplyConfig(config.Live2D);
            _logger.Info($"[Live2D] 已按模型紧贴窗口宽度：{width}×{height}（模型原始 {e.Width:0}×{e.Height:0}，缩放 {config.Live2D.Scale:0.##}）");
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Live2D] 按模型紧贴窗口宽度失败：{ex.Message}");
        }
    }

    /// <summary>配置变化（高度/缩放/开关）时，按上次模型尺寸重新收紧窗口宽度。</summary>
    private void ApplyAutoFitWidth(AppConfig config)
    {
        if (!config.Live2D.AutoFitWindow || _lastModelWidth <= 0 || _lastModelHeight <= 0)
        {
            return;
        }

        try
        {
            var currentWidth = (int)Math.Round(config.Live2D.Width);
            var currentHeight = (int)Math.Round(config.Live2D.Height);
            var (width, height) = Live2DWindowSizing.FitToModel(
                currentWidth,
                currentHeight,
                _lastModelWidth,
                _lastModelHeight,
                config.Live2D.Scale);
            if (width == currentWidth && height == currentHeight)
            {
                return;
            }

            config.Live2D.Width = width;
            config.Live2D.Height = height;
            _configStore.Save(config);
            _logger.Info($"[Live2D] 高度/缩放变化后重新收紧窗口宽度：{width}×{height}");
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Live2D] 重新收紧窗口宽度失败：{ex.Message}");
        }
    }

    private double ToMouthOpen(float level, double gain) =>
        Math.Clamp(level * gain * Config.Live2D.MouthSensitivity, 0, 1);

    private void SetState(AppState state, string? statusText)
    {
        AppState previous;
        lock (_stateLock)
        {
            previous = _state;
            _state = state;
            _statusText = statusText;
        }

        if (previous != state || !string.Equals(StatusText, statusText, StringComparison.Ordinal))
        {
            try
            {
                StateChanged?.Invoke(this, new AppStateChangedEventArgs(state, previous, statusText));
            }
            catch (Exception ex)
            {
                _logger.Warn($"状态事件处理异常：{ex.Message}");
            }
        }

        try
        {
            _live2d.SetState(ToLive2DState(state));
            if (state == AppState.Idle || state == AppState.Disabled)
            {
                _live2d.SetMouthOpen(0);
                _live2d.SetBubbleText(null);
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"同步 Live2D 状态失败：{ex.Message}");
        }
    }

    private void SetStatus(string? statusText, bool raiseEvent)
    {
        string? previous;
        lock (_stateLock)
        {
            previous = _statusText;
            _statusText = statusText;
        }

        if (raiseEvent && !string.Equals(previous, statusText, StringComparison.Ordinal))
        {
            StateChanged?.Invoke(this, new AppStateChangedEventArgs(State, State, statusText));
        }
    }

    private void Fail(string userMessage, Exception? exception)
    {
        _logger.Error($"语音输入失败：{userMessage}", exception);
        try
        {
            ErrorOccurred?.Invoke(this, userMessage);
        }
        catch (Exception ex)
        {
            _logger.Warn($"错误事件处理异常：{ex.Message}");
        }

        _live2d.SetState(Live2DState.Error);
        _live2d.SetBubbleText(Truncate(userMessage, 70));
        SetState(AppState.Error, userMessage);
        _ = ResetErrorStateAsync();
    }

    private async Task ResetErrorStateAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(3.5)).ConfigureAwait(false);
        if (State == AppState.Error)
        {
            SetState(AppState.Idle, BuildIdleStatusText());
            _live2d.SetState(Live2DState.Idle);
            _live2d.SetBubbleText(null);
        }
    }

    private void SafeBeep(AudioCue cue)
    {
        try
        {
            _beep.Play(cue);
        }
        catch (Exception ex)
        {
            _logger.Debug($"提示音播放失败：{ex.Message}");
        }
    }

    private string BuildIdleStatusText()
    {
        var config = Config;
        if (_isPaused)
        {
            return "已暂停";
        }

        return config.Hotkeys.DefaultMode == RecordingMode.Toggle
            ? $"待机 · 按 {config.Hotkeys.Toggle} 开始/结束"
            : $"待机 · 按住 {config.Hotkeys.PushToTalk} 说话";
    }

    private static string DescribeFailure(Exception ex) => ex switch
    {
        AsrException asr => asr.Message,
        OperationCanceledException => "操作已取消",
        _ => $"处理失败：{ex.Message}"
    };

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text[..maxLength] + "…";
    }

    private static Live2DState ToLive2DState(AppState state) => state switch
    {
        AppState.Listening => Live2DState.Listening,
        AppState.Recognizing => Live2DState.Thinking,
        AppState.Speaking => Live2DState.Speaking,
        AppState.Error => Live2DState.Error,
        _ => Live2DState.Idle
    };

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _logger.Info("VoiceInputController 释放");

        try
        {
            _hotkeys.HotkeyTriggered -= OnHotkeyTriggered;
            _hotkeys.Error -= OnHotkeyError;
            _audio.LevelChanged -= OnMicrophoneLevel;
            _audio.AutoStopRequested -= OnAutoStopRequested;
            _tts.LevelChanged -= OnTtsLevelChanged;
            _tts.Error -= OnTtsError;
            _live2d.ErrorOccurred -= OnLive2DError;
            _live2d.Ready -= OnLive2DReady;
            _live2d.ModelSizeAvailable -= OnLive2DModelSizeAvailable;

            _hotkeys.Stop();
            _hotkeys.Dispose();
            _tts.Dispose();
            _live2d.Dispose();
            await _audio.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warn($"释放资源时出错：{ex.Message}");
        }
        finally
        {
            _transcribeGate.Dispose();
        }
    }
}
