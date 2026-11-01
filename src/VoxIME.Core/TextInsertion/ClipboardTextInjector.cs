using System.Globalization;
using VoxIME.Core.Abstractions;
using VoxIME.Core.Configuration;
using VoxIME.Core.Logging;

namespace VoxIME.Core.TextInsertion;

/// <summary>
/// 把文本送入当前焦点窗口：默认走"写剪贴板 + Ctrl+V"（兼容性最好），
/// 也可以走 SendInput 逐字模拟输入（不污染剪贴板）。
/// </summary>
/// <remarks>
/// <para>剪贴板模式会尽量保存并还原用户原来的剪贴板文本；失败时把待插入文本留在剪贴板作为兜底，
/// 让用户可以手动 Ctrl+V。</para>
/// <para>插入在后台完成，不假设调用线程是 UI 线程。</para>
/// </remarks>
public sealed class ClipboardTextInjector : ITextInjector
{
    private const int ClipboardFlushDelayMs = 20;
    private const int TypingDelayMs = 3;
    private const int EnterDelayMs = 30;

    private readonly IClipboardService _clipboard;
    private readonly IAppLogger _logger;
    private readonly IInputSimulator _input;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public ClipboardTextInjector(IClipboardService clipboard, IAppLogger logger)
        : this(clipboard, logger, new Win32InputSimulator(), static (delay, ct) => Task.Delay(delay, ct))
    {
    }

    internal ClipboardTextInjector(
        IClipboardService clipboard,
        IAppLogger logger,
        IInputSimulator input,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _logger = logger ?? NullLogger.Instance;
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _delay = delay ?? (static (d, ct) => Task.Delay(d, ct));
    }

    public async Task<InsertionResult> InsertAsync(string text, InsertionConfig options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrEmpty(text))
        {
            return InsertionResult.Ok(0);
        }

        try
        {
            return options.Mode == InsertionMode.SimulateTyping
                ? await SimulateTypingAsync(text, options, cancellationToken).ConfigureAwait(false)
                : await PasteAsync(text, options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.Debug("文本插入已取消。");
            return InsertionResult.Fail("文本插入已取消。");
        }
        catch (Exception ex)
        {
            _logger.Error("文本插入失败", ex);
            var copied = TryFallbackCopy(text);
            return InsertionResult.Fail($"文本插入失败：{ex.Message}", copied);
        }
    }

    private async Task<InsertionResult> PasteAsync(string text, InsertionConfig options, CancellationToken cancellationToken)
    {
        string? originalText = null;
        if (options.RestoreClipboard)
        {
            try
            {
                originalText = _clipboard.TryGetText();
            }
            catch (Exception ex)
            {
                _logger.Warn("读取原剪贴板内容失败，稍后无法还原：" + ex.Message);
            }
        }

        try
        {
            _clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            _logger.Error("写入剪贴板失败", ex);
            var copied = TryFallbackCopy(text);
            return InsertionResult.Fail($"剪贴板不可用：{ex.Message}", copied);
        }

        // 给剪贴板留一点时间落盘，再发 Ctrl+V。
        await _delay(TimeSpan.FromMilliseconds(ClipboardFlushDelayMs), cancellationToken).ConfigureAwait(false);

        if (!_input.SendPasteShortcut())
        {
            _logger.Warn("发送 Ctrl+V 失败。");
            var copied = TryFallbackCopy(text);
            return InsertionResult.Fail("发送粘贴快捷键失败，文本已尽量留在剪贴板。", copied);
        }

        var pasteDelay = Math.Clamp(options.PasteDelayMs, 0, 2000);
        await _delay(TimeSpan.FromMilliseconds(pasteDelay), cancellationToken).ConfigureAwait(false);

        if (options.PressEnterAfterInsert)
        {
            await _delay(TimeSpan.FromMilliseconds(EnterDelayMs), cancellationToken).ConfigureAwait(false);
            if (!_input.SendEnter())
            {
                _logger.Warn("发送回车键失败，文本已插入但未换行。");
            }

            await _delay(TimeSpan.FromMilliseconds(EnterDelayMs), cancellationToken).ConfigureAwait(false);
        }

        var restored = false;
        if (options.RestoreClipboard && originalText is not null)
        {
            try
            {
                _clipboard.SetText(originalText);
                restored = true;
            }
            catch (Exception ex)
            {
                _logger.Warn("还原原剪贴板内容失败：" + ex.Message);
            }
        }

        return InsertionResult.Ok(text.Length, copied: !restored);
    }

    private async Task<InsertionResult> SimulateTypingAsync(string text, InsertionConfig options, CancellationToken cancellationToken)
    {
        foreach (var element in EnumerateTextElements(text))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (element is "\r\n" or "\n" or "\r")
            {
                if (!_input.SendEnter())
                {
                    return FailWithFallback("模拟回车键失败。", text);
                }
            }
            else if (!_input.SendUnicodeChar(element))
            {
                return FailWithFallback($"模拟输入字符“{element}”失败。", text);
            }

            await _delay(TimeSpan.FromMilliseconds(TypingDelayMs), cancellationToken).ConfigureAwait(false);
        }

        if (options.PressEnterAfterInsert)
        {
            await _delay(TimeSpan.FromMilliseconds(EnterDelayMs), cancellationToken).ConfigureAwait(false);
            if (!_input.SendEnter())
            {
                _logger.Warn("发送回车键失败，文本已输入但未换行。");
            }
        }

        return InsertionResult.Ok(text.Length);
    }

    private InsertionResult FailWithFallback(string error, string text)
    {
        _logger.Warn(error);
        var copied = TryFallbackCopy(text);
        return InsertionResult.Fail(error + "（文本已尽量留在剪贴板）", copied);
    }

    private bool TryFallbackCopy(string text)
    {
        try
        {
            _clipboard.SetText(text);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn("把文本写入剪贴板兜底失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>按字素（文本元素）切分，代理对（emoji 等）不会被拆坏。</summary>
    private static IEnumerable<string> EnumerateTextElements(string text)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            if (!string.IsNullOrEmpty(element))
            {
                yield return element;
            }
        }
    }
}
