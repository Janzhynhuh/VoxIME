using System.Text;
using System.Text.Json.Nodes;

namespace VoxIME.Core.Asr.Providers;

/// <summary>
/// 讯飞 IAT 结果拼装器：按 sn 保存各片结果，支持动态修正（pgs=apd 追加 / pgs=rpl 替换 rg 范围）。
/// 对应文档“动态修正返回参数”：pgs 为 "apd" 时追加，为 "rpl" 时按 rg=[起,止] 替换前面的结果。
/// </summary>
/// <remarks>
/// 兼容处理：个别旧文档/第三方实现把替换范围字段写成 rl（文档现为 rg），这里两个字段都接受；
/// ws[].cw[].wp 为保留字段，忽略；文本取每个 ws 的第一个 cw.w。
/// </remarks>
public sealed class XfyunTranscriptAssembler
{
    private readonly SortedDictionary<int, string> _segments = new();

    /// <summary>当前已拼装的文本。</summary>
    public string GetText()
    {
        var builder = new StringBuilder();
        foreach (var segment in _segments.Values)
        {
            builder.Append(segment);
        }

        return builder.ToString();
    }

    /// <summary>清空已拼装内容。</summary>
    public void Reset() => _segments.Clear();

    /// <summary>处理一个 data.result 节点（对应响应中的 data.result）。</summary>
    public void Append(JsonObject? result)
    {
        if (result is null)
        {
            return;
        }

        var text = ExtractText(result) ?? "";
        var sn = AsrHttpSupport.GetInt(result["sn"]) ?? (_segments.Count + 1);
        var pgs = AsrHttpSupport.GetString(result["pgs"]);

        if (string.Equals(pgs, "rpl", StringComparison.OrdinalIgnoreCase))
        {
            var range = ReadReplaceRange(result);
            if (range is { } replaceRange)
            {
                for (var index = replaceRange.From; index <= replaceRange.To; index++)
                {
                    _segments.Remove(index);
                }
            }
        }

        // apd（追加最终结果）与普通结果都按 sn 覆盖写入，最后按 sn 顺序拼接。
        _segments[sn] = text;
    }

    /// <summary>从 data.result 中提取本片文本（ws[].cw[0].w 依次拼接）。</summary>
    public static string? ExtractText(JsonObject result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result["ws"] is not JsonArray words)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var word in words)
        {
            if (word is not JsonObject wordObject || wordObject["cw"] is not JsonArray candidates)
            {
                continue;
            }

            foreach (var candidate in candidates)
            {
                if (candidate is not JsonObject candidateObject)
                {
                    continue;
                }

                var text = AsrHttpSupport.GetString(candidateObject["w"]);
                if (!string.IsNullOrEmpty(text))
                {
                    builder.Append(text);
                    break;
                }
            }
        }

        return builder.ToString();
    }

    private static (int From, int To)? ReadReplaceRange(JsonObject result)
    {
        foreach (var key in new[] { "rg", "rl" })
        {
            if (result[key] is not JsonArray range || range.Count < 2)
            {
                continue;
            }

            var from = AsrHttpSupport.GetInt(range[0]);
            var to = AsrHttpSupport.GetInt(range[1]);
            if (from is not null && to is not null)
            {
                return (Math.Min(from.Value, to.Value), Math.Max(from.Value, to.Value));
            }
        }

        return null;
    }
}
