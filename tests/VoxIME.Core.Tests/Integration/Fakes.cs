using VoxIME.Core.Abstractions;
using VoxIME.Core.Configuration;
using VoxIME.Core.History;
using VoxIME.Core.Logging;

// 这些事件用于实现接口，部分场景不会被测试触发，忽略“事件从未使用”的告警。
#pragma warning disable CS0067

namespace VoxIME.Core.Tests.Integration;

internal sealed class TestConfigStore : IConfigStore
{
    public TestConfigStore(AppConfig? config = null)
    {
        Current = config ?? AppConfig.CreateDefault();
        Current.Normalize();
        ConfigFilePath = Path.Combine(Path.GetTempPath(), "voxime-test-config.json");
    }

    public string ConfigFilePath { get; }
    public AppConfig Current { get; private set; }
    public event EventHandler<AppConfig>? ConfigChanged;

    public AppConfig Load() => Current;

    public void Save(AppConfig config)
    {
        config.Normalize();
        Current = config;
        ConfigChanged?.Invoke(this, Current);
    }

    public void ResetToDefaults() => Save(AppConfig.CreateDefault());

    public string? GetApiKey(ProviderProfile profile) => profile.ApiKey;

    public void SetApiKey(ProviderProfile profile, string? apiKey) => profile.ApiKey = apiKey ?? "";
}

internal sealed class FakeAudioCaptureService : IAudioCaptureService
{
    private readonly List<RecordingOptions> _startedOptions = new();

    public AudioCaptureState State { get; private set; } = AudioCaptureState.Idle;
    public bool IsRecording { get; private set; }
    public bool CancelCalled { get; private set; }
    public int StartCount => _startedOptions.Count;
    public IReadOnlyList<RecordingOptions> StartedOptions => _startedOptions;
    public RecordingResult Result { get; set; } =
        new() { Clip = AudioClip.CreateTone(TimeSpan.FromSeconds(1), 220), HasSpeech = true, SpeechDuration = TimeSpan.FromSeconds(1), RecordingDuration = TimeSpan.FromSeconds(1.2) };

    public event EventHandler<AudioLevelEventArgs>? LevelChanged;
    public event EventHandler? AutoStopRequested;

    public IReadOnlyList<AudioDeviceInfo> GetDevices() => new[]
    {
        new AudioDeviceInfo { Id = "default", Name = "测试麦克风", IsDefault = true }
    };

    public void Start(RecordingOptions options)
    {
        _startedOptions.Add(options);
        IsRecording = true;
        State = AudioCaptureState.Capturing;
    }

    public Task<RecordingResult> StopAsync(CancellationToken cancellationToken = default)
    {
        IsRecording = false;
        State = AudioCaptureState.Idle;
        return Task.FromResult(Result);
    }

    public void Cancel()
    {
        CancelCalled = true;
        IsRecording = false;
        State = AudioCaptureState.Idle;
    }

    public void RaiseLevel(float level) => LevelChanged?.Invoke(this, new AudioLevelEventArgs(level));

    public void RaiseAutoStop() => AutoStopRequested?.Invoke(this, EventArgs.Empty);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeAsrProviderRegistry : IAsrProviderRegistry
{
    public Func<ProviderProfile, AudioClip, AsrCallOptions, CancellationToken, Task<AsrResult>> Transcribe { get; set; } =
        (profile, clip, options, ct) => Task.FromResult(new AsrResult("你好，世界", profile.Id, TimeSpan.FromMilliseconds(120), clip.Duration));

    public int CreateCount { get; private set; }
    public ProviderProfile? LastProfile { get; private set; }
    public List<AsrProviderDescriptor> DescriptorList { get; } = new();

    public IReadOnlyList<AsrProviderDescriptor> Descriptors => DescriptorList;

    public AsrProviderDescriptor? FindDescriptor(string typeId) => DescriptorList.FirstOrDefault(d => d.TypeId == typeId);

    public IAsrProvider? Create(ProviderProfile profile) => CreateOrThrow(profile);

    public IAsrProvider CreateOrThrow(ProviderProfile profile)
    {
        CreateCount++;
        LastProfile = profile;
        return new FakeAsrProvider(profile, this);
    }

    private sealed class FakeAsrProvider : IAsrProvider
    {
        private readonly ProviderProfile _profile;
        private readonly FakeAsrProviderRegistry _owner;

        public FakeAsrProvider(ProviderProfile profile, FakeAsrProviderRegistry owner)
        {
            _profile = profile;
            _owner = owner;
        }

        public AsrProviderDescriptor Descriptor { get; } = new()
        {
            TypeId = AsrProviderTypeIds.Mock,
            DisplayName = "Fake",
            Description = "测试用"
        };

        public Task<AsrResult> TranscribeAsync(AudioClip clip, AsrCallOptions options, CancellationToken cancellationToken = default) =>
            _owner.Transcribe(_profile, clip, options, cancellationToken);
    }
}

internal sealed class FakeTextInjector : ITextInjector
{
    public List<string> InsertedTexts { get; } = new();
    public InsertionResult Result { get; set; } = InsertionResult.Ok(1);

    public Task<InsertionResult> InsertAsync(string text, InsertionConfig options, CancellationToken cancellationToken = default)
    {
        InsertedTexts.Add(text);
        return Task.FromResult(Result);
    }
}

internal sealed class FakeTextPostProcessor : ITextPostProcessor
{
    public string Process(string rawText, InsertionConfig config) => rawText.Trim();
}

internal sealed class FakeTtsService : ITtsService
{
    public bool IsAvailable { get; set; } = true;
    public List<string> SpokenTexts { get; } = new();
    public bool StopCalled { get; private set; }

    public event EventHandler<AudioLevelEventArgs>? LevelChanged;
    public event EventHandler? SpeakingStarted;
    public event EventHandler? SpeakingCompleted;
    public event EventHandler<string>? Error;

    public IReadOnlyList<string> GetInstalledVoices() => new[] { "Fake Voice" };

    public async Task SpeakAsync(string text, TtsOptions options, CancellationToken cancellationToken = default)
    {
        SpokenTexts.Add(text);
        SpeakingStarted?.Invoke(this, EventArgs.Empty);
        LevelChanged?.Invoke(this, new AudioLevelEventArgs(0.4f));
        await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        LevelChanged?.Invoke(this, new AudioLevelEventArgs(0f));
        SpeakingCompleted?.Invoke(this, EventArgs.Empty);
    }

    public void Stop() => StopCalled = true;

    public void Dispose()
    {
    }
}

internal sealed class FakeHotkeyService : IHotkeyService
{
    public bool IsRunning { get; private set; }
    public HotkeyConfig? Config { get; private set; }
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }

    public event EventHandler<HotkeyEventArgs>? HotkeyTriggered;
    public event EventHandler<string>? Error;

    public void Start(HotkeyConfig config)
    {
        Config = config;
        IsRunning = true;
        StartCount++;
    }

    public void Stop()
    {
        IsRunning = false;
        StopCount++;
    }

    public void Update(HotkeyConfig config)
    {
        Config = config;
    }

    public void Raise(HotkeyAction action, string gesture = "Ctrl+Alt") =>
        HotkeyTriggered?.Invoke(this, new HotkeyEventArgs(action, gesture));

    public void Dispose()
    {
        IsRunning = false;
    }
}

internal sealed class FakeLive2DController : ILive2DController
{
    public bool IsAvailable { get; private set; }
    public bool IsVisible { get; private set; }
    public Live2DState State { get; private set; } = Live2DState.Idle;
    public string? LastError { get; private set; }
    public double MouthOpen { get; private set; }
    public (double X, double Y) Gaze { get; private set; }
    public string? BubbleText { get; private set; }
    public List<Live2DState> States { get; } = new();
    public Live2DConfig? Config { get; private set; }
    public bool Initialized { get; private set; }

    public event EventHandler? Ready;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler<Live2DModelSizeEventArgs>? ModelSizeAvailable;

    public void RaiseModelSize(string name, double width, double height) =>
        ModelSizeAvailable?.Invoke(this, new Live2DModelSizeEventArgs(name, width, height));

    public void Initialize(Live2DConfig config)
    {
        Config = config;
        Initialized = true;
        IsAvailable = true;
        Ready?.Invoke(this, EventArgs.Empty);
    }

    public void ApplyConfig(Live2DConfig config) => Config = config;

    public void Show() => IsVisible = true;

    public void Hide() => IsVisible = false;

    public void SetState(Live2DState state)
    {
        State = state;
        States.Add(state);
    }

    public void SetMouthOpen(double value) => MouthOpen = value;

    public void SetGaze(double normalizedX, double normalizedY) => Gaze = (normalizedX, normalizedY);

    public void SetBubbleText(string? text) => BubbleText = text;

    public void Dispose() => IsVisible = false;
}

internal sealed class FakeBeepPlayer : IBeepPlayer
{
    public List<AudioCue> Cues { get; } = new();
    public void Play(AudioCue cue) => Cues.Add(cue);
}

internal sealed class FakeHistoryStore : IHistoryStore
{
    public List<TranscriptionRecord> Records { get; } = new();
    public string FilePath => "memory";
    public event EventHandler? Changed;
    public IReadOnlyList<TranscriptionRecord> Load(int? limit = null) => Records.AsEnumerable().Reverse().ToList();
    public void Add(TranscriptionRecord record) => Records.Add(record);
    public void Clear() => Records.Clear();
}

internal sealed class TestLogger : IAppLogger
{
    public string LogFilePath => "memory";
    public List<string> Messages { get; } = new();
    public void Log(LogLevel level, string message, Exception? exception = null) => Messages.Add($"{level}: {message}");
    public void Trace(string message) => Messages.Add("Trace: " + message);
    public void Debug(string message) => Messages.Add("Debug: " + message);
    public void Info(string message) => Messages.Add("Info: " + message);
    public void Warn(string message) => Messages.Add("Warn: " + message);
    public void Error(string message, Exception? exception = null) => Messages.Add("Error: " + message);
}
