using VoxIME.Core.Abstractions;
using VoxIME.Core.Configuration;
using VoxIME.Core.Hotkeys;

namespace VoxIME.Core.Tests.Hotkeys;

/// <summary>热键状态机测试：模拟低级键盘钩子收到的 keydown/keyup 序列。</summary>
public class HotkeyMatcherTests
{
    private const int LCtrl = 0xA2;
    private const int LAlt = 0xA4;
    private const int LShift = 0xA0;
    private const int KeyM = 0x4D;
    private const int KeyEsc = 0x1B;
    private const int KeyF2 = 0x71;

    private static HotkeyConfig DefaultConfig() => new()
    {
        PushToTalk = "Ctrl+Alt",
        Toggle = "Ctrl+Alt+M",
        Cancel = "Esc",
        EnablePushToTalk = true,
        EnableToggle = true
    };

    [Fact]
    public void 按住说话按下与松开()
    {
        var matcher = new HotkeyMatcher(DefaultConfig());

        Assert.Empty(matcher.ProcessKeyDown(LCtrl));
        var pressed = matcher.ProcessKeyDown(LAlt);

        var pressedEvent = Assert.Single(pressed);
        Assert.Equal(HotkeyAction.PushToTalkPressed, pressedEvent.Action);
        Assert.Equal("Ctrl+Alt", pressedEvent.Gesture);
        Assert.True(matcher.IsPushToTalkActive);

        var released = matcher.ProcessKeyUp(LAlt);

        var releasedEvent = Assert.Single(released);
        Assert.Equal(HotkeyAction.PushToTalkReleased, releasedEvent.Action);
        Assert.False(matcher.IsPushToTalkActive);
    }

    [Fact]
    public void 重复keydown不会重复触发按住说话()
    {
        var matcher = new HotkeyMatcher(DefaultConfig());
        matcher.ProcessKeyDown(LCtrl);
        var pressed = matcher.ProcessKeyDown(LAlt);
        Assert.Single(pressed);

        Assert.Empty(matcher.ProcessKeyDown(LAlt));
        Assert.Empty(matcher.ProcessKeyDown(LAlt));
        Assert.Empty(matcher.ProcessKeyDown(LCtrl));
    }

    [Fact]
    public void 松开任意一个按键都会结束按住说话()
    {
        var matcher = new HotkeyMatcher(DefaultConfig());
        matcher.ProcessKeyDown(LCtrl);
        matcher.ProcessKeyDown(LAlt);

        var released = matcher.ProcessKeyUp(LCtrl);

        Assert.Single(released);
        Assert.Empty(matcher.ProcessKeyUp(LAlt));
        Assert.False(matcher.IsPushToTalkActive);
    }

    [Fact]
    public void 切换热键只在按下沿触发一次()
    {
        var matcher = new HotkeyMatcher(DefaultConfig());
        matcher.ProcessKeyDown(LCtrl);
        matcher.ProcessKeyDown(LAlt);

        var toggled = matcher.ProcessKeyDown(KeyM);
        var toggleEvent = Assert.Single(toggled);
        Assert.Equal(HotkeyAction.TogglePressed, toggleEvent.Action);
        Assert.Equal("Ctrl+Alt+M", toggleEvent.Gesture);

        // 键盘自动重复不算新的切换。
        Assert.Empty(matcher.ProcessKeyDown(KeyM));

        // 松开后再按一次，才会再次切换。
        matcher.ProcessKeyUp(KeyM);
        var second = matcher.ProcessKeyDown(KeyM);
        Assert.Single(second);
    }

    [Fact]
    public void Esc触发取消()
    {
        var matcher = new HotkeyMatcher(DefaultConfig());

        var cancelled = matcher.ProcessKeyDown(KeyEsc);
        var cancelEvent = Assert.Single(cancelled);
        Assert.Equal(HotkeyAction.CancelPressed, cancelEvent.Action);
        Assert.Equal("Esc", cancelEvent.Gesture);

        matcher.ProcessKeyUp(KeyEsc);
        var again = matcher.ProcessKeyDown(KeyEsc);
        Assert.Single(again);
    }

    [Fact]
    public void 单键热键也支持按住说话()
    {
        var config = DefaultConfig();
        config.PushToTalk = "F2";
        var matcher = new HotkeyMatcher(config);

        var pressed = matcher.ProcessKeyDown(KeyF2);
        Assert.Single(pressed);
        Assert.True(matcher.IsPushToTalkActive);

        var released = matcher.ProcessKeyUp(KeyF2);
        Assert.Single(released);
        Assert.False(matcher.IsPushToTalkActive);
    }

    [Fact]
    public void 额外按住其他修饰键不影响匹配()
    {
        var matcher = new HotkeyMatcher(DefaultConfig());
        matcher.ProcessKeyDown(LShift);
        matcher.ProcessKeyDown(LCtrl);

        var pressed = matcher.ProcessKeyDown(LAlt);

        Assert.Single(pressed);
        Assert.True(matcher.IsPushToTalkActive);
    }

    [Fact]
    public void 未匹配的按键不产生事件()
    {
        var matcher = new HotkeyMatcher(DefaultConfig());

        Assert.Empty(matcher.ProcessKeyDown(0x41)); // A
        Assert.Empty(matcher.ProcessKeyUp(0x41));
        Assert.Empty(matcher.ProcessKeyDown(KeyM)); // 单独按 M 不满足 Ctrl+Alt+M
    }

    [Fact]
    public void 通用修饰键码与左右区分码都能匹配()
    {
        var matcher = new HotkeyMatcher(DefaultConfig());

        var pressed = matcher.ProcessKeyDown(0x11);
        Assert.Empty(pressed);
        pressed = matcher.ProcessKeyDown(0x12);

        Assert.Single(pressed);
        Assert.True(matcher.IsPushToTalkActive);
    }

    [Fact]
    public void 禁用热键后不再触发()
    {
        var config = DefaultConfig();
        config.EnablePushToTalk = false;
        config.EnableToggle = false;
        var matcher = new HotkeyMatcher(config);

        Assert.Null(matcher.PushToTalkGesture);
        Assert.Null(matcher.ToggleGesture);

        Assert.Empty(matcher.ProcessKeyDown(LCtrl));
        Assert.Empty(matcher.ProcessKeyDown(LAlt));
        Assert.Empty(matcher.ProcessKeyDown(KeyM));
        Assert.False(matcher.IsPushToTalkActive);
    }

    [Fact]
    public void 非法热键被记录且不参与匹配()
    {
        var config = DefaultConfig();
        config.PushToTalk = "Bogus";
        config.Toggle = "Ctrl+";
        var matcher = new HotkeyMatcher(config);

        Assert.Equal(2, matcher.InvalidGestures.Count);
        Assert.Contains("Bogus", matcher.InvalidGestures);
        Assert.Null(matcher.PushToTalkGesture);
        Assert.Null(matcher.ToggleGesture);
        Assert.NotNull(matcher.CancelGesture);
    }

    [Fact]
    public void 按住说话支持多个热键()
    {
        var config = DefaultConfig();
        config.PushToTalkGestures = new List<string> { "Ctrl+Alt", "F2" };
        var matcher = new HotkeyMatcher(config);

        Assert.Equal(2, matcher.PushToTalkGestures.Count);

        var pressed = matcher.ProcessKeyDown(KeyF2);
        Assert.Equal(HotkeyAction.PushToTalkPressed, Assert.Single(pressed).Action);
        Assert.True(matcher.IsPushToTalkActive);

        var released = matcher.ProcessKeyUp(KeyF2);
        Assert.Equal(HotkeyAction.PushToTalkReleased, Assert.Single(released).Action);

        matcher.ProcessKeyDown(LCtrl);
        var pressedAgain = matcher.ProcessKeyDown(LAlt);
        Assert.Equal(HotkeyAction.PushToTalkPressed, Assert.Single(pressedAgain).Action);
    }

    [Fact]
    public void 切换与取消都支持多个热键()
    {
        var config = DefaultConfig();
        config.ToggleGestures = new List<string> { "Ctrl+Alt+M", "F9" };
        config.CancelGestures = new List<string> { "Esc", "F10" };
        var matcher = new HotkeyMatcher(config);

        var toggle = matcher.ProcessKeyDown(0x78); // F9
        Assert.Equal(HotkeyAction.TogglePressed, Assert.Single(toggle).Action);
        matcher.ProcessKeyUp(0x78);

        matcher.ProcessKeyDown(LCtrl);
        matcher.ProcessKeyDown(LAlt);
        var toggleByCombo = matcher.ProcessKeyDown(KeyM);
        Assert.Equal(HotkeyAction.TogglePressed, Assert.Single(toggleByCombo).Action);

        var cancel = matcher.ProcessKeyDown(0x79); // F10
        Assert.Equal(HotkeyAction.CancelPressed, Assert.Single(cancel).Action);
    }

    [Fact]
    public void Reset清空按键状态()
    {
        var matcher = new HotkeyMatcher(DefaultConfig());
        matcher.ProcessKeyDown(LCtrl);
        matcher.ProcessKeyDown(LAlt);
        Assert.True(matcher.IsPushToTalkActive);

        matcher.Reset();

        Assert.False(matcher.IsPushToTalkActive);
        Assert.Empty(matcher.PressedKeys);

        // 重置后重新按下组合键应再次触发。
        matcher.ProcessKeyDown(LCtrl);
        Assert.Single(matcher.ProcessKeyDown(LAlt));
    }

    [Fact]
    public void 未按下的键抬起是安全的()
    {
        var matcher = new HotkeyMatcher(DefaultConfig());

        Assert.Empty(matcher.ProcessKeyUp(0x41));
        Assert.Empty(matcher.ProcessKeyUp(0));
        Assert.Empty(matcher.ProcessKeyDown(0));
    }

    [Fact]
    public void 鼠标侧键按住说话()
    {
        var config = DefaultConfig();
        config.PushToTalk = "MouseX1";
        var matcher = new HotkeyMatcher(config);

        var pressed = Assert.Single(matcher.ProcessKeyDown(0x05));
        Assert.Equal(HotkeyAction.PushToTalkPressed, pressed.Action);
        Assert.Equal("MouseX1", pressed.Gesture);
        Assert.True(matcher.IsPushToTalkActive);

        var released = Assert.Single(matcher.ProcessKeyUp(0x05));
        Assert.Equal(HotkeyAction.PushToTalkReleased, released.Action);
        Assert.Equal("MouseX1", released.Gesture);
        Assert.False(matcher.IsPushToTalkActive);
    }

    [Fact]
    public void 带修饰键的鼠标侧键需要修饰键也按下()
    {
        var config = DefaultConfig();
        config.PushToTalk = "Ctrl+MouseX2";
        var matcher = new HotkeyMatcher(config);

        Assert.Empty(matcher.ProcessKeyDown(0x06));
        Assert.Empty(matcher.ProcessKeyUp(0x06));

        matcher.ProcessKeyDown(LCtrl);
        var pressed = Assert.Single(matcher.ProcessKeyDown(0x06));
        Assert.Equal(HotkeyAction.PushToTalkPressed, pressed.Action);
        Assert.Equal("Ctrl+MouseX2", pressed.Gesture);

        var released = Assert.Single(matcher.ProcessKeyUp(LCtrl));
        Assert.Equal(HotkeyAction.PushToTalkReleased, released.Action);
    }

    [Fact]
    public void 鼠标键也可以作为切换录音热键()
    {
        var config = DefaultConfig();
        config.Toggle = "MouseX2";
        config.EnablePushToTalk = false;
        var matcher = new HotkeyMatcher(config);

        var toggle = Assert.Single(matcher.ProcessKeyDown(0x06));
        Assert.Equal(HotkeyAction.TogglePressed, toggle.Action);
        Assert.Equal("MouseX2", toggle.Gesture);
        Assert.Empty(matcher.ProcessKeyUp(0x06));
    }

    [Fact]
    public void 鼠标键与键盘热键互不影响()
    {
        var config = DefaultConfig();
        config.PushToTalk = "MouseX1";
        var matcher = new HotkeyMatcher(config);

        Assert.Single(matcher.ProcessKeyDown(0x05));
        Assert.True(matcher.IsPushToTalkActive);

        matcher.ProcessKeyDown(0x41);
        Assert.True(matcher.IsPushToTalkActive);

        Assert.Single(matcher.ProcessKeyUp(0x05));
        Assert.False(matcher.IsPushToTalkActive);
    }
}
