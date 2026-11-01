using VoxIME.Core.Hotkeys;

namespace VoxIME.Core.Tests.Hotkeys;

/// <summary>热键字符串解析/规范化/校验测试（纯逻辑，无系统依赖）。</summary>
public class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Alt")]
    [InlineData("ctrl+alt")]
    [InlineData("ALT+CTRL")]
    [InlineData(" Alt + Ctrl ")]
    [InlineData("Control+Menu")]
    public void 修饰键组合解析成功(string text)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.NotNull(gesture);
        Assert.True(gesture.IsModifierOnly);
        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Alt, gesture.Modifiers);
        Assert.Equal(0, gesture.VirtualKey);
        Assert.Equal("Ctrl+Alt", gesture.Display);
    }

    [Theory]
    [InlineData("ctrl+alt+m", "Ctrl+Alt+M")]
    [InlineData("Shift+Ctrl+P", "Ctrl+Shift+P")]
    [InlineData("Win+Alt+F12", "Alt+Win+F12")]
    [InlineData("ctrl+shift+space", "Ctrl+Shift+Space")]
    [InlineData("Esc", "Esc")]
    [InlineData("escape", "Esc")]
    [InlineData("return", "Enter")]
    [InlineData("pgdn", "PageDown")]
    [InlineData("ArrowUp", "Up")]
    [InlineData("f2", "F2")]
    [InlineData("0", "0")]
    public void 组合键规范化输出(string input, string expected)
    {
        Assert.True(HotkeyGesture.TryParse(input, out var gesture));
        Assert.Equal(expected, gesture!.Display);
        Assert.Equal(expected, HotkeyGesture.Normalize(input));
        Assert.Equal(expected, gesture.ToString());
    }

    [Theory]
    [InlineData("A", 0x41)]
    [InlineData("z", 0x5A)]
    [InlineData("0", 0x30)]
    [InlineData("9", 0x39)]
    [InlineData("F1", 0x70)]
    [InlineData("F24", 0x87)]
    [InlineData("Esc", 0x1B)]
    [InlineData("Space", 0x20)]
    [InlineData("Enter", 0x0D)]
    [InlineData("Tab", 0x09)]
    [InlineData("Up", 0x26)]
    [InlineData("Delete", 0x2E)]
    public void 主键映射到正确的虚拟键码(string input, int expectedVirtualKey)
    {
        var gesture = HotkeyGesture.ParseOrNull(input);

        Assert.NotNull(gesture);
        Assert.Equal(expectedVirtualKey, gesture.VirtualKey);
        Assert.False(gesture.IsModifierOnly);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+")]
    [InlineData("+Alt")]
    [InlineData("Ctrl++Alt")]
    [InlineData("Bogus")]
    [InlineData("Ctrl+Ctrl")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+Alt+Shift+Win+Ctrl")]
    [InlineData("F25")]
    [InlineData("Hyper+A")]
    public void 非法热键解析失败(string text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Null(gesture);
        Assert.False(HotkeyGesture.IsValid(text));
        Assert.Null(HotkeyGesture.ParseOrNull(text));
        Assert.Equal(string.Empty, HotkeyGesture.Normalize(text));
    }

    [Fact]
    public void 空引用与非空空白字符处理()
    {
        Assert.False(HotkeyGesture.TryParse(null, out _));
        Assert.False(HotkeyGesture.IsValid(null));
        Assert.Equal(string.Empty, HotkeyGesture.Normalize(null));
        Assert.Null(HotkeyGesture.ParseOrNull(null));
    }

    [Fact]
    public void 修饰键标志位正确()
    {
        var gesture = HotkeyGesture.ParseOrNull("Ctrl+Shift+Alt+Win+M");

        Assert.NotNull(gesture);
        Assert.True(gesture.HasControl);
        Assert.True(gesture.HasAlt);
        Assert.True(gesture.HasShift);
        Assert.True(gesture.HasWin);
        Assert.Equal("Ctrl+Alt+Shift+Win+M", gesture.Display);

        var altOnly = HotkeyGesture.ParseOrNull("Alt+X");
        Assert.NotNull(altOnly);
        Assert.False(altOnly.HasControl);
        Assert.True(altOnly.HasAlt);
        Assert.False(altOnly.HasShift);
        Assert.False(altOnly.HasWin);
    }

    [Fact]
    public void 单键与修饰键组合在等值显示上可区分()
    {
        var modifierOnly = HotkeyGesture.ParseOrNull("Ctrl+Alt");
        var withKey = HotkeyGesture.ParseOrNull("Ctrl+Alt+M");

        Assert.NotNull(modifierOnly);
        Assert.NotNull(withKey);
        Assert.NotEqual(modifierOnly.Display, withKey.Display);
        Assert.Equal("M", withKey.KeyName);
        Assert.Null(modifierOnly.KeyName);
    }
}
