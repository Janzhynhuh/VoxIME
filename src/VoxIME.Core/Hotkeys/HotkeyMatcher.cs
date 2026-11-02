using VoxIME.Core.Abstractions;
using VoxIME.Core.Configuration;

namespace VoxIME.Core.Hotkeys;

/// <summary>
/// 热键匹配状态机：消费全局键盘钩子的 keydown/keyup，输出"按住说话/松开/切换/取消"语义事件。
/// </summary>
/// <remarks>
/// <para>只根据"按键从抬起变为按下"的边沿触发，因此键盘自动重复（连续 keydown）不会重复触发。</para>
/// <para>按住说话在组合键全部按下时进入 Pressed，任意一个所需键抬起时进入 Released。</para>
/// <para>每个功能可配置多个热键，任意一个匹配即可触发；旧配置的单热键字段仍然兼容。</para>
/// <para>本类型不处理真实键盘钩子，纯状态机，可完全离线单测。</para>
/// </remarks>
public sealed class HotkeyMatcher
{
    private readonly HashSet<int> _downKeys = new();
    private readonly List<string> _invalidGestures = new();
    private readonly List<HotkeyGesture> _pushToTalk = new();
    private readonly List<HotkeyGesture> _toggle = new();
    private readonly List<HotkeyGesture> _cancel = new();
    private readonly List<HotkeyGesture> _activePushToTalk = new();

    private string _lastPushToTalkDisplay = "";

    public HotkeyMatcher(HotkeyConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        ParseGestureList(config.EnablePushToTalk, config.PushToTalkGestures, config.PushToTalk, _pushToTalk);
        ParseGestureList(config.EnableToggle, config.ToggleGestures, config.Toggle, _toggle);
        ParseGestureList(true, config.CancelGestures, config.Cancel, _cancel);
    }

    /// <summary>按住说话的全部热键（未启用时为空）。</summary>
    public IReadOnlyList<HotkeyGesture> PushToTalkGestures => _pushToTalk;

    /// <summary>按下切换的全部热键（未启用时为空）。</summary>
    public IReadOnlyList<HotkeyGesture> ToggleGestures => _toggle;

    /// <summary>取消的全部热键。</summary>
    public IReadOnlyList<HotkeyGesture> CancelGestures => _cancel;

    /// <summary>第一个按住说话热键（兼容旧调用；未启用或配置非法时为 null）。</summary>
    public HotkeyGesture? PushToTalkGesture => _pushToTalk.FirstOrDefault();

    /// <summary>第一个按下切换热键（兼容旧调用）。</summary>
    public HotkeyGesture? ToggleGesture => _toggle.FirstOrDefault();

    /// <summary>第一个取消热键（兼容旧调用）。</summary>
    public HotkeyGesture? CancelGesture => _cancel.FirstOrDefault();

    /// <summary>解析失败被忽略的热键字符串（服务会通过 Error 事件上报）。</summary>
    public IReadOnlyList<string> InvalidGestures => _invalidGestures;

    /// <summary>按住说话是否处于"已按下"状态。</summary>
    public bool IsPushToTalkActive => _activePushToTalk.Count > 0;

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

        var cancel = FindMainKeyGesture(_cancel, virtualKey);
        if (cancel is not null)
        {
            Add(ref events, new HotkeyEventArgs(HotkeyAction.CancelPressed, cancel.Display));
        }

        var toggle = FindMainKeyGesture(_toggle, virtualKey);
        if (toggle is not null)
        {
            Add(ref events, new HotkeyEventArgs(HotkeyAction.TogglePressed, toggle.Display));
        }

        if (_activePushToTalk.Count == 0)
        {
            foreach (var gesture in _pushToTalk)
            {
                if (!gesture.IsSatisfiedBy(_downKeys))
                {
                    continue;
                }

                _activePushToTalk.Add(gesture);
                _lastPushToTalkDisplay = gesture.Display;
                Add(ref events, new HotkeyEventArgs(HotkeyAction.PushToTalkPressed, gesture.Display));
                break;
            }
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

        if (_activePushToTalk.Count == 0)
        {
            return Array.Empty<HotkeyEventArgs>();
        }

        _activePushToTalk.RemoveAll(gesture => !gesture.IsSatisfiedBy(_downKeys));
        if (_activePushToTalk.Count > 0)
        {
            return Array.Empty<HotkeyEventArgs>();
        }

        return new[]
        {
            new HotkeyEventArgs(
                HotkeyAction.PushToTalkReleased,
                string.IsNullOrEmpty(_lastPushToTalkDisplay) ? "按住说话" : _lastPushToTalkDisplay)
        };
    }

    /// <summary>清空状态（开始/停止监听时调用，避免残留按键状态导致卡在按下态）。</summary>
    public void Reset()
    {
        _downKeys.Clear();
        _activePushToTalk.Clear();
        _lastPushToTalkDisplay = "";
    }

    private HotkeyGesture? FindMainKeyGesture(List<HotkeyGesture> gestures, int virtualKey)
    {
        foreach (var gesture in gestures)
        {
            if (gesture.VirtualKey == virtualKey && gesture.IsSatisfiedBy(_downKeys))
            {
                return gesture;
            }
        }

        return null;
    }

    private void ParseGestureList(bool enabled, List<string>? configured, string? legacy, List<HotkeyGesture> target)
    {
        if (!enabled)
        {
            return;
        }

        var texts = configured?.Where(text => !string.IsNullOrWhiteSpace(text)).ToList() ?? new List<string>();
        if (texts.Count == 0 && !string.IsNullOrWhiteSpace(legacy))
        {
            texts.Add(legacy);
        }

        foreach (var text in texts)
        {
            var trimmed = text.Trim();
            if (HotkeyGesture.TryParse(trimmed, out var gesture) && gesture is not null)
            {
                if (target.All(existing => !string.Equals(existing.Display, gesture.Display, StringComparison.OrdinalIgnoreCase)))
                {
                    target.Add(gesture);
                }
            }
            else if (!_invalidGestures.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                _invalidGestures.Add(trimmed);
            }
        }
    }

    private static void Add(ref List<HotkeyEventArgs>? events, HotkeyEventArgs args)
    {
        events ??= new List<HotkeyEventArgs>(2);
        events.Add(args);
    }
}
