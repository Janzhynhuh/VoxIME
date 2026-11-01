using VoxIME.Core.Configuration;

namespace VoxIME.Core.Abstractions;

public enum HotkeyAction
{
    PushToTalkPressed,
    PushToTalkReleased,
    TogglePressed,
    CancelPressed
}

public sealed class HotkeyEventArgs : EventArgs
{
    public HotkeyEventArgs(HotkeyAction action, string gesture)
    {
        Action = action;
        Gesture = gesture;
    }

    public HotkeyAction Action { get; }
    public string Gesture { get; }
}

/// <summary>全局热键服务：基于低级键盘钩子，支持"按住说话"与"按下切换"。</summary>
public interface IHotkeyService : IDisposable
{
    bool IsRunning { get; }
    event EventHandler<HotkeyEventArgs>? HotkeyTriggered;
    event EventHandler<string>? Error;

    void Start(HotkeyConfig config);
    void Stop();
    void Update(HotkeyConfig config);
}
