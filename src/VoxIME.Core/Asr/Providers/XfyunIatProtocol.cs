using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using VoxIME.Core.Abstractions;

namespace VoxIME.Core.Asr.Providers;

/// <summary>
/// 讯飞语音听写（流式版 IAT）WebSocket 协议工具：鉴权 URL 签名、音频分帧、结果文本拼装。
/// 文档：https://www.xfyun.cn/doc/asr/voicedictation/API.html
/// </summary>
/// <remarks>
/// 鉴定算法：signature_origin = "host: {host}\ndate: {date}\nGET {path} HTTP/1.1"，
/// hmac-sha256(signature_origin, apiSecret) → base64 后放进 authorization 原文，再整体 base64 作为 query 参数。
/// 音频帧：16k/16bit/单声道 PCM 每帧 1280 字节（40ms），首帧 status=0 带 common/business，中间帧 status=1，末帧 status=2。
/// </remarks>
public static class XfyunIatProtocol
{
    public const string DefaultHost = "iat-api.xfyun.cn";
    public const string DefaultPath = "/v2/iat";
    public const string DefaultWebSocketUrl = "wss://iat-api.xfyun.cn/v2/iat";
    public const int FrameBytes = 1280;
    public const int FrameIntervalMilliseconds = 40;
    public const string Algorithm = "hmac-sha256";
    public const string SignedHeaders = "host date request-line";

    /// <summary>构造 RFC1123 格式的 date（UTC，带 GMT）。</summary>
    public static string BuildDate(DateTimeOffset timestamp) =>
        timestamp.UtcDateTime.ToString("r", CultureInfo.InvariantCulture);

    /// <summary>构造参与签名的 request-line。</summary>
    public static string BuildRequestLine(string path) =>
        $"GET {(string.IsNullOrEmpty(path) ? DefaultPath : path)} HTTP/1.1";

    /// <summary>构造签名原文（signature_origin）。</summary>
    public static string BuildSignatureOrigin(string host, string date, string requestLine) =>
        $"host: {host}\ndate: {date}\n{requestLine}";

    /// <summary>HMAC-SHA256 后 Base64（即签名 signature）。</summary>
    public static string BuildSignature(string signatureOrigin, string apiSecret)
    {
        ArgumentNullException.ThrowIfNull(signatureOrigin);
        ArgumentNullException.ThrowIfNull(apiSecret);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(apiSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(signatureOrigin));
        return Convert.ToBase64String(hash);
    }

    /// <summary>构造 authorization 原文（base64 之前）。</summary>
    public static string BuildAuthorizationOrigin(string apiKey, string signature) =>
        $"api_key=\"{apiKey}\", algorithm=\"{Algorithm}\", headers=\"{SignedHeaders}\", signature=\"{signature}\"";

    /// <summary>构造完整鉴权 WebSocket URL（query：authorization / date / host）。</summary>
    public static string BuildAuthUrl(Uri baseUri, string apiKey, string apiSecret, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiSecret);

        var host = baseUri.IsDefaultPort ? baseUri.Host : $"{baseUri.Host}:{baseUri.Port}";
        var path = string.IsNullOrEmpty(baseUri.AbsolutePath) ? DefaultPath : baseUri.AbsolutePath;
        var date = BuildDate(timestamp);
        var requestLine = BuildRequestLine(path);
        var signatureOrigin = BuildSignatureOrigin(host, date, requestLine);
        var signature = BuildSignature(signatureOrigin, apiSecret);
        var authorizationOrigin = BuildAuthorizationOrigin(apiKey, signature);
        var authorization = Convert.ToBase64String(Encoding.UTF8.GetBytes(authorizationOrigin));

        var builder = new StringBuilder(baseUri.GetLeftPart(UriPartial.Path));
        builder.Append("?authorization=").Append(Uri.EscapeDataString(authorization));
        builder.Append("&date=").Append(Uri.EscapeDataString(date));
        builder.Append("&host=").Append(Uri.EscapeDataString(host));
        return builder.ToString();
    }

    /// <summary>把语言代码映射成讯飞 business.language（默认 zh_cn，英文 en_us）。</summary>
    public static string MapLanguage(string? language)
    {
        var normalized = AsrHttpSupport.NormalizeLanguage(language);
        return normalized.ToLowerInvariant() switch
        {
            "en" or "en-us" or "en_us" or "en-gb" or "en_ca" => "en_us",
            _ => "zh_cn"
        };
    }

    /// <summary>按 1280 字节切分 PCM 帧。</summary>
    public static IReadOnlyList<byte[]> SplitAudioFrames(byte[] pcm16, int frameBytes = FrameBytes)
    {
        ArgumentNullException.ThrowIfNull(pcm16);
        if (frameBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameBytes), "帧大小必须为正数。");
        }

        var frames = new List<byte[]>((pcm16.Length + frameBytes - 1) / frameBytes);
        for (var offset = 0; offset < pcm16.Length; offset += frameBytes)
        {
            var length = Math.Min(frameBytes, pcm16.Length - offset);
            var chunk = new byte[length];
            Buffer.BlockCopy(pcm16, offset, chunk, 0, length);
            frames.Add(chunk);
        }

        return frames;
    }

    /// <summary>
    /// 构造完整的讯飞请求帧序列（JSON 字符串）：
    /// 首帧 status=0 带 common/business/first audio，中间帧 status=1，最后追加 status=2 结束帧。
    /// </summary>
    public static IReadOnlyList<string> BuildFrames(
        byte[] pcm16,
        int sampleRate,
        JsonObject common,
        JsonObject business)
    {
        ArgumentNullException.ThrowIfNull(pcm16);
        ArgumentNullException.ThrowIfNull(common);
        ArgumentNullException.ThrowIfNull(business);

        if (pcm16.Length == 0)
        {
            throw new AsrException("音频数据为空，无法发送给讯飞识别。", AsrProviderTypeIds.Xfyun);
        }

        var rate = sampleRate == 8000 ? 8000 : 16000;
        var format = $"audio/L16;rate={rate}";
        var chunks = SplitAudioFrames(pcm16);
        var frames = new List<string>(chunks.Count + 1);

        for (var index = 0; index < chunks.Count; index++)
        {
            var data = new JsonObject
            {
                ["status"] = index == 0 ? 0 : 1,
                ["format"] = format,
                ["encoding"] = "raw",
                ["audio"] = Convert.ToBase64String(chunks[index])
            };

            var frame = new JsonObject();
            if (index == 0)
            {
                frame["common"] = common.DeepClone();
                frame["business"] = business.DeepClone();
            }

            frame["data"] = data;
            frames.Add(frame.ToJsonString(AsrHttpSupport.JsonWriteOptions));
        }

        // 数据结束标识：只发 status=2。
        frames.Add(new JsonObject { ["data"] = new JsonObject { ["status"] = 2 } }
            .ToJsonString(AsrHttpSupport.JsonWriteOptions));
        return frames;
    }
}
