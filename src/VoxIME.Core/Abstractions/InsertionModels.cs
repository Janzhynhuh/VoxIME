using VoxIME.Core.Configuration;

namespace VoxIME.Core.Abstractions;

public sealed class InsertionResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public int CharacterCount { get; init; }
    public bool CopiedToClipboard { get; init; }

    public static InsertionResult Ok(int characterCount, bool copied = false) =>
        new() { Success = true, CharacterCount = characterCount, CopiedToClipboard = copied };

    public static InsertionResult Fail(string error, bool copied = false) =>
        new() { Success = false, Error = error, CopiedToClipboard = copied };
}

/// <summary>把文本送入当前焦点窗口。实现负责剪贴板粘贴/模拟输入两种模式。</summary>
public interface ITextInjector
{
    Task<InsertionResult> InsertAsync(string text, InsertionConfig options, CancellationToken cancellationToken = default);
}

/// <summary>系统剪贴板读写（Win32 实现，避免 Core 依赖 WPF）。</summary>
public interface IClipboardService
{
    void SetText(string text);

    /// <summary>读取文本；剪贴板被占用或格式不支持时返回 null。</summary>
    string? TryGetText();
}

/// <summary>识别结果后处理：去空白、术语替换、标点/空格策略。</summary>
public interface ITextPostProcessor
{
    string Process(string rawText, InsertionConfig config);
}
