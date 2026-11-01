using VoxIME.Core.Abstractions;
using VoxIME.Core.Logging;
using VoxIME.Core.Tts;

namespace VoxIME.Core.Tests.Tts;

/// <summary>TTS 服务测试：用伪合成引擎 + 伪播放器，完全离线、不依赖语音包/声卡。</summary>
public class SapiTtsServiceTests
{
    [Fact]
    public void 无语音包时IsAvailable为false()
    {
        var (service, engine, _) = CreateService(available: false);

        Assert.False(service.IsAvailable);
        Assert.Empty(service.GetInstalledVoices());
        Assert.Empty(engine.GetInstalledVoices());
    }

    [Fact]
    public async Task 无语音包时SpeakAsync直接返回不抛异常()
    {
        var (service, engine, player) = CreateService(available: false);
        var started = false;
        service.SpeakingStarted += (_, _) => started = true;

        await service.SpeakAsync("你好，世界", new TtsOptions());

        Assert.False(started);
        Assert.Equal(0, engine.SynthesizeCalls);
        Assert.Equal(0, player.StartCount);
    }

    [Fact]
    public async Task 正常朗读触发开始电平与完成事件()
    {
        var (service, engine, player) = CreateService();
        var events = new List<string>();
        var levels = new List<float>();
        service.SpeakingStarted += (_, _) => events.Add("started");
        service.SpeakingCompleted += (_, _) => events.Add("completed");
        service.LevelChanged += (_, args) => levels.Add(args.Level);

        await service.SpeakAsync("你好", new TtsOptions { Volume = 100 });

        Assert.Equal(new[] { "started", "completed" }, events);
        Assert.NotEmpty(levels);
        Assert.All(levels, level => Assert.InRange(level, 0f, 1f));
        Assert.Contains(levels, level => level > 0);
        Assert.Equal(1, engine.SynthesizeCalls);
        Assert.Equal(1, player.StartCount);
        Assert.Equal(engine.Wav, player.LastWav);
        Assert.True(player.StopCount >= 1);
    }

    [Fact]
    public async Task 朗读音量会传给播放器()
    {
        var (service, _, player) = CreateService();

        await service.SpeakAsync("测试音量", new TtsOptions { Volume = 50 });

        Assert.Equal(0.5f, player.LastVolume, 2);
    }

    [Fact]
    public async Task 合成失败通过Error事件上报且不抛异常()
    {
        var (service, engine, player) = CreateService();
        engine.ThrowOnSynthesize = true;
        var errors = new List<string>();
        service.Error += (_, message) => errors.Add(message);

        await service.SpeakAsync("会失败", new TtsOptions());

        Assert.NotEmpty(errors);
        Assert.Equal(0, player.StartCount);
    }

    [Fact]
    public async Task 播放失败通过Error事件上报且不抛异常()
    {
        var (service, _, player) = CreateService();
        player.ThrowOnStart = true;
        var errors = new List<string>();
        var started = false;
        service.Error += (_, message) => errors.Add(message);
        service.SpeakingStarted += (_, _) => started = true;

        await service.SpeakAsync("放不出声", new TtsOptions());

        Assert.NotEmpty(errors);
        Assert.False(started);
    }

    [Fact]
    public async Task 合成空音频时跳过播放()
    {
        var (service, engine, player) = CreateService();
        engine.Wav = Array.Empty<byte>();
        var completed = false;
        service.SpeakingCompleted += (_, _) => completed = true;

        await service.SpeakAsync("空音频", new TtsOptions());

        Assert.False(completed);
        Assert.Equal(0, player.StartCount);
    }

    [Fact]
    public async Task Stop可以打断正在进行的朗读()
    {
        var (service, _, player) = CreateService();
        player.EndlessPlayback = true;
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = false;
        service.SpeakingStarted += (_, _) => started.TrySetResult(true);
        service.SpeakingCompleted += (_, _) => completed = true;

        var speakTask = service.SpeakAsync("这是一段很长的朗读文本", new TtsOptions());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        service.Stop();
        await speakTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(completed);
        Assert.True(player.StopCount > 0);
    }

    [Fact]
    public async Task 空白文本直接返回()
    {
        var (service, engine, _) = CreateService();

        await service.SpeakAsync("   ", new TtsOptions());

        Assert.Equal(0, engine.SynthesizeCalls);
    }

    [Fact]
    public async Task 已取消的令牌不会抛异常()
    {
        var (service, engine, _) = CreateService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await service.SpeakAsync("不会朗读", new TtsOptions(), cts.Token);

        Assert.Equal(0, engine.SynthesizeCalls);
    }

    [Fact]
    public void 音色列表来自合成引擎()
    {
        var (service, engine, _) = CreateService();
        engine.Voices.Add("第二个音色");

        var voices = service.GetInstalledVoices();

        Assert.Equal(new[] { "测试音色", "第二个音色" }, voices);
        Assert.True(service.IsAvailable);
    }

    [Fact]
    public void Dispose释放播放器与合成引擎且可重复调用()
    {
        var (service, engine, player) = CreateService();

        service.Dispose();
        service.Dispose();

        Assert.True(engine.Disposed);
        Assert.True(player.DisposeCount >= 1);
        Assert.False(service.IsAvailable);
    }

    [Fact]
    public async Task 同一实例可重复朗读()
    {
        var (service, engine, player) = CreateService();
        var startedCount = 0;
        var completedCount = 0;
        service.SpeakingStarted += (_, _) => startedCount++;
        service.SpeakingCompleted += (_, _) => completedCount++;

        await service.SpeakAsync("第一段", new TtsOptions());
        await service.SpeakAsync("第二段", new TtsOptions());

        Assert.Equal(2, startedCount);
        Assert.Equal(2, completedCount);
        Assert.Equal(2, engine.SynthesizeCalls);
        Assert.Equal(2, player.StartCount);
    }

    [Fact]
    public async Task Dispose后调用SpeakAsync不抛异常()
    {
        var (service, _, _) = CreateService();
        service.Dispose();

        await service.SpeakAsync("释放之后", new TtsOptions());
    }

    private static (SapiTtsService Service, FakeSynthesisEngine Engine, FakeWavPlayer Player) CreateService(bool available = true)
    {
        var engine = new FakeSynthesisEngine { Available = available };
        var factory = new FakeWavPlayerFactory();
        var service = new SapiTtsService(NullLogger.Instance, engine, factory);
        return (service, engine, factory.Player);
    }

    private sealed class FakeSynthesisEngine : ISpeechSynthesisEngine
    {
        public bool Available { get; set; } = true;

        public List<string> Voices { get; } = new() { "测试音色" };

        public bool ThrowOnSynthesize { get; set; }

        public byte[] Wav { get; set; } = AudioClip.CreateTone(TimeSpan.FromMilliseconds(200), 440, 0.5).ToWav();

        public int SynthesizeCalls { get; private set; }

        public bool Disposed { get; private set; }

        public bool IsAvailable => Available;

        public IReadOnlyList<string> GetInstalledVoices() => Available ? Voices : Array.Empty<string>();

        public byte[] SynthesizeToWav(string text, TtsOptions options)
        {
            SynthesizeCalls++;
            if (ThrowOnSynthesize)
            {
                throw new InvalidOperationException("模拟合成失败");
            }

            return Wav;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeWavPlayerFactory : IWavPlayerFactory
    {
        public FakeWavPlayer Player { get; } = new();

        public IWavPlayer Create() => Player;
    }

    private sealed class FakeWavPlayer : IWavPlayer
    {
        private bool _playing;
        private int _polls;

        public bool EndlessPlayback { get; set; }

        public bool ThrowOnStart { get; set; }

        public int PollsBeforeComplete { get; set; } = 2;

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public int DisposeCount { get; private set; }

        public byte[]? LastWav { get; private set; }

        public float LastVolume { get; private set; }

        public bool IsPlaying
        {
            get
            {
                if (!_playing)
                {
                    return false;
                }

                _polls++;
                if (!EndlessPlayback && _polls > PollsBeforeComplete)
                {
                    _playing = false;
                    return false;
                }

                return true;
            }
        }

        public void Start(byte[] wavBytes, float volume)
        {
            if (ThrowOnStart)
            {
                throw new InvalidOperationException("模拟播放失败（没有音频设备）");
            }

            StartCount++;
            LastWav = wavBytes;
            LastVolume = volume;
            _playing = true;
            _polls = 0;
        }

        public void Stop()
        {
            StopCount++;
            _playing = false;
        }

        public void Dispose()
        {
            DisposeCount++;
            _playing = false;
        }
    }
}
