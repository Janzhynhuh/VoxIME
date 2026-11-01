namespace VoxIME.Core.TextInsertion;

/// <summary>
/// 键盘模拟注入点：生产环境用 Win32 SendInput 实现，单元测试用伪造实现，
/// 从而让上屏逻辑完全离线可测。
/// </summary>
internal interface IInputSimulator
{
    /// <summary>发送 Ctrl+V 粘贴快捷键。</summary>
    bool SendPasteShortcut();

    /// <summary>发送回车键。</summary>
    bool SendEnter();

    /// <summary>用 KEYEVENTF_UNICODE 输入一个"文本元素"（可能含代理对的两个 UTF-16 码元）。</summary>
    bool SendUnicodeChar(string textElement);
}

/// <summary>基于 SendInput 的真实键盘模拟。</summary>
internal sealed class Win32InputSimulator : IInputSimulator
{
    public bool SendPasteShortcut()
    {
        var inputs = new[]
        {
            CreateKey(TextInsertionNativeMethods.VK_CONTROL, keyUp: false, unicode: false),
            CreateKey(TextInsertionNativeMethods.VK_V, keyUp: false, unicode: false),
            CreateKey(TextInsertionNativeMethods.VK_V, keyUp: true, unicode: false),
            CreateKey(TextInsertionNativeMethods.VK_CONTROL, keyUp: true, unicode: false)
        };

        return Send(inputs);
    }

    public bool SendEnter()
    {
        var inputs = new[]
        {
            CreateKey(TextInsertionNativeMethods.VK_RETURN, keyUp: false, unicode: false),
            CreateKey(TextInsertionNativeMethods.VK_RETURN, keyUp: true, unicode: false)
        };

        return Send(inputs);
    }

    public bool SendUnicodeChar(string textElement)
    {
        if (string.IsNullOrEmpty(textElement))
        {
            return false;
        }

        // 代理对（emoji 等）是两个 UTF-16 码元，KEYEVENTF_UNICODE 需要逐码元发送。
        var inputs = new TextInsertionNativeMethods.INPUT[textElement.Length * 2];
        for (var i = 0; i < textElement.Length; i++)
        {
            inputs[i * 2] = CreateKey(textElement[i], keyUp: false, unicode: true);
            inputs[i * 2 + 1] = CreateKey(textElement[i], keyUp: true, unicode: true);
        }

        return Send(inputs);
    }

    private static bool Send(TextInsertionNativeMethods.INPUT[] inputs)
    {
        if (inputs.Length == 0)
        {
            return false;
        }

        var size = System.Runtime.InteropServices.Marshal.SizeOf<TextInsertionNativeMethods.INPUT>();
        var sent = TextInsertionNativeMethods.SendInput((uint)inputs.Length, inputs, size);
        return sent == inputs.Length;
    }

    private static TextInsertionNativeMethods.INPUT CreateKey(int virtualKey, bool keyUp, bool unicode)
    {
        uint flags = 0;
        if (keyUp)
        {
            flags |= TextInsertionNativeMethods.KEYEVENTF_KEYUP;
        }

        if (unicode)
        {
            flags |= TextInsertionNativeMethods.KEYEVENTF_UNICODE;
        }

        return new TextInsertionNativeMethods.INPUT
        {
            type = TextInsertionNativeMethods.INPUT_KEYBOARD,
            U = new TextInsertionNativeMethods.InputUnion
            {
                ki = new TextInsertionNativeMethods.KEYBDINPUT
                {
                    wVk = unicode ? (ushort)0 : (ushort)virtualKey,
                    wScan = unicode ? (ushort)virtualKey : (ushort)0,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }
}
