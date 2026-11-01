using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace VoxIME.App.Infrastructure;

/// <summary>
/// 热键手势字符串的解析 / 规范化 / 校验（与 Core 的热键服务解耦，独立实现一份 UI 侧校验）。
/// 支持格式：Ctrl+Alt、Ctrl+Shift+Space、F2、Esc、Ctrl+Alt+M。
/// </summary>
public static class HotkeyGesture
{
    private static readonly Dictionary<string, string> ModifierAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = "Ctrl",
        ["control"] = "Ctrl",
        ["alt"] = "Alt",
        ["shift"] = "Shift",
        ["win"] = "Win",
        ["windows"] = "Win",
        ["meta"] = "Win"
    };

    /// <summary>解析并规范化；失败时通过 error 返回中文提示。</summary>
    public static bool TryNormalize(string? input, out string normalized, out string? error)
    {
        normalized = "";
        error = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "热键不能为空";
            return false;
        }

        var raw = input.Trim().Replace(" ", "");
        var parts = raw.Split('+', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            error = "热键格式不正确";
            return false;
        }

        var modifiers = new List<string>();
        string? key = null;

        foreach (var part in parts)
        {
            if (ModifierAliases.TryGetValue(part, out var modifier))
            {
                if (modifiers.Contains(modifier))
                {
                    error = $"修饰键 {modifier} 重复";
                    return false;
                }

                modifiers.Add(modifier);
                continue;
            }

            if (key is not null)
            {
                error = "只能包含一个主按键（例如 Ctrl+Alt+M）";
                return false;
            }

            key = NormalizeKeyName(part);
            if (key is null)
            {
                error = $"无法识别的按键“{part}”";
                return false;
            }
        }

        if (key is null && modifiers.Count == 0)
        {
            error = "热键格式不正确";
            return false;
        }

        if (key is null && modifiers.Count == 1)
        {
            error = "至少需要两个键（例如 Ctrl+Alt）或一个功能键（例如 F2）";
            return false;
        }

        // 修饰键按固定顺序排列，保证配置稳定。
        var ordered = new List<string>();
        foreach (var name in new[] { "Ctrl", "Alt", "Shift", "Win" })
        {
            if (modifiers.Contains(name))
            {
                ordered.Add(name);
            }
        }

        if (key is not null)
        {
            ordered.Add(key);
        }

        normalized = string.Join("+", ordered);
        if (normalized.Length == 0)
        {
            error = "热键格式不正确";
            return false;
        }

        return true;
    }

    /// <summary>把 WPF 的按键组合格式化为配置字符串。</summary>
    public static string Format(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }

        var keyName = GetKeyName(key);
        if (!string.IsNullOrEmpty(keyName))
        {
            parts.Add(keyName);
        }

        return string.Join("+", parts);
    }

    /// <summary>规范化单个按键名；无法识别返回 null。</summary>
    public static string? NormalizeKeyName(string part)
    {
        if (string.IsNullOrWhiteSpace(part))
        {
            return null;
        }

        var token = part.Trim();

        if (token.Length == 1 && char.IsLetterOrDigit(token[0]))
        {
            return char.ToUpperInvariant(token[0]).ToString();
        }

        if (token.Length == 1)
        {
            return token switch
            {
                "+" => "Plus",
                "-" => "Minus",
                "," => "Comma",
                "." => "Period",
                "/" => "Slash",
                ";" => "Semicolon",
                "'" => "Quote",
                "[" => "OpenBracket",
                "]" => "CloseBracket",
                "\\" => "Backslash",
                "`" => "Tilde",
                _ => null
            };
        }

        if (token.StartsWith("F", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(token[1..], out var fn) && fn is >= 1 and <= 24)
        {
            return $"F{fn}";
        }

        if (token.StartsWith("D", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(token[1..], out var digit) && digit is >= 0 and <= 9 && token.Length == 2)
        {
            return digit.ToString(CultureInfo.InvariantCulture);
        }

        if (token.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase))
        {
            var rest = token[6..];
            return rest.Length == 0 ? null : "Num" + rest.ToUpperInvariant();
        }

        var known = token.ToLowerInvariant() switch
        {
            "esc" or "escape" => "Esc",
            "space" => "Space",
            "enter" or "return" => "Enter",
            "tab" => "Tab",
            "back" or "backspace" => "Backspace",
            "delete" or "del" => "Delete",
            "insert" or "ins" => "Insert",
            "home" => "Home",
            "end" => "End",
            "pageup" or "pgup" => "PageUp",
            "pagedown" or "pgdn" => "PageDown",
            "up" => "Up",
            "down" => "Down",
            "left" => "Left",
            "right" => "Right",
            "plus" => "Plus",
            "minus" => "Minus",
            "comma" => "Comma",
            "period" => "Period",
            "slash" => "Slash",
            "semicolon" => "Semicolon",
            "quote" => "Quote",
            "openbracket" => "OpenBracket",
            "closebracket" => "CloseBracket",
            "backslash" => "Backslash",
            "tilde" => "Tilde",
            "capslock" => "CapsLock",
            "printscreen" => "PrintScreen",
            "pause" => "Pause",
            "scrolllock" => "ScrollLock",
            "apps" => "Apps",
            _ => null
        };

        if (known is not null)
        {
            return known;
        }

        // 未知但形如合法标识符的按键（例如媒体键 MediaPlayPause）也接受，交给 Core 校验。
        if (token.All(char.IsLetterOrDigit) && token.Length is >= 2 and <= 24)
        {
            return token;
        }

        return null;
    }

    private static string? GetKeyName(Key key)
    {
        if (key == Key.None)
        {
            return null;
        }

        return key switch
        {
            Key.LeftCtrl or Key.RightCtrl => "Ctrl",
            Key.LeftAlt or Key.RightAlt => "Alt",
            Key.LeftShift or Key.RightShift => "Shift",
            Key.LWin or Key.RWin => "Win",
            Key.Escape => "Esc",
            Key.Return => "Enter",
            Key.Back => "Backspace",
            Key.Delete => "Delete",
            Key.Insert => "Insert",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Left => "Left",
            Key.Right => "Right",
            Key.Space => "Space",
            Key.Tab => "Tab",
            Key.Capital => "CapsLock",
            Key.PrintScreen => "PrintScreen",
            Key.Pause => "Pause",
            Key.Scroll => "ScrollLock",
            Key.Apps => "Apps",
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(CultureInfo.InvariantCulture),
            >= Key.NumPad0 and <= Key.NumPad9 => "NumPad" + (int)(key - Key.NumPad0),
            >= Key.A and <= Key.Z => key.ToString().ToUpperInvariant(),
            >= Key.F1 and <= Key.F24 => key.ToString().ToUpperInvariant(),
            Key.OemPlus => "Plus",
            Key.OemMinus => "Minus",
            Key.OemComma => "Comma",
            Key.OemPeriod => "Period",
            Key.OemQuestion => "Slash",
            Key.OemSemicolon => "Semicolon",
            Key.OemQuotes => "Quote",
            Key.OemOpenBrackets => "OpenBracket",
            Key.OemCloseBrackets => "CloseBracket",
            Key.OemPipe => "Backslash",
            Key.OemTilde => "Tilde",
            _ => null
        };
    }
}

/// <summary>
/// 附加行为：让 TextBox 直接“录下”用户按下的组合键（Text 双向绑定，UpdateSourceTrigger=PropertyChanged）。
/// </summary>
public static class HotkeyCapture
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(HotkeyCapture),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box)
        {
            return;
        }

        box.PreviewKeyDown -= OnPreviewKeyDown;
        if (e.NewValue is true)
        {
            box.PreviewKeyDown += OnPreviewKeyDown;
        }
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // 单独的纯修饰键按下时展示半成品提示，方便用户看到当前组合。
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            var partial = HotkeyGesture.Format(Keyboard.Modifiers, Key.None);
            box.Text = string.IsNullOrEmpty(partial) ? "" : partial;
            e.Handled = true;
            return;
        }

        var gesture = HotkeyGesture.Format(Keyboard.Modifiers, key);
        if (HotkeyGesture.TryNormalize(gesture, out var normalized, out _))
        {
            box.Text = normalized;
        }

        e.Handled = true;
    }
}
