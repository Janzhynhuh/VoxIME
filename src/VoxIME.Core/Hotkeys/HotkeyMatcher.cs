using VoxIME.Core.Abstractions;
using VoxIME.Core.Configuration;

namespace VoxIME.Core.Hotkeys;

/// <summary>
/// 热键匹配状态机：消费全局键盘钩子的 keydown/keyup，输出"按住说话/松开/切换/取消"语义事件。
/// </summary>
/// <remarks>
/// <para>只根据"按键从抬起变为按下"的边沿触发，因此键盘自动重复（连续 keydown）不会重复触发。</para>
/// <para>按住说话在组合键全部按下时进入 Pressed，任意一个所需键抬起时进入 Released。</para>
/// <para>本类型不处理真实键盘钩子，纯状态机，可完全离线单测。</para>
/// </remarks>
public sealed class HotkeyMatcher
{
    private readonly HashSet<int> _downKeys = new();
    private readonly List<string> _invalidGestures = new();

    private readonly HotkeyGesture? _pushToTalk;
    private readonly HotkeyGesture? _toggle;
    private readonly HotkeyGesture? _cancel;

    private bool _pushToTalkActive;

    public HotkeyMatcher(HotkeyConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        _pushToTalk = ParseGesture(config.EnablePushToTalk, config.PushToTalk);
        _toggle = ParseGesture(config.EnableToggle, config.Toggle);
        _cancel = ParseGesture(true, config.Cancel);
    }

    /// <summary>按住说话热键（未启用或配置非法时为 null）。</summary>
    public HotkeyGesture? PushToTalkGesture => _pushToTalk;

    /// <summary>按下切换热键（未启用或配置非法时为 null）。</summary>
    public HotkeyGesture? ToggleGesture => _toggle;

    /// <summary>取消热键（配置非法时为 null）。</summary>
    public HotkeyGesture? CancelGesture => _cancel;

    /// <summary>解析失败被忽略的热键字符串（服务会通过 Error 事件上报）。</summary>
    public IReadOnlyList<string> InvalidGestures => _invalidGestures;

    /// <summary>按住说话是否处于"已按下"状态。</summary>
    public bool IsPushToTalkActive => _pushToTalkActive;

    /// <summary>当前按下的虚拟键集合（只读快照，调试用）。</summary>
    public IReadOnlyCollection<int> PressedKeys => _downKeys;

    /// <summary>处理一次按键按下，返回需要上报的热键事件（可能为空）。</summary>
    public IReadOnlyList<HotkeyEventArgs> ProcessKeyDown(int virtualKey)
    {
        if (virtualKey <= 0)
        {
            return Array.Empty<HotkeyEventArgs>();
        }

        if (!_downKeys.Add(virtualKey))
        {
            // 键盘自动重复：同一个键的重复 keydown 不再触发。
            return Array.Empty<HotkeyEventArgs>();
        }

        List<HotkeyEventArgs>? events = null;

        if (_cancel is not null && _cancel.VirtualKey == virtualKey && _cancel.IsSatisfiedBy(_downKeys))
        {
            Add(ref events, new HotkeyEventArgs(HotkeyAction.CancelPressed, _cancel.Display));
        }

        if (_toggle is not null && _toggle.VirtualKey == virtualKey && _toggle.IsSatisfiedBy(_downKeys))
        {
            Add(ref events, new HotkeyEventArgs(HotkeyAction.TogglePressed, _toggle.Display));
        }

        if (_pushToTalk is not null && !_pushToTalkActive && _pushToTalk.IsSatisfiedBy(_downKeys))
        {
            _pushToTalkActive = true;
            Add(ref events, new HotkeyEventArgs(HotkeyAction.PushToTalkPressed, _pushToTalk.Display));
        }

        return events is null ? Array.Empty<HotkeyEventArgs>() : events;
    }

    /// <summary>处理一次按键抬起，返回需要上报的热键事件（可能为空）。</summary>
    public IReadOnlyList<HotkeyEventArgs> ProcessKeyUp(int virtualKey)
    {
        if (virtualKey <= 0 || !_downKeys.Remove(virtualKey))
        {
            return Array.Empty<HotkeyEventArgs>();
        }

        if (_pushToTalkActive && _pushToTalk is not null && !_pushToTalk.IsSatisfiedBy(_downKeys))
        {
            _pushToTalkActive = false;
            return new[] { new HotkeyEventArgs(HotkeyAction.PushToTalkReleased, _pushToTalk.Display) };
        }

        return Array.Empty<HotkeyEventArgs>();
    }

    /// <summary>清空状态（开始/停止监听时调用，避免残留按键状态导致卡在按下态）。</summary>
    public void Reset()
    {
        _downKeys.Clear();
        _pushToTalkActive = false;
    }

    private HotkeyGesture? ParseGesture(bool enabled, string? text)
    {
        if (!enabled || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (HotkeyGesture.TryParse(text, out var gesture) && gesture is not null)
        {
            return gesture;
        }

        _invalidGestures.Add(text.Trim());
        return null;
    }

    private static void Add(ref List<HotkeyEventArgs>? events, HotkeyEventArgs args)
    {
        events ??= new List<HotkeyEventArgs>(2);
        events.Add(args);
    }
}
