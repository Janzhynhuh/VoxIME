using System.Text;
using VoxIME.Core.Abstractions;
using VoxIME.Core.Configuration;

namespace VoxIME.Core.TextInsertion;

/// <summary>
/// 识别结果默认后处理：去控制字符/零宽字符、去首尾空白、术语替换（长串优先）、可选的尾部空格。
/// </summary>
public sealed class DefaultTextPostProcessor : ITextPostProcessor
{
    /// <summary>需要清掉的零宽/方向控制字符（不包含 ZWJ/ZWNJ，避免拆坏 emoji 组合序列）。</summary>
    private static readonly char[] InvisibleCharacters =
    {
        '\u200B', // 零宽空格
        '\u200E', // LRM
        '\u200F', // RLM
        '\u2060', // Word Joiner
        '\uFEFF', // BOM
        '\u202A', '\u202B', '\u202C', '\u202D', '\u202E'
    };

    public string Process(string rawText, InsertionConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrEmpty(rawText))
        {
            return string.Empty;
        }

        var text = RemoveInvisibleAndControl(rawText);

        if (config.TrimWhitespace)
        {
            text = text.Trim();
        }

        if (config.Replacements is { Count: > 0 })
        {
            text = ApplyReplacements(text, config.Replacements);
        }

        if (config.AppendSpace)
        {
            text = AppendTrailingSpace(text);
        }

        return text;
    }

    private static string RemoveInvisibleAndControl(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (Array.IndexOf(InvisibleCharacters, ch) >= 0)
            {
                continue;
            }

            if (char.IsControl(ch) && ch is not ('\r' or '\n' or '\t'))
            {
                continue;
            }

            if (ch == '\r')
            {
                // 统一换行风格为 \n；\r\n 只产生一个换行。
                builder.Append('\n');
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                continue;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static string ApplyReplacements(string text, IReadOnlyList<TextReplacement> replacements)
    {
        var ordered = replacements
            .Where(r => !string.IsNullOrEmpty(r.From))
            .OrderByDescending(r => r.From.Length)
            .ToList();

        foreach (var replacement in ordered)
        {
            text = text.Replace(replacement.From, replacement.To ?? string.Empty, StringComparison.Ordinal);
        }

        return text;
    }

    private static string AppendTrailingSpace(string text)
    {
        if (text.Length == 0)
        {
            return string.Empty;
        }

        return text.TrimEnd() + " ";
    }
}
