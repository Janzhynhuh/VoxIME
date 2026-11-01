using System.Text;

namespace VoxIME.Core.Asr.Providers;

/// <summary>
/// 本地 CLI 参数模板解析：按空白拆分参数，支持双引号 / 单引号包裹含空格的参数，
/// 引号内的 \" 与 \' 视为字面量。空引号（""）会保留为一个空参数。
/// </summary>
public static class CliArgumentParser
{
    /// <summary>把参数模板拆成参数列表（不做占位符替换）。</summary>
    public static IReadOnlyList<string> Split(string? arguments)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return result;
        }

        var current = new StringBuilder();
        var inQuotes = false;
        var quoteChar = '"';
        var hasToken = false;

        for (var i = 0; i < arguments.Length; i++)
        {
            var c = arguments[i];
            if (inQuotes)
            {
                if (c == '\\' && i + 1 < arguments.Length && arguments[i + 1] == quoteChar)
                {
                    current.Append(quoteChar);
                    i++;
                }
                else if (c == quoteChar)
                {
                    inQuotes = false;
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (hasToken)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                inQuotes = true;
                quoteChar = c;
                hasToken = true;
                continue;
            }

            current.Append(c);
            hasToken = true;
        }

        if (hasToken)
        {
            result.Add(current.ToString());
        }

        return result;
    }
}
