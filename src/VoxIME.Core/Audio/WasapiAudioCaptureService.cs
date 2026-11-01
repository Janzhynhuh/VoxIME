using NAudio.CoreAudioApi;
using NAudio.Wave;
using VoxIME.Core.Abstractions;
using VoxIME.Core.Logging;

namespace VoxIME.Core.Audio;

/// <summary>
/// 基于 WASAPI 的麦克风采集服务。
/// </summary>
/// <remarks>
/// <para>采集设备原始格式后实时下混为单声道并重采样到 16kHz / PCM16（适配各 ASR 厂商），
/// 约 25Hz 触发 <see cref="LevelChanged"/>。静音自动停止只在本轮已检测到至少 MinSpeechMs 语音之后启用
/// （用户按下热键后组织语言不会被截断）；未检测到语音时只在达到最大时长时触发 <see cref="AutoStopRequested"/>。</para>
/// <para>所有设备/线程异常都会被捕获并通过日志报告；事件在采集回调线程触发，上层自行 Dispatcher。</para>
/// </remarks>
public sealed class WasapiAudioCaptureService : IAudioCaptureService
{
    private const int OutputSampleRate = CapturePipeline.TargetSampleRate;

    /// <summary>约 25Hz 上报一次电平：16000 / 25 = 640 采样。</summary>
    private const int LevelWindowSamples = OutputSampleRate / 25;

    private const int CaptureBufferMs = 40;

    private readonly IAppLogger _logger;
    private readonly object _sync = new();

    /// <summary>串行化开始/停止，避免并发启停把状态机搅乱。</summary>
    private readonly object _lifecycle = new();

    private AudioCaptureState _state = AudioCaptureState.Idle;
    private WasapiCapture? _capture;
    private MMDevice? _device;
    private CapturePipeline? _pipeline;
    private VoiceActivityDetector? _vad;
    private MemoryStream? _pcm;
    private RecordingOptions? _options;

    private double _levelSumSquares;
    private int _levelSampleCount;

    /// <summary>已写入缓冲区的 16kHz 采样数（用音频时间而非墙钟判定超时，避免设备启动延迟造成误差）。</summary>
    private long _capturedSamples;

    /// <summary>最近一次检测到语音时的采样位置，-1 表示还没有说话。</summary>
    private long _lastVoiceAtSamples = -1;

    private bool _autoStopRaised;
    private bool _discard;

    public WasapiAudioCaptureService(IAppLogger logger)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public event EventHandler<AudioLevelEventArgs>? LevelChanged;

    public event EventHandler? AutoStopRequested;

    public AudioCaptureState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public bool IsRecording => State == AudioCaptureState.Capturing;

    /// <summary>枚举当前可用的录音设备（不抛异常；没有设备时返回空列表）。</summary>
    public IReadOnlyList<AudioDeviceInfo> GetDevices()
    {
        var result = new List<AudioDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? defaultId = null;
            try
            {
                using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                defaultId = defaultDevice.ID;
            }
            catch (Exception ex)
            {
                _logger.Debug("获取默认录音设备失败：" + ex.Message);
            }

            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            foreach (var device in devices)
            {
                using (device)
                {
                    string id;
                    string name;
                    try
                    {
                        id = device.ID;
                        name = device.FriendlyName;
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug("读取录音设备信息失败：" + ex.Message);
                        continue;
                    }

                    result.Add(new AudioDeviceInfo
                    {
                        Id = id,
                        Name = string.IsNullOrWhiteSpace(name) ? "未命名麦克风" : name,
                        IsDefault = defaultId != null && string.Equals(id, defaultId, StringComparison.Ordinal)
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error("枚举录音设备失败", ex);
        }

        return result;
    }

    public void Start(RecordingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_lifecycle)
        {
            lock (_sync)
            {
                if (_state is AudioCaptureState.Capturing or AudioCaptureState.Starting)
                {
                    _logger.Warn("麦克风已在录音，忽略重复的启动请求。");
                    return;
                }
            }

            StartCore(options);
        }
    }

    public Task<RecordingResult> StopAsync(CancellationToken cancellationToken = default)
    {
        // 放到线程池执行：停止 WASAPI 采集会等待采集线程退出（几十毫秒），不要阻塞 UI 线程。
        return Task.Run(() => StopCore(discard: false), CancellationToken.None);
    }

    public void Cancel()
    {
        lock (_lifecycle)
        {
            try
            {
                StopCore(discard: true);
            }
            catch (Exception ex)
            {
                _logger.Error("取消录音失败", ex);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lifecycle)
        {
            try
            {
                StopCore(discard: true);
            }
            catch (Exception ex)
            {
                _logger.Warn("释放录音服务时出错：" + ex.Message);
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void StartCore(RecordingOptions options)
    {
        MMDevice? device = null;
        WasapiCapture? capture = null;
        CapturePipeline? pipeline = null;
        MemoryStream? pcm = null;
        try
        {
            device = ResolveDevice(options.DeviceId);
            var captureInstance = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: CaptureBufferMs);
            capture = captureInstance;
            var pipelineInstance = new CapturePipeline(captureInstance.WaveFormat, OutputSampleRate);
            pipeline = pipelineInstance;
            var vad = new VoiceActivityDetector(options.VadThreshold, Math.Max(1, options.MinSpeechMs), OutputSampleRate);
            var estimatedBytes = OutputSampleRate * 2 * Math.Clamp(options.MaxDurationSeconds, 1, 600);
            var pcmInstance = new MemoryStream(estimatedBytes);
            pcm = pcmInstance;

            captureInstance.DataAvailable += OnDataAvailable;
            captureInstance.RecordingStopped += OnRecordingStopped;

            lock (_sync)
            {
                _device = device;
                _capture = captureInstance;
                _pipeline = pipelineInstance;
                _vad = vad;
                _pcm = pcmInstance;
                _options = options;
                _levelSumSquares = 0;
                _levelSampleCount = 0;
                _capturedSamples = 0;
                _lastVoiceAtSamples = -1;
                _autoStopRaised = false;
                _discard = false;
                _state = AudioCaptureState.Capturing;
                device = null;
                capture = null;
                pipeline = null;
                pcm = null;
            }

            // 状态先置为 Capturing 再开始采集，避免设备立刻回调时丢掉第一块数据。
            captureInstance.StartRecording();
            _logger.Info($"开始录音：设备={options.DeviceId ?? "默认"}，最大时长={options.MaxDurationSeconds}s，" +
                         $"静音自动停止={options.AutoStopOnSilence}");
        }
        catch (Exception ex)
        {
            _logger.Error("启动麦克风采集失败", ex);
            DisposeQuietly(capture, device, pipeline, pcm);
            WasapiCapture? cap;
            MMDevice? dev;
            CapturePipeline? pipe;
            MemoryStream? stream;
            lock (_sync)
            {
                DetachAndTakeResourcesLocked(out cap, out dev, out pipe, out stream);
                _state = AudioCaptureState.Failed;
            }

            DisposeQuietly(cap, dev, pipe, stream);
            throw new InvalidOperationException("无法启动麦克风采集：" + ex.Message, ex);
        }
    }

    private MMDevice ResolveDevice(string? deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            try
            {
                return enumerator.GetDevice(deviceId);
            }
            catch (Exception ex)
            {
                _logger.Warn($"找不到指定麦克风（{deviceId}），回退到默认设备：{ex.Message}");
            }
        }

        try
        {
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        }
        catch (Exception ex)
        {
            _logger.Warn("获取默认录音设备失败，尝试多媒体默认设备：" + ex.Message);
        }

        try
        {
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("没有可用的麦克风设备。", ex);
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            float? level = null;
            var autoStop = false;
            lock (_sync)
            {
                if (_state is not (AudioCaptureState.Capturing or AudioCaptureState.Stopping))
                {
                    return;
                }

                var pipeline = _pipeline;
                var vad = _vad;
                var pcm = _pcm;
                var options = _options;
                if (pipeline is null || vad is null || pcm is null || options is null)
                {
                    return;
                }

                pipeline.AddSamples(e.Buffer, e.BytesRecorded);
                var samples = pipeline.ReadAvailable();
                if (samples.Length == 0)
                {
                    return;
                }

                vad.Process(samples);
                var sumSquares = Pcm16Converter.WriteTo(pcm, samples);

                _capturedSamples += samples.Length;
                if (vad.IsSpeaking)
                {
                    _lastVoiceAtSamples = _capturedSamples;
                }

                _levelSumSquares += sumSquares;
                _levelSampleCount += samples.Length;
                if (_levelSampleCount >= LevelWindowSamples)
                {
                    var rms = Math.Sqrt(_levelSumSquares / _levelSampleCount);
                    level = (float)Math.Clamp(rms, 0d, 1d);
                    _levelSumSquares = 0;
                    _levelSampleCount = 0;
                }

                autoStop = EvaluateAutoStopLocked();
            }

            if (level.HasValue)
            {
                RaiseLevel(level.Value);
            }

            if (autoStop)
            {
                RaiseAutoStopRequested();
            }
        }
        catch (Exception ex)
        {
            // 绝不把异常抛回采集线程，避免整机录音线程崩溃。
            _logger.Error("处理麦克风数据时出错", ex);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        try
        {
            bool unexpected;
            lock (_sync)
            {
                unexpected = _state == AudioCaptureState.Capturing;
                if (unexpected)
                {
                    _state = AudioCaptureState.Stopping;
                }
            }

            if (e.Exception is not null)
            {
                _logger.Error("麦克风采集中断", e.Exception);
            }

            if (unexpected)
            {
                _logger.Warn("麦克风采集意外结束，通知上层停止录音。");
                RaiseAutoStopRequested();
            }
        }
        catch (Exception ex)
        {
            _logger.Error("处理麦克风停止事件时出错", ex);
        }
    }

    private bool EvaluateAutoStopLocked()
    {
        if (_autoStopRaised || _options is null)
        {
            return false;
        }

        // 以"已采集到的音频时长"为基准，避免设备启动延迟导致自动停止提前触发。
        var capturedMs = _capturedSamples * 1000.0 / OutputSampleRate;
        var lastVoiceMs = _lastVoiceAtSamples >= 0 ? _lastVoiceAtSamples * 1000.0 / OutputSampleRate : -1;
        var hasSpeech = _vad?.HasSpeech == true;

        var reason = AutoStopPolicy.Evaluate(capturedMs, lastVoiceMs, hasSpeech, _options);
        if (reason == AutoStopReason.None)
        {
            return false;
        }

        _autoStopRaised = true;
        if (reason == AutoStopReason.MaxDuration)
        {
            _logger.Info($"录音达到最大时长 {_options.MaxDurationSeconds}s，请求自动停止。");
        }
        else
        {
            _logger.Debug($"检测到持续静音 {Math.Clamp(_options.SilenceTimeoutMs, 100, 60_000)}ms，请求自动停止。");
        }

        return true;
    }

    private RecordingResult StopCore(bool discard)
    {
        WasapiCapture? capture;
        lock (_sync)
        {
            if (_state != AudioCaptureState.Capturing)
            {
                return CreateEmptyResult();
            }

            _state = AudioCaptureState.Stopping;
            _discard = discard;
            capture = _capture;
        }

        try
        {
            capture?.StopRecording();
        }
        catch (Exception ex)
        {
            _logger.Warn("停止麦克风采集时出错：" + ex.Message);
        }

        RecordingResult result;
        WasapiCapture? cap;
        MMDevice? dev;
        CapturePipeline? pipe;
        MemoryStream? stream;
        lock (_sync)
        {
            result = FinalizeRecordingLocked();
            DetachAndTakeResourcesLocked(out cap, out dev, out pipe, out stream);
            _state = AudioCaptureState.Idle;
        }

        DisposeQuietly(cap, dev, pipe, stream);
        _logger.Info($"录音结束：时长 {result.RecordingDuration.TotalSeconds:0.00}s，" +
                     $"语音 {result.SpeechDuration.TotalSeconds:0.00}s，含有语音={result.HasSpeech}");
        return result;
    }

    private RecordingResult FinalizeRecordingLocked()
    {
        try
        {
            var pipeline = _pipeline;
            if (pipeline is not null)
            {
                var rest = pipeline.ReadAvailable();
                if (rest.Length > 0)
                {
                    _vad?.Process(rest);
                    if (!_discard && _pcm is not null)
                    {
                        Pcm16Converter.WriteTo(_pcm, rest);
                    }
                }
            }

            _vad?.Flush();
        }
        catch (Exception ex)
        {
            _logger.Error("收尾处理残留录音数据失败", ex);
        }

        var vad = _vad;
        var clip = _discard || _pcm is null
            ? AudioClip.Empty
            : AudioClip.FromPcm16(_pcm.ToArray(), OutputSampleRate, 1);

        return new RecordingResult
        {
            Clip = clip,
            HasSpeech = vad?.HasSpeech ?? false,
            RecordingDuration = clip.Duration,
            SpeechDuration = vad?.SpeechDuration ?? TimeSpan.Zero
        };
    }

    private void DetachAndTakeResourcesLocked(
        out WasapiCapture? capture,
        out MMDevice? device,
        out CapturePipeline? pipeline,
        out MemoryStream? pcm)
    {
        capture = _capture;
        device = _device;
        pipeline = _pipeline;
        pcm = _pcm;

        if (capture is not null)
        {
            try
            {
                capture.DataAvailable -= OnDataAvailable;
                capture.RecordingStopped -= OnRecordingStopped;
            }
            catch (Exception ex)
            {
                _logger.Debug("解绑采集事件失败：" + ex.Message);
            }
        }

        _capture = null;
        _device = null;
        _pipeline = null;
        _vad = null;
        _pcm = null;
        _options = null;
        _levelSumSquares = 0;
        _levelSampleCount = 0;
        _capturedSamples = 0;
        _lastVoiceAtSamples = -1;
        _discard = false;
    }

    private static RecordingResult CreateEmptyResult() => new()
    {
        Clip = AudioClip.Empty,
        HasSpeech = false,
        RecordingDuration = TimeSpan.Zero,
        SpeechDuration = TimeSpan.Zero
    };

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
                _logger.Error("电平事件处理器抛出异常", ex);
            }
        }
    }

    private void RaiseAutoStopRequested()
    {
        var handler = AutoStopRequested;
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
                _logger.Error("自动停止事件处理器抛出异常", ex);
            }
        }
    }

    private void DisposeQuietly(params object?[] resources)
    {
        foreach (var resource in resources)
        {
            if (resource is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.Debug("释放音频资源失败：" + ex.Message);
                }
            }
        }
    }
}
