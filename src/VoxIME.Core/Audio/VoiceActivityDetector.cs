namespace VoxIME.Core.Audio;

/// <summary>
/// 轻量级语音活动检测（VAD）：短时能量 + 噪声底自适应 + 最短语音时长。
/// </summary>
/// <remarks>
/// <para>输入为 16000Hz 归一化 float 采样（-1..1），内部按 20ms 一帧计算均方根能量。</para>
/// <para>判定规则：<c>rms &gt; max(绝对阈值, 噪声底 * 2.5)</c> 记为语音帧；
/// 噪声底只在"非语音帧"上自适应上调（快），遇到更安静的环境则立即下调，
/// 因此持续说话的帧不会被噪声底吞掉。</para>
/// <para>累积语音时长达到最短语音时长后 <see cref="HasSpeech"/> 置为 true。</para>
/// <para>本类型非线程安全：每个采集实例独占一个检测器，只在采集回调线程内使用。</para>
/// </remarks>
public sealed class VoiceActivityDetector
{
    public const int DefaultSampleRate = 16000;
    public const int DefaultFrameMs = 20;

    /// <summary>噪声底每帧上调倍率（约 12 帧从 0.0005 追到 0.012）。</summary>
    private const double NoiseFloorGrowth = 1.3;

    /// <summary>噪声底上调时叠加的极小量，避免静音时噪声底恒为 0。</summary>
    private const double NoiseFloorEpsilon = 1e-5;

    /// <summary>动态阈值 = 噪声底 * 该倍率。</summary>
    private const double NoiseToThresholdRatio = 2.5;

    /// <summary>语音结束后保持"正在说话"状态的时间，避免词间停顿被切碎。</summary>
    private const int HangoverMs = 200;

    private readonly double _absoluteThreshold;
    private readonly int _minSpeechMs;
    private readonly int _sampleRate;
    private readonly int _frameSamples;
    private readonly int _frameMs;
    private readonly List<float> _pending = new(DefaultFrameMs * 2);

    private double _noiseFloor = 0.0005;
    private double _speechMs;
    private double _hangoverLeftMs;
    private double _lastLevel;
    private bool _isSpeaking;
    private long _processedFrames;
    private long _speechFrames;

    /// <param name="vadThreshold">绝对能量阈值（RMS，0..1），低于该值的帧一定不是语音。</param>
    /// <param name="minSpeechMs">判定"有语音"所需的最短累积语音时长（毫秒）。</param>
    /// <param name="sampleRate">采样率，默认 16000。</param>
    public VoiceActivityDetector(double vadThreshold = 0.014, int minSpeechMs = 300, int sampleRate = DefaultSampleRate)
    {
        _sampleRate = Math.Clamp(sampleRate, 8000, 48000);
        _frameMs = DefaultFrameMs;
        _frameSamples = Math.Max(1, _sampleRate * _frameMs / 1000);
        _absoluteThreshold = double.IsFinite(vadThreshold) && vadThreshold > 0 ? vadThreshold : 0.014;
        _minSpeechMs = Math.Clamp(minSpeechMs, 0, 60_000);
    }

    public int SampleRate => _sampleRate;

    public int FrameSamples => _frameSamples;

    public TimeSpan FrameDuration => TimeSpan.FromMilliseconds(_frameMs);

    public TimeSpan MinSpeechDuration => TimeSpan.FromMilliseconds(_minSpeechMs);

    /// <summary>当前估计的噪声底 RMS。</summary>
    public double NoiseFloor => _noiseFloor;

    /// <summary>最近一帧的能量（RMS，0..1）。</summary>
    public double LastLevel => _lastLevel;

    /// <summary>当前是否处于语音段（含 200ms 尾音保持）。</summary>
    public bool IsSpeaking => _isSpeaking;

    /// <summary>累积语音时长是否已达到最短语音时长。</summary>
    public bool HasSpeech => _speechMs >= _minSpeechMs;

    /// <summary>累积语音时长（只统计被判为语音的帧）。</summary>
    public TimeSpan SpeechDuration => TimeSpan.FromMilliseconds(_speechMs);

    /// <summary>已处理的音频时长。</summary>
    public TimeSpan ProcessedDuration => TimeSpan.FromSeconds(_processedFrames * (double)_frameSamples / _sampleRate);

    /// <summary>已处理帧数 / 其中语音帧数（调试与测试用）。</summary>
    public long ProcessedFrames => _processedFrames;

    public long SpeechFrames => _speechFrames;

    /// <summary>
    /// 送入一段 16kHz 归一化采样。返回处理完这些采样后是否处于语音段。
    /// 采样会被内部按帧缓冲，允许任意长度（含不足一帧的尾部）。
    /// </summary>
    public bool Process(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return _isSpeaking;
        }

        for (var i = 0; i < samples.Length; i++)
        {
            var value = samples[i];
            _pending.Add(float.IsFinite(value) ? value : 0f);
            if (_pending.Count >= _frameSamples)
            {
                ProcessFrame(_frameSamples);
                _pending.Clear();
            }
        }

        return _isSpeaking;
    }

    /// <summary>把缓冲区中不足一帧的尾部按实际长度处理掉（停止录音时调用，避免丢掉最后几十毫秒）。</summary>
    public bool Flush()
    {
        if (_pending.Count > 0)
        {
            ProcessFrame(_pending.Count);
            _pending.Clear();
        }

        return _isSpeaking;
    }

    /// <summary>重置全部状态（开始新一段录音时调用）。</summary>
    public void Reset()
    {
        _pending.Clear();
        _noiseFloor = 0.0005;
        _speechMs = 0;
        _hangoverLeftMs = 0;
        _lastLevel = 0;
        _isSpeaking = false;
        _processedFrames = 0;
        _speechFrames = 0;
    }

    private void ProcessFrame(int sampleCount)
    {
        if (sampleCount <= 0)
        {
            return;
        }

        var sumSquares = 0d;
        for (var i = 0; i < sampleCount; i++)
        {
            var value = _pending[i];
            sumSquares += (double)value * value;
        }

        var rms = Math.Sqrt(sumSquares / sampleCount);
        _lastLevel = rms;

        var dynamicThreshold = Math.Max(_absoluteThreshold, _noiseFloor * NoiseToThresholdRatio);
        var voiced = rms > dynamicThreshold;

        if (voiced)
        {
            _speechMs += sampleCount * 1000.0 / _sampleRate;
            _speechFrames++;
            _isSpeaking = true;
            _hangoverLeftMs = HangoverMs;
        }
        else
        {
            // 只在非语音帧上自适应噪声底：环境变吵时阈值抬高，安静时立即回落。
            _noiseFloor = Math.Min(rms, _noiseFloor * NoiseFloorGrowth + NoiseFloorEpsilon);
            if (_isSpeaking)
            {
                _hangoverLeftMs -= sampleCount * 1000.0 / _sampleRate;
                if (_hangoverLeftMs <= 0)
                {
                    _isSpeaking = false;
                }
            }
        }

        _processedFrames++;
    }
}
