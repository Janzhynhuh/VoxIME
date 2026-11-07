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

/// <summary>模型加载完成后的尺寸信息（用于窗口自适应）。</summary>
public sealed class Live2DModelSizeEventArgs : EventArgs
{
    public Live2DModelSizeEventArgs(string name, double width, double height)
    {
        Name = name;
        Width = width;
        Height = height;
    }

    public string Name { get; }
    public double Width { get; }
    public double Height { get; }
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

    /// <summary>模型加载成功并上报原始宽高（用于按模型比例自适应窗口）。</summary>
    event EventHandler<Live2DModelSizeEventArgs>? ModelSizeAvailable;

    void Initialize(Live2DConfig config);
    void ApplyConfig(Live2DConfig config);
    void Show();
    void Hide();

    /// <summary>当前配置中的点击穿透目标状态（窗口实际样式由 SetClickThrough / ApplyConfig 应用）。</summary>
    bool IsClickThrough { get; }

    /// <summary>立即切换点击穿透并写入内部配置快照（不负责持久化，宿主可据此写回配置）。</summary>
    void SetClickThrough(bool clickThrough);

    void SetState(Live2DState state);

    /// <summary>在桌宠上预览一个动作组（设置页动作预览用）；空组名忽略。</summary>
    void PreviewMotion(string? motionGroup);

    /// <summary>在桌宠上预览一个表情（设置页表情预览用）；空表情名忽略。</summary>
    void PreviewExpression(string? expression);

    /// <summary>0..1 张嘴幅度（麦克风音量或 TTS 播放音量）。</summary>
    void SetMouthOpen(double value);

    /// <summary>归一化视线目标（-1..1），用于眼动跟随。</summary>
    void SetGaze(double normalizedX, double normalizedY);

    /// <summary>在桌宠气泡中显示文字；null 隐藏。</summary>
    void SetBubbleText(string? text);
}
