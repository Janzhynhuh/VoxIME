using VoxIME.Core.Configuration;

namespace VoxIME.Core.Abstractions;

public enum Live2DState
{
    Idle,
    Listening,
    Thinking,
    Speaking,
    Error
}

/// <summary>
/// Live2D 桌宠控制器：负责透明置顶窗口、模型加载、状态/眼动/口型联动。
/// 在无模型或 WebView2 不可用时应自动降级为内置兜底形象。
/// 必须由 UI 线程创建和调用（实现内部应自行 Dispatcher 封送）。
/// </summary>
public interface ILive2DController : IDisposable
{
    bool IsAvailable { get; }
    bool IsVisible { get; }
    Live2DState State { get; }
    string? LastError { get; }

    event EventHandler? Ready;
    event EventHandler<string>? ErrorOccurred;

    void Initialize(Live2DConfig config);
    void ApplyConfig(Live2DConfig config);
    void Show();
    void Hide();

    void SetState(Live2DState state);

    /// <summary>0..1 张嘴幅度（麦克风音量或 TTS 播放音量）。</summary>
    void SetMouthOpen(double value);

    /// <summary>归一化视线目标（-1..1），用于眼动跟随。</summary>
    void SetGaze(double normalizedX, double normalizedY);

    /// <summary>在桌宠气泡中显示文字；null 隐藏。</summary>
    void SetBubbleText(string? text);
}
