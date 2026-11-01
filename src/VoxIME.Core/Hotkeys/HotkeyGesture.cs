using System.Text;

namespace VoxIME.Core.Hotkeys;

/// <summary>热键修饰键（与具体左/右按键无关）。</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Win = 8
}

/// <summary>
/// 解析后的热键组合，例如 <c>Ctrl+Alt</c>（仅修饰键，按住说话）或 <c>Ctrl+Alt+M</c>（普通组合键）。
/// </summary>
/// <remarks>
/// 支持的热键字符串：修饰键 Ctrl/Alt/Shift/Win 加单个按键（A-Z、0-9、F1-F24、Esc、Space、Enter、
/// Tab、Backspace、Delete、方向键等），大小写不敏感、可用任意顺序书写。
/// 规范化输出顺序固定为 <c>Ctrl+Alt+Shift+Win+主键</c>。
/// </remarks>
public sealed class HotkeyGesture
{
    private static readonly Dictionary<string, HotkeyModifiers> ModifierAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = HotkeyModifiers.Control,
        ["Control"] = HotkeyModifiers.Control,
        ["Ctl"] = HotkeyModifiers.Control,
        ["Alt"] = HotkeyModifiers.Alt,
        ["Menu"] = HotkeyModifiers.Alt,
        ["Shift"] = HotkeyModifiers.Shift,
        ["Win"] = HotkeyModifiers.Win,
        ["Windows"] = HotkeyModifiers.Win,
        ["Meta"] = HotkeyModifiers.Win,
        ["Super"] = HotkeyModifiers.Win,
        ["Cmd"] = HotkeyModifiers.Win
    };

    private static readonly Dictionary<string, (int VirtualKey, string Name)> KeyAliases = BuildKeyAliases();

    private static readonly (HotkeyModifiers Modifier, string Name)[] ModifierOrder =
    {
        (HotkeyModifiers.Control, "Ctrl"),
        (HotkeyModifiers.Alt, "Alt"),
        (HotkeyModifiers.Shift, "Shift"),
        (HotkeyModifiers.Win, "Win")
    };

    internal HotkeyGesture(HotkeyModifiers modifiers, int virtualKey, string? keyName)
    {
        Modifiers = modifiers;
        VirtualKey = virtualKey;
        KeyName = keyName;
        Display = BuildDisplay(modifiers, keyName);
    }

    public HotkeyModifiers Modifiers { get; }

    /// <summary>主键虚拟键码；修饰键组合（如 Ctrl+Alt）为 0。</summary>
    public int VirtualKey { get; }

    /// <summary>主键规范名称；修饰键组合为 null。</summary>
    public string? KeyName { get; }

    /// <summary>规范化后的展示文本，例如 <c>Ctrl+Alt+M</c>。</summary>
    public string Display { get; }

    public bool IsModifierOnly => VirtualKey == 0;

    public bool HasControl => (Modifiers & HotkeyModifiers.Control) != 0;

    public bool HasAlt => (Modifiers & HotkeyModifiers.Alt) != 0;

    public bool HasShift => (Modifiers & HotkeyModifiers.Shift) != 0;

    public bool HasWin => (Modifiers & HotkeyModifiers.Win) != 0;

    /// <summary>尝试解析热键字符串；非法输入返回 false。</summary>
    public static bool TryParse(string? text, out HotkeyGesture? gesture)
    {
        gesture = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var tokens = text.Split('+', StringSplitOptions.TrimEntries);
        if (tokens.Length is < 1 or > 6)
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        var virtualKey = 0;
        string? keyName = null;

        foreach (var token in tokens)
        {
            if (string.IsNullOrEmpty(token))
            {
                return false;
            }

            if (ModifierAliases.TryGetValue(token, out var modifier))
            {
                if ((modifiers & modifier) != 0)
                {
                    return false; // 重复修饰键
                }

                modifiers |= modifier;
                continue;
            }

            if (keyName is not null)
            {
                return false; // 只允许一个主键
            }

            if (!KeyAliases.TryGetValue(token, out var key))
            {
                return false;
            }

            virtualKey = key.VirtualKey;
            keyName = key.Name;
        }

        if (virtualKey == 0 && modifiers == HotkeyModifiers.None)
        {
            return false;
        }

        gesture = new HotkeyGesture(modifiers, virtualKey, keyName);
        return true;
    }

    /// <summary>解析热键字符串，非法时返回 null（不抛异常，便于配置容错）。</summary>
    public static HotkeyGesture? ParseOrNull(string? text) =>
        TryParse(text, out var gesture) ? gesture : null;

    /// <summary>判断热键字符串是否合法。</summary>
    public static bool IsValid(string? text) => TryParse(text, out _);

    /// <summary>把热键字符串规范化为展示文本；非法时返回空字符串。</summary>
    public static string Normalize(string? text)
    {
        if (TryParse(text, out var gesture) && gesture is not null)
        {
            return gesture.Display;
        }

        return string.Empty;
    }

    /// <summary>当前按下的虚拟键集合是否满足该热键（允许按住额外的键）。</summary>
    internal bool IsSatisfiedBy(IReadOnlySet<int> downKeys)
    {
        if (downKeys is null)
        {
            return false;
        }

        if (HasControl && !IsAnyDown(downKeys, VirtualKeys.Control, VirtualKeys.LeftControl, VirtualKeys.RightControl))
        {
            return false;
        }

        if (HasAlt && !IsAnyDown(downKeys, VirtualKeys.Alt, VirtualKeys.LeftAlt, VirtualKeys.RightAlt))
        {
            return false;
        }

        if (HasShift && !IsAnyDown(downKeys, VirtualKeys.Shift, VirtualKeys.LeftShift, VirtualKeys.RightShift))
        {
            return false;
        }

        if (HasWin && !IsAnyDown(downKeys, VirtualKeys.LeftWin, VirtualKeys.RightWin))
        {
            return false;
        }

        return VirtualKey == 0 || downKeys.Contains(VirtualKey);
    }

    public override string ToString() => Display;

    private static bool IsAnyDown(IReadOnlySet<int> downKeys, params int[] virtualKeys)
    {
        foreach (var key in virtualKeys)
        {
            if (downKeys.Contains(key))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildDisplay(HotkeyModifiers modifiers, string? keyName)
    {
        var builder = new StringBuilder();
        foreach (var (modifier, name) in ModifierOrder)
        {
            if ((modifiers & modifier) == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('+');
            }

            builder.Append(name);
        }

        if (!string.IsNullOrEmpty(keyName))
        {
            if (builder.Length > 0)
            {
                builder.Append('+');
            }

            builder.Append(keyName);
        }

        return builder.ToString();
    }

    private static Dictionary<string, (int VirtualKey, string Name)> BuildKeyAliases()
    {
        var map = new Dictionary<string, (int VirtualKey, string Name)>(StringComparer.OrdinalIgnoreCase);

        static void Add(Dictionary<string, (int, string)> target, string token, int virtualKey, string name)
        {
            target[token] = (virtualKey, name);
        }

        for (var c = 'A'; c <= 'Z'; c++)
        {
            var vk = 0x40 + (c - 'A' + 1);
            Add(map, c.ToString(), vk, c.ToString());
            Add(map, "Key" + c, vk, c.ToString());
        }

        for (var d = '0'; d <= '9'; d++)
        {
            var vk = 0x30 + (d - '0');
            Add(map, d.ToString(), vk, d.ToString());
            Add(map, "Digit" + d, vk, d.ToString());
            Add(map, "NumPad" + d, 0x60 + (d - '0'), d.ToString());
        }

        for (var i = 1; i <= 24; i++)
        {
            Add(map, "F" + i, 0x6F + i, "F" + i);
        }

        Add(map, "Esc", VirtualKeys.Escape, "Esc");
        Add(map, "Escape", VirtualKeys.Escape, "Esc");
        Add(map, "Space", VirtualKeys.Space, "Space");
        Add(map, "Spacebar", VirtualKeys.Space, "Space");
        Add(map, "Enter", VirtualKeys.Enter, "Enter");
        Add(map, "Return", VirtualKeys.Enter, "Enter");
        Add(map, "Tab", VirtualKeys.Tab, "Tab");
        Add(map, "Backspace", VirtualKeys.Backspace, "Backspace");
        Add(map, "Back", VirtualKeys.Backspace, "Backspace");
        Add(map, "Delete", VirtualKeys.Delete, "Delete");
        Add(map, "Del", VirtualKeys.Delete, "Delete");
        Add(map, "Insert", VirtualKeys.Insert, "Insert");
        Add(map, "Ins", VirtualKeys.Insert, "Insert");
        Add(map, "Home", VirtualKeys.Home, "Home");
        Add(map, "End", VirtualKeys.End, "End");
        Add(map, "PageUp", VirtualKeys.PageUp, "PageUp");
        Add(map, "PgUp", VirtualKeys.PageUp, "PageUp");
        Add(map, "PageDown", VirtualKeys.PageDown, "PageDown");
        Add(map, "PgDn", VirtualKeys.PageDown, "PageDown");
        Add(map, "Up", VirtualKeys.Up, "Up");
        Add(map, "ArrowUp", VirtualKeys.Up, "Up");
        Add(map, "Down", VirtualKeys.Down, "Down");
        Add(map, "ArrowDown", VirtualKeys.Down, "Down");
        Add(map, "Left", VirtualKeys.Left, "Left");
        Add(map, "ArrowLeft", VirtualKeys.Left, "Left");
        Add(map, "Right", VirtualKeys.Right, "Right");
        Add(map, "ArrowRight", VirtualKeys.Right, "Right");
        Add(map, "Pause", VirtualKeys.Pause, "Pause");
        Add(map, "CapsLock", VirtualKeys.CapsLock, "CapsLock");
        Add(map, "PrintScreen", VirtualKeys.PrintScreen, "PrintScreen");
        Add(map, "ScrollLock", VirtualKeys.ScrollLock, "ScrollLock");
        Add(map, "NumLock", VirtualKeys.NumLock, "NumLock");
        Add(map, "-", VirtualKeys.OemMinus, "-");
        Add(map, "=", VirtualKeys.OemPlus, "=");
        Add(map, "[", VirtualKeys.OemOpenBracket, "[");
        Add(map, "]", VirtualKeys.OemCloseBracket, "]");
        Add(map, ";", VirtualKeys.OemSemicolon, ";");
        Add(map, "'", VirtualKeys.OemQuote, "'");
        Add(map, ",", VirtualKeys.OemComma, ",");
        Add(map, ".", VirtualKeys.OemPeriod, ".");
        Add(map, "/", VirtualKeys.OemSlash, "/");
        Add(map, "\\", VirtualKeys.OemBackslash, "\\");
        Add(map, "`", VirtualKeys.OemTilde, "`");

        return map;
    }
}

/// <summary>用到的虚拟键码（Win32 VK_* 常量子集）。</summary>
internal static class VirtualKeys
{
    public const int Backspace = 0x08;
    public const int Tab = 0x09;
    public const int Enter = 0x0D;
    public const int Shift = 0x10;
    public const int Control = 0x11;
    public const int Alt = 0x12;
    public const int Pause = 0x13;
    public const int CapsLock = 0x14;
    public const int Escape = 0x1B;
    public const int Space = 0x20;
    public const int PageUp = 0x21;
    public const int PageDown = 0x22;
    public const int End = 0x23;
    public const int Home = 0x24;
    public const int Left = 0x25;
    public const int Up = 0x26;
    public const int Right = 0x27;
    public const int Down = 0x28;
    public const int PrintScreen = 0x2C;
    public const int Insert = 0x2D;
    public const int Delete = 0x2E;
    public const int LeftWin = 0x5B;
    public const int RightWin = 0x5C;
    public const int NumLock = 0x90;
    public const int ScrollLock = 0x91;
    public const int LeftShift = 0xA0;
    public const int RightShift = 0xA1;
    public const int LeftControl = 0xA2;
    public const int RightControl = 0xA3;
    public const int LeftAlt = 0xA4;
    public const int RightAlt = 0xA5;
    public const int OemSemicolon = 0xBA;
    public const int OemPlus = 0xBB;
    public const int OemComma = 0xBC;
    public const int OemMinus = 0xBD;
    public const int OemPeriod = 0xBE;
    public const int OemSlash = 0xBF;
    public const int OemTilde = 0xC0;
    public const int OemOpenBracket = 0xDB;
    public const int OemBackslash = 0xDC;
    public const int OemCloseBracket = 0xDD;
    public const int OemQuote = 0xDE;
}
