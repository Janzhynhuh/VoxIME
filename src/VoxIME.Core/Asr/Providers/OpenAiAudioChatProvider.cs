using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VoxIME.Core.Abstractions;
using VoxIME.Core.Configuration;
using VoxIME.Core.Logging;

namespace VoxIME.Core.Asr.Providers;

/// <summary>
/// OpenAI 兼容的 <c>POST {BaseUrl}/chat/completions</c> 音频识别器：把 WAV 以 base64
/// 放进 <c>messages[0].content[0].input_audio</c>，从 <c>choices[0].message.content</c> 取识别文本。
/// 主要用于小米 MiMo ASR（mimo-v2.5-asr）与阿里云 DashScope（qwen3-asr-flash）。
/// </summary>
/// <remarks>
/// ExtraJson 支持的控制字段（不会写入请求体）：
/// <list type="bullet">
/// <item><c>authHeader</c>：bearer（默认，Authorization: Bearer）| api-key（api-key 头）| x-api-key。</item>
/// <item><c>audioDataFormat</c>：base64（默认，纯 base64 + format 字段）| data-url（data:audio/wav;base64,...）。</item>
/// </list>
/// 其余字段会深度合并进请求体（例如 {"temperature":0} / {"asr_options":{"enable_itn":true}}）。
/// </remarks>
public sealed class OpenAiAudioChatProvider : IAsrProvider
{
    /// <summary>接口后缀。</summary>
    public const string EndpointSuffix = "/chat/completions";

    private const string ControlAuthHeader = "authHeader";
    private const string ControlAudioDataFormat = "audioDataFormat";

    private readonly ProviderProfile _profile;
    private readonly HttpClient _httpClient;
    private readonly IAppLogger _logger;
    private readonly string _endpoint;

    public OpenAiAudioChatProvider(ProviderProfile profile, HttpClient httpClient, IAppLogger logger)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? NullLogger.Instance;

        if (string.IsNullOrWhiteSpace(profile.BaseUrl))
        {
            throw new AsrException(
                "音频对话识别器缺少 BaseUrl（示例：https://api.xiaomimimo.com/v1）。",
                AsrProviderTypeIds.OpenAiAudioChat);
        }

        if (string.IsNullOrWhiteSpace(profile.Model))
        {
            throw new AsrException(
                "音频对话识别器缺少模型（Model，示例：mimo-v2.5-asr、qwen3-asr-flash）。",
                AsrProviderTypeIds.OpenAiAudioChat);
        }

        _endpoint = AsrHttpSupport.BuildEndpoint(profile.BaseUrl, EndpointSuffix);
    }

    /// <inheritdoc />
    public AsrProviderDescriptor Descriptor { get; } =
        AsrProviderCatalog.GetDescriptor(AsrProviderTypeIds.OpenAiAudioChat)
        ?? throw new InvalidOperationException("内置 ASR 目录缺少 openai-audio-chat 描述。");

    /// <inheritdoc />
    public async Task<AsrResult> TranscribeAsync(
        AudioClip clip,
        AsrCallOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clip);
        cancellationToken.ThrowIfCancellationRequested();

        options ??= new AsrCallOptions();
        var language = AsrHttpSupport.LanguageOrNull(
            AsrHttpSupport.ResolveLanguage(options.Language, _profile.Language));
        var wav = clip.ToWav();

        var extra = AsrHttpSupport.MergeExtraJson(_profile.ExtraJson, options.ExtraJson, Descriptor.TypeId);
        var authHeader = ReadControlString(extra, ControlAuthHeader, "bearer");
        var audioDataFormat = ReadControlString(extra, ControlAudioDataFormat, "base64");

        var requestBody = BuildRequestBody(wav, _profile.Model, language, audioDataFormat, extra);
        var json = requestBody.ToJsonString(AsrHttpSupport.JsonWriteOptions);

        _logger.Info(
            $"ASR[{Descriptor.DisplayName}] 请求音频对话接口 {_endpoint}，模型 {_profile.Model}，" +
            $"语言 {language ?? "auto"}，音频 {clip.Duration.TotalSeconds:F1} 秒 / {wav.Length} 字节，鉴权 {authHeader}。");

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        ApplyAuth(request, authHeader);
        AsrHttpSupport.ApplyUserAgent(request);

        var stopwatch = Stopwatch.StartNew();
        var body = await AsrHttpSupport.PostForBodyAsync(
            _httpClient,
            request,
            _profile.TimeoutSeconds,
            cancellationToken,
            Descriptor.TypeId,
            "音频对话识别",
            _logger).ConfigureAwait(false);
        stopwatch.Stop();

        var parsed = ParseChatCompletion(body);
        _logger.Debug(
            $"ASR[{Descriptor.DisplayName}] 识别完成，耗时 {stopwatch.ElapsedMilliseconds} ms，文本长度 {parsed.Text.Length}。");

        return new AsrResult(
            parsed.Text,
            Descriptor.TypeId,
            stopwatch.Elapsed,
            clip.Duration,
            parsed.DetectedLanguage,
            parsed.RequestId);
    }

    private static JsonObject BuildRequestBody(
        byte[] wav,
        string model,
        string? language,
        string audioDataFormat,
        JsonObject extra)
    {
        var base64 = Convert.ToBase64String(wav);
        var audioObject = new JsonObject();
        var useDataUrl = string.Equals(audioDataFormat, "data-url", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(audioDataFormat, "dataurl", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(audioDataFormat, "data_url", StringComparison.OrdinalIgnoreCase);
        if (useDataUrl)
        {
            // DashScope qwen3-asr-flash 只接受 data URI 形式；MiMo 的 data URL 形式可省略 format。
            audioObject["data"] = "data:audio/wav;base64," + base64;
        }
        else
        {
            // 纯 base64 时 MiMo / OpenAI 规范要求同时给出 format。
            audioObject["data"] = base64;
            audioObject["format"] = "wav";
        }

        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "input_audio",
                            ["input_audio"] = audioObject
                        }
                    }
                }
            }
        };

        if (!string.IsNullOrEmpty(language))
        {
            body["asr_options"] = new JsonObject { ["language"] = language };
        }

        // 深度合并调用方附加参数（可覆盖 model / messages / asr_options 等）。
        AsrHttpSupport.DeepMerge(body, extra);
        return body;
    }

    private void ApplyAuth(HttpRequestMessage request, string authHeader)
    {
        var apiKey = _profile.ApiKey;
        switch (authHeader.Trim().ToLowerInvariant())
        {
            case "api-key":
            case "apikey":
                AsrHttpSupport.ApplyApiKeyHeader(request, "api-key", apiKey);
                break;
            case "x-api-key":
                AsrHttpSupport.ApplyApiKeyHeader(request, "x-api-key", apiKey);
                break;
            case "bearer":
            case "":
                AsrHttpSupport.ApplyBearerAuth(request, apiKey);
                break;
            default:
                _logger.Warn($"未知的 authHeader 值“{authHeader}”，已回退为 Bearer 鉴权。");
                AsrHttpSupport.ApplyBearerAuth(request, apiKey);
                break;
        }
    }

    private static string ReadControlString(JsonObject extra, string key, string fallback)
    {
        var value = AsrHttpSupport.GetString(extra[key]);
        extra.Remove(key);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private (string Text, string? DetectedLanguage, string? RequestId) ParseChatCompletion(string body)
    {
        var trimmed = body?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            throw AsrHttpSupport.CreateInvalidResponseException(
                Descriptor.TypeId,
                "音频对话接口返回了空响应。",
                body);
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(trimmed);
        }
        catch (JsonException ex)
        {
            throw new AsrException(
                $"无法解析音频对话接口响应：{ex.Message}",
                Descriptor.TypeId,
                responseBody: AsrHttpSupport.Truncate(body),
                innerException: ex);
        }

        if (node is not JsonObject obj)
        {
            throw AsrHttpSupport.CreateInvalidResponseException(
                Descriptor.TypeId,
                "音频对话接口响应不是 JSON 对象。",
                body);
        }

        // 部分兼容服务在 HTTP 200 里返回 {"error": {...}}。
        if (obj["error"] is JsonObject error)
        {
            var message = AsrHttpSupport.GetString(error["message"]) ?? error.ToJsonString();
            throw new AsrException(
                $"音频对话接口返回错误：{message}",
                Descriptor.TypeId,
                responseBody: AsrHttpSupport.Truncate(body));
        }

        var choices = obj["choices"] as JsonArray;
        if (choices is null || choices.Count == 0)
        {
            throw AsrHttpSupport.CreateInvalidResponseException(
                Descriptor.TypeId,
                "音频对话接口响应缺少 choices。",
                body);
        }

        var messageNode = choices[0]?["message"];
        var contentNode = messageNode?["content"];
        var text = ExtractContentText(contentNode);
        if (text is null)
        {
            throw AsrHttpSupport.CreateInvalidResponseException(
                Descriptor.TypeId,
                "音频对话接口响应缺少 choices[0].message.content。",
                body);
        }

        var requestId = AsrHttpSupport.GetString(obj["id"]);
        var detectedLanguage = null as string;
        if (obj["asr_options"] is JsonObject asrOptions)
        {
            detectedLanguage = AsrHttpSupport.GetString(asrOptions["language"]);
        }

        if (string.IsNullOrEmpty(detectedLanguage) && obj["language"] is not null)
        {
            detectedLanguage = AsrHttpSupport.GetString(obj["language"]);
        }

        return (text.Trim(), detectedLanguage, requestId);
    }

    private static string? ExtractContentText(JsonNode? contentNode)
    {
        switch (contentNode)
        {
            case JsonValue value:
                return AsrHttpSupport.GetString(value);
            case JsonArray parts:
            {
                var builder = new StringBuilder();
                foreach (var part in parts)
                {
                    if (part is not JsonObject partObject)
                    {
                        continue;
                    }

                    var text = AsrHttpSupport.GetString(partObject["text"]);
                    if (text is null && partObject["content"] is not null)
                    {
                        text = AsrHttpSupport.GetString(partObject["content"]);
                    }

                    if (!string.IsNullOrEmpty(text))
                    {
                        builder.Append(text);
                    }
                }

                return builder.Length == 0 ? null : builder.ToString();
            }
            default:
                return null;
        }
    }
}
