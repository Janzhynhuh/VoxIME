using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VoxIME.Core.Abstractions;
using VoxIME.Core.Logging;

namespace VoxIME.Core.Asr;

/// <summary>
/// ASR 适配器公共工具：User-Agent、超时控制、HTTP 错误映射、JSON 合并、字段读取。
/// 私有（internal）实现细节，不对外暴露契约。
/// </summary>
internal static class AsrHttpSupport
{
    /// <summary>所有识别请求统一的 User-Agent。</summary>
    public const string UserAgent = "VoxIME/0.1";

    /// <summary>错误响应体最多保留的字符数（避免日志/异常过大）。</summary>
    public const int MaxResponseBodyChars = 1000;

    public static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        WriteIndented = false
    };

    // ---------------------------------------------------------------- 请求装饰

    /// <summary>统一追加 User-Agent: VoxIME/0.1。</summary>
    public static void ApplyUserAgent(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
    }

    /// <summary>有 API Key 时追加 Authorization: Bearer。</summary>
    public static void ApplyBearerAuth(HttpRequestMessage request, string? apiKey)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }
    }

    /// <summary>有 API Key 时追加指定名称的鉴权头（如 api-key / x-api-key）。</summary>
    public static void ApplyApiKeyHeader(HttpRequestMessage request, string headerName, string? apiKey)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.TryAddWithoutValidation(headerName, apiKey.Trim());
        }
    }

    // ---------------------------------------------------------------- URL 拼装

    /// <summary>
    /// 拼接接口地址：先去掉 BaseUrl 末尾的空白与 '/'；若已经以 <paramref name="suffix"/>
    /// 结尾（不区分大小写）则直接使用，否则追加。例如：
    /// https://api.openai.com/v1 + /audio/transcriptions => https://api.openai.com/v1/audio/transcriptions。
    /// </summary>
    public static string BuildEndpoint(string baseUrl, string suffix)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return "";
        }

        if (string.IsNullOrWhiteSpace(suffix))
        {
            return baseUrl.Trim().TrimEnd('/');
        }

        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return trimmed + (suffix.StartsWith('/') ? suffix : "/" + suffix);
    }

    // ---------------------------------------------------------------- 超时与 HTTP

    /// <summary>
    /// 以调用方 CancellationToken 为基础，叠加 profile.TimeoutSeconds 的软超时。
    /// 注意：HttpClient.Timeout 必须是 InfiniteTimeSpan（由装配方负责），真正超时这里控制。
    /// </summary>
    public static CancellationTokenSource CreateTimeoutSource(int timeoutSeconds, CancellationToken cancellationToken)
    {
        var seconds = Math.Clamp(timeoutSeconds, 1, 3600);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        return cts;
    }

    /// <summary>
    /// 发送请求并读取响应文本。非 2xx 映射为带状态码与响应片段的 <see cref="AsrException"/>；
    /// 超时（调用方未取消的前提下）与网络异常同样映射为中文 AsrException。
    /// </summary>
    public static async Task<string> PostForBodyAsync(
        HttpClient httpClient,
        HttpRequestMessage request,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        string providerId,
        string action,
        IAppLogger? logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(request);

        var seconds = Math.Clamp(timeoutSeconds, 1, 3600);
        using var timeoutCts = CreateTimeoutSource(seconds, cancellationToken);
        try
        {
            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                .ConfigureAwait(false);

            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new AsrException($"{action}读取响应内容失败：{ex.Message}", providerId, innerException: ex);
            }

            if (!response.IsSuccessStatusCode)
            {
                var snippet = Truncate(body);
                logger?.Warn($"ASR[{providerId}] {action} 返回 HTTP {(int)response.StatusCode}（{response.StatusCode}）。响应片段：{snippet}");
                throw CreateHttpException(providerId, action, response.StatusCode, snippet);
            }

            return body;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger?.Warn($"ASR[{providerId}] {action} 超时（{seconds} 秒）。");
            throw new AsrException(
                $"{action}超时（{seconds} 秒内未完成），请检查网络或调大该服务的超时时间。",
                providerId,
                innerException: ex);
        }
        catch (HttpRequestException ex)
        {
            logger?.Warn($"ASR[{providerId}] {action} 网络请求失败：{ex.Message}");
            throw new AsrException($"{action}网络请求失败：{ex.Message}", providerId, innerException: ex);
        }
    }

    /// <summary>构造 HTTP 状态码错误（含响应体前 1000 字符）。</summary>
    public static AsrException CreateHttpException(
        string providerId,
        string action,
        HttpStatusCode statusCode,
        string? responseBody)
    {
        var snippet = Truncate(responseBody);
        var message = $"{action}失败：HTTP {(int)statusCode} {statusCode}。";
        if (!string.IsNullOrEmpty(snippet))
        {
            message += $" 响应内容：{snippet}";
        }

        return new AsrException(message, providerId, (int)statusCode, snippet);
    }

    /// <summary>构造“响应不合法”错误。</summary>
    public static AsrException CreateInvalidResponseException(string providerId, string message, string? responseBody)
    {
        var snippet = Truncate(responseBody);
        var full = snippet.Length == 0 ? message : $"{message} 响应内容：{snippet}";
        return new AsrException(full, providerId, responseBody: snippet);
    }

    /// <summary>截断文本（默认 1000 字符）。</summary>
    public static string Truncate(string? text, int max = MaxResponseBodyChars)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        return text.Length <= max ? text : text[..max] + "…";
    }

    // ---------------------------------------------------------------- JSON 工具

    /// <summary>宽松解析 JSON 对象，失败返回 null。</summary>
    public static JsonObject? TryParseJsonObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 合并 profile.ExtraJson 与调用级 ExtraJson（后者优先）。必须是 JSON 对象，否则抛中文 AsrException。
    /// </summary>
    public static JsonObject MergeExtraJson(string? profileExtraJson, string? callExtraJson, string providerId)
    {
        var result = new JsonObject();
        MergeExtraJsonInto(result, profileExtraJson, providerId, "服务配置中的 ExtraJson");
        MergeExtraJsonInto(result, callExtraJson, providerId, "本次调用的 ExtraJson");
        return result;
    }

    private static void MergeExtraJsonInto(JsonObject target, string? raw, string providerId, string sourceName)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(raw);
        }
        catch (JsonException ex)
        {
            throw new AsrException($"{sourceName} 不是合法的 JSON：{ex.Message}", providerId, innerException: ex);
        }

        if (parsed is not JsonObject obj)
        {
            throw new AsrException($"{sourceName} 必须是 JSON 对象（形如 {{\"key\": value}}）。", providerId);
        }

        DeepMerge(target, obj);
    }

    /// <summary>把 source 深度合并进 target（对象递归合并，其余覆盖；写入前 DeepClone 避免节点双父异常）。</summary>
    public static void DeepMerge(JsonObject target, JsonObject? source)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (source is null)
        {
            return;
        }

        foreach (var pair in source)
        {
            if (pair.Value is JsonObject sourceChild && target[pair.Key] is JsonObject targetChild)
            {
                DeepMerge(targetChild, sourceChild);
            }
            else
            {
                target[pair.Key] = pair.Value?.DeepClone();
            }
        }
    }

    /// <summary>读取字符串值（数字/布尔会转成文本，对象/数组返回 null）。</summary>
    public static string? GetString(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonValue value:
                if (value.TryGetValue<string>(out var text))
                {
                    return text;
                }

                if (value.TryGetValue<bool>(out var flag))
                {
                    return flag ? "true" : "false";
                }

                return value.ToJsonString();
            default:
                return null;
        }
    }

    /// <summary>读取整数（兼容数字、数字字符串、布尔）。</summary>
    public static int? GetInt(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonValue value:
                if (value.TryGetValue<int>(out var intValue))
                {
                    return intValue;
                }

                if (value.TryGetValue<long>(out var longValue))
                {
                    return (int)longValue;
                }

                if (value.TryGetValue<double>(out var doubleValue))
                {
                    return (int)doubleValue;
                }

                if (value.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed))
                {
                    return parsed;
                }

                return null;
            default:
                return null;
        }
    }

    /// <summary>读取布尔值（兼容 true/false、"true"/"false"、1/0）。</summary>
    public static bool? GetBool(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonValue value:
                if (value.TryGetValue<bool>(out var flag))
                {
                    return flag;
                }

                if (value.TryGetValue<int>(out var intValue))
                {
                    return intValue != 0;
                }

                if (value.TryGetValue<string>(out var text))
                {
                    return text.Trim().ToLowerInvariant() switch
                    {
                        "true" or "1" or "yes" => true,
                        "false" or "0" or "no" => false,
                        _ => null
                    };
                }

                return null;
            default:
                return null;
        }
    }

    /// <summary>把 JSON 标量转 multipart 文本；对象/数组序列化为 JSON 文本（null 返回 null 表示跳过）。</summary>
    public static string? ExtraValueAsString(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonValue value:
                if (value.TryGetValue<string>(out var text))
                {
                    return text;
                }

                if (value.TryGetValue<bool>(out var flag))
                {
                    return flag ? "true" : "false";
                }

                return value.ToJsonString();
            default:
                return node.ToJsonString(JsonWriteOptions);
        }
    }

    // ---------------------------------------------------------------- 语言

    /// <summary>规范化语言代码；空值/auto 系列返回 "auto"。</summary>
    public static string NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return "auto";
        }

        var trimmed = language.Trim();
        return trimmed.ToLowerInvariant() switch
        {
            "auto" or "auto-detect" or "auto_detect" or "autodetect" or "自动" or "自动检测" => "auto",
            _ => trimmed
        };
    }

    /// <summary>是否为自动检测语言（空值也算 auto）。</summary>
    public static bool IsAutoLanguage(string? language) =>
        string.Equals(NormalizeLanguage(language), "auto", StringComparison.OrdinalIgnoreCase);

    /// <summary>解析生效语言：调用级优先，其次 profile，最后 auto。</summary>
    public static string ResolveLanguage(string? callLanguage, string? profileLanguage) =>
        NormalizeLanguage(string.IsNullOrWhiteSpace(callLanguage) ? profileLanguage : callLanguage);

    /// <summary>仅当语言不是 auto 时返回语言代码，否则返回 null（用于省略 language 字段）。</summary>
    public static string? LanguageOrNull(string? language) =>
        IsAutoLanguage(language) ? null : NormalizeLanguage(language);
}
