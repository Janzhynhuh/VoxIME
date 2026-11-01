using VoxIME.Core.Abstractions;
using VoxIME.Core.Configuration;
using VoxIME.Core.Services;

namespace VoxIME.Core.Tests.Integration;

public sealed class VoiceInputControllerTests
{
    private readonly TestConfigStore _configStore = new();
    private readonly FakeAudioCaptureService _audio = new();
    private readonly FakeAsrProviderRegistry _asr = new();
    private readonly FakeTextInjector _injector = new();
    private readonly FakeTextPostProcessor _postProcessor = new();
    private readonly FakeTtsService _tts = new();
    private readonly FakeHotkeyService _hotkeys = new();
    private readonly FakeLive2DController _live2d = new();
    private readonly FakeBeepPlayer _beep = new();
    private readonly FakeHistoryStore _history = new();
    private readonly TestLogger _logger = new();
    private readonly VoiceInputController _controller;

    public VoiceInputControllerTests()
    {
        _controller = new VoiceInputController(
            _configStore, _audio, _asr, _injector, _postProcessor,
            _tts, _hotkeys, _live2d, _beep, _history, _logger);
    }

    [Fact]
    public void Initialize_StartsHotkeysAndLive2D()
    {
        _controller.Initialize();

        Assert.True(_hotkeys.IsRunning);
        Assert.True(_live2d.Initialized);
        Assert.Equal(AppState.Idle, _controller.State);
        Assert.Contains("Ctrl+Alt", _controller.StatusText);
        Assert.Equal(Live2DState.Idle, _live2d.State);
    }

    [Fact]
    public void ModelSizeAvailable_AutoFitsWindowAndPersists()
    {
        _controller.Initialize();
        _configStore.Current.Live2D.Width = 380;
        _configStore.Current.Live2D.Height = 560;
        _configStore.Current.Live2D.AutoFitWindow = true;

        _live2d.RaiseModelSize("tall-model", 1000, 2000);

        Assert.Equal(276, _configStore.Current.Live2D.Width);
        Assert.Equal(560, _configStore.Current.Live2D.Height);
        Assert.Equal(276, _live2d.Config!.Width);
        Assert.Equal(560, _live2d.Config.Height);
    }

    [Fact]
    public void ModelSizeAvailable_DoesNotResize_WhenAutoFitDisabled()
    {
        _controller.Initialize();
        _configStore.Current.Live2D.Width = 380;
        _configStore.Current.Live2D.Height = 560;
        _configStore.Current.Live2D.AutoFitWindow = false;

        _live2d.RaiseModelSize("tall-model", 1000, 2000);

        Assert.Equal(380, _configStore.Current.Live2D.Width);
        Assert.Equal(560, _configStore.Current.Live2D.Height);
    }

    [Fact]
    public void ApplyConfig_RecomputesWidth_WhenScaleChanges()
    {
        _controller.Initialize();
        _configStore.Current.Live2D.Width = 380;
        _configStore.Current.Live2D.Height = 560;
        _configStore.Current.Live2D.AutoFitWindow = true;
        _live2d.RaiseModelSize("tall-model", 1000, 2000);
        Assert.Equal(276, _configStore.Current.Live2D.Width);

        _configStore.Current.Live2D.Scale = 1.5;
        _controller.ApplyConfig(_configStore.Current);

        Assert.Equal(409, _configStore.Current.Live2D.Width); // 1000 * (560/2000 * 0.95 * 1.5) + 10
        Assert.Equal(560, _configStore.Current.Live2D.Height);
        Assert.Equal(409, _live2d.Config!.Width);
    }

    [Fact]
    public void StartRecording_EntersListeningAndStartsAudio()
    {
        _controller.Initialize();

        var started = _controller.StartRecording();

        Assert.True(started);
        Assert.Equal(AppState.Listening, _controller.State);
        Assert.True(_audio.IsRecording);
        Assert.Equal(16000, _audio.StartedOptions[0].SampleRate);
        Assert.Equal(1, _audio.StartedOptions[0].Channels);
        Assert.Contains(AudioCue.Start, _beep.Cues);
        Assert.Equal(Live2DState.Listening, _live2d.State);
    }

    [Fact]
    public async Task StopAndTranscribe_InsertsText_AddsHistory_AndRaisesEvent()
    {
        _controller.Initialize();
        _controller.StartRecording();
        TranscriptionRecord? received = null;
        _controller.TranscriptionReceived += (_, record) => received = record;

        var record = await _controller.StopAndTranscribeAsync();

        Assert.NotNull(record);
        Assert.NotNull(received);
        Assert.Equal("你好，世界", record!.Text);
        Assert.True(record.Inserted);
        Assert.Single(_injector.InsertedTexts);
        Assert.Equal("你好，世界", _injector.InsertedTexts[0]);
        Assert.Single(_history.Records);
        Assert.Equal(AppState.Idle, _controller.State);
        Assert.Equal(1, _asr.CreateCount);
        Assert.Contains(AudioCue.Stop, _beep.Cues);
    }

    [Fact]
    public async Task StopAndTranscribe_NoSpeech_DoesNotCallProvider()
    {
        _controller.Initialize();
        _controller.StartRecording();
        _audio.Result = new RecordingResult
        {
            Clip = AudioClip.CreateSilence(TimeSpan.FromSeconds(1)),
            HasSpeech = false,
            RecordingDuration = TimeSpan.FromSeconds(1)
        };

        var record = await _controller.StopAndTranscribeAsync();

        Assert.Null(record);
        Assert.Equal(0, _asr.CreateCount);
        Assert.Equal(AppState.Idle, _controller.State);
    }

    [Fact]
    public async Task StopAndTranscribe_ProviderFailure_EntersErrorAndRecordsHistory()
    {
        _controller.Initialize();
        _controller.StartRecording();
        _asr.Transcribe = (_, _, _, _) => throw new AsrException("余额不足", AsrProviderTypeIds.OpenAiTranscription, 402, "insufficient");
        string? error = null;
        _controller.ErrorOccurred += (_, message) => error = message;

        var record = await _controller.StopAndTranscribeAsync();

        Assert.NotNull(record);
        Assert.NotNull(record!.Error);
        Assert.Contains("余额不足", record.Error);
        Assert.Single(_history.Records);
        Assert.Equal(AppState.Error, _controller.State);
        Assert.NotNull(error);
        Assert.Equal(Live2DState.Error, _live2d.State);
    }

    [Fact]
    public async Task PreviewMode_DoesNotInsertAutomatically()
    {
        _configStore.Current.Insertion.AutoInsert = true;
        _configStore.Current.Insertion.PreviewBeforeInsert = true;
        _controller.Initialize();
        _controller.StartRecording();

        var record = await _controller.StopAndTranscribeAsync();

        Assert.NotNull(record);
        Assert.False(record!.Inserted);
        Assert.Empty(_injector.InsertedTexts);
    }

    [Fact]
    public async Task AutoInsertDisabled_DoesNotInsert()
    {
        _configStore.Current.Insertion.AutoInsert = false;
        _controller.Initialize();
        _controller.StartRecording();

        var record = await _controller.StopAndTranscribeAsync();

        Assert.NotNull(record);
        Assert.Empty(_injector.InsertedTexts);
    }

    [Fact]
    public async Task TtsEnabled_AutoSpeakResult_ReadsRecognizedText()
    {
        _configStore.Current.Tts.Enabled = true;
        _configStore.Current.Tts.AutoSpeakResult = true;
        _controller.Initialize();
        _controller.StartRecording();

        await _controller.StopAndTranscribeAsync();

        Assert.Single(_tts.SpokenTexts);
        Assert.Equal("你好，世界", _tts.SpokenTexts[0]);
        Assert.Equal(AppState.Idle, _controller.State);
    }

    [Fact]
    public void PushToTalkHotkey_StartsAndStopsRecording()
    {
        _controller.Initialize();

        _hotkeys.Raise(HotkeyAction.PushToTalkPressed);
        Assert.Equal(AppState.Listening, _controller.State);

        _hotkeys.Raise(HotkeyAction.PushToTalkReleased);
        Assert.True(WaitUntil(() => _controller.State == AppState.Idle, TimeSpan.FromSeconds(3)));
        Assert.Single(_injector.InsertedTexts);
    }

    [Fact]
    public void ToggleHotkey_TogglesRecording()
    {
        _controller.Initialize();

        _hotkeys.Raise(HotkeyAction.TogglePressed);
        Assert.Equal(AppState.Listening, _controller.State);

        _hotkeys.Raise(HotkeyAction.TogglePressed);
        Assert.True(WaitUntil(() => _controller.State == AppState.Idle, TimeSpan.FromSeconds(3)));
        Assert.Single(_injector.InsertedTexts);
    }

    [Fact]
    public void CancelWhileListening_StopsAndResetsState()
    {
        _controller.Initialize();
        _controller.StartRecording();

        _controller.Cancel();

        Assert.True(_audio.CancelCalled);
        Assert.Equal(AppState.Idle, _controller.State);
        Assert.Equal(Live2DState.Idle, _live2d.State);
    }

    [Fact]
    public void Pause_StopsHotkeysAndBlocksRecording()
    {
        _controller.Initialize();

        _controller.IsPaused = true;

        Assert.False(_controller.StartRecording());
        Assert.Equal(AppState.Disabled, _controller.State);
        Assert.False(_hotkeys.IsRunning);
    }

    [Fact]
    public void AutoStopRequested_StopsRecordingAndTranscribes()
    {
        _controller.Initialize();
        _controller.StartRecording();

        _audio.RaiseAutoStop();

        Assert.True(WaitUntil(() => _controller.State == AppState.Idle && _injector.InsertedTexts.Count == 1, TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void MicrophoneLevel_UpdatesLive2DMouthWhileListening()
    {
        _controller.Initialize();
        _controller.StartRecording();

        _audio.RaiseLevel(0.2f);

        Assert.True(_live2d.MouthOpen > 0);
    }

    [Fact]
    public void ApplyConfig_UpdatesHotkeysAndLive2D()
    {
        _controller.Initialize();
        var config = _configStore.Current;
        config.Hotkeys.PushToTalk = "F2";
        config.Live2D.Opacity = 0.5;

        _controller.ApplyConfig(config);

        Assert.Equal("F2", _hotkeys.Config!.PushToTalk);
        Assert.Equal(0.5, _live2d.Config!.Opacity);
    }

    [Fact]
    public async Task TestProviderAsync_CapturesAndTranscribesWithGivenProfile()
    {
        _controller.Initialize();
        var profile = new ProviderProfile
        {
            Id = "test",
            Name = "测试服务",
            TypeId = AsrProviderTypeIds.Mock
        };

        var result = await _controller.TestProviderAsync(profile, seconds: 1);

        Assert.Equal("你好，世界", result.Text);
        Assert.True(_audio.StartCount >= 1);
        Assert.Equal("test", _asr.LastProfile!.Id);
        Assert.Equal(AppState.Idle, _controller.State);
    }

    [Fact]
    public async Task DisposeAsync_DisposesServices()
    {
        _controller.Initialize();

        await _controller.DisposeAsync();

        Assert.False(_hotkeys.IsRunning);
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return condition();
    }
}
