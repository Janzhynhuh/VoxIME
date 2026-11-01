using NAudio.Wave;

namespace VoxIME.Core.Tts;

/// <summary>WAV 播放抽象，便于测试时替换成不依赖声卡的假实现。</summary>
internal interface IWavPlayer : IDisposable
{
    bool IsPlaying { get; }

    void Start(byte[] wavBytes, float volume);

    void Stop();
}

internal interface IWavPlayerFactory
{
    IWavPlayer Create();
}

/// <summary>默认工厂：NAudio WaveOutEvent 播放。</summary>
internal sealed class NAudioWavPlayerFactory : IWavPlayerFactory
{
    public IWavPlayer Create() => new NAudioWavPlayer();
}

/// <summary>基于 NAudio WaveOutEvent 的 WAV 播放器（16bit PCM）。</summary>
internal sealed class NAudioWavPlayer : IWavPlayer
{
    private readonly object _sync = new();
    private MemoryStream? _stream;
    private WaveFileReader? _reader;
    private WaveOutEvent? _output;
    private bool _disposed;

    public bool IsPlaying
    {
        get
        {
            lock (_sync)
            {
                return !_disposed && _output is not null && _output.PlaybackState == PlaybackState.Playing;
            }
        }
    }

    public void Start(byte[] wavBytes, float volume)
    {
        ArgumentNullException.ThrowIfNull(wavBytes);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopLocked();

            _stream = new MemoryStream(wavBytes, writable: false);
            _reader = new WaveFileReader(_stream);
            _output = new WaveOutEvent
            {
                DesiredLatency = 100,
                NumberOfBuffers = 2,
                Volume = Math.Clamp(volume, 0f, 1f)
            };
            _output.Init(_reader);
            _output.Play();
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            StopLocked();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            StopLocked();
        }
    }

    private void StopLocked()
    {
        if (_output is not null)
        {
            try
            {
                _output.Stop();
            }
            catch (Exception)
            {
                // 忽略停止播放的异常，随后统一释放。
            }
        }

        try
        {
            _output?.Dispose();
        }
        catch (Exception)
        {
            // 忽略。
        }

        try
        {
            _reader?.Dispose();
        }
        catch (Exception)
        {
            // 忽略。
        }

        try
        {
            _stream?.Dispose();
        }
        catch (Exception)
        {
            // 忽略。
        }

        _output = null;
        _reader = null;
        _stream = null;
    }
}
