using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VoxIME.Core.Abstractions;
using VoxIME.Core.Configuration;
using VoxIME.Core.Logging;

namespace VoxIME.Core.Asr.Providers;

/// <summary>
/// OpenAI 兼容的 <c>POST {BaseUrl}/audio/transcriptions</c> 多段表单识别器。
/// 适用于 OpenAI Whisper、硅基流动 SenseVoice、LM Studio / whisper.cpp server 等兼容实现。
/// </summary>
/// <remarks>
/// 请求字段：file=audio.wav（audio/wav）、model、language（auto/空时省略）、prompt、response_format=json；
/// 再叠加 profile/调用级 ExtraJson 中的字段（可覆盖任意字段，例如 {"response_format":"verbose_json"}）。
/// 响应解析 <c>text</c> 字段（verbose_json 同样含 text）。
/// </remarks>
public sealed class OpenAiCompatibleTranscriptionProvider : IAsrProvider
{
    /// <summary>接口后缀。</summary>
    public const string EndpointSuffix = "/audio/transcriptions";

    private readonly ProviderProfile _profile;
    private readonly HttpClient _httpClient;
    private readonly IAppLogger _logger;
    private readonly string _endpoint;

    public OpenAiCompatibleTranscriptionProvider(ProviderProfile profile, HttpClient httpClient, IAppLogger logger)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? NullLogger.Instance;

        if (string.IsNullOrWhiteSpace(profile.BaseUrl))
        {
            throw new AsrException(
                "OpenAI 兼容转录接口缺少 BaseUrl（示例：https://api.openai.com/v1）。",
                AsrProviderTypeIds.OpenAiTranscription);
        }

        if (string.IsNullOrWhiteSpace(profile.Model))
        {
            throw new AsrException(
                "OpenAI 兼容转录接口缺少模型（Model，示例：whisper-1、FunAudioLLM/SenseVoiceSmall）。",
                AsrProviderTypeIds.OpenAiTranscription);
        }

        _endpoint = AsrHttpSupport.BuildEndpoint(profile.BaseUrl, EndpointSuffix);
    }

    /// <inheritdoc />
    public AsrProviderDescriptor Descriptor { get; } =
        AsrProviderCatalog.GetDescriptor(AsrProviderTypeIds.OpenAiTranscription)
        ?? throw new InvalidOperationException("内置 ASR 目录缺少 openai-transcription 描述。");

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
        var prompt = string.IsNullOrWhiteSpace(options.Prompt) ? _profile.Prompt : options.Prompt;
        var wav = clip.ToWav();

        _logger.Info(
            $"ASR[{Descriptor.DisplayName}] 请求转录接口 {_endpoint}，模型 {_profile.Model}，" +
            $"语言 {language ?? "auto"}，音频 {clip.Duration.TotalSeconds:F1} 秒 / {wav.Length} 字节。");

        // ExtraJson 最后应用，可覆盖 response_format / temperature / language 等任意表单字段。
        var extra = AsrHttpSupport.MergeExtraJson(_profile.ExtraJson, options.ExtraJson, Descriptor.TypeId);
        var fields = new List<KeyValuePair<string, string>>();
        SetField(fields, "model", _profile.Model);
        if (!string.IsNullOrEmpty(language))
        {
            SetField(fields, "language", language);
        }

        if (!string.IsNullOrWhiteSpace(prompt))
        {
            SetField(fields, "prompt", prompt);
        }

        SetField(fields, "response_format", "json");

        foreach (var pair in extra)
        {
            var value = AsrHttpSupport.ExtraValueAsString(pair.Value);
            if (value is null)
            {
                continue;
            }

            if (string.Equals(pair.Key, "file", StringComparison.OrdinalIgnoreCase))
            {
                // file 由音频内容固定提供，忽略 ExtraJson 中的同名覆盖。
                _logger.Warn("ExtraJson 中的 file 字段被忽略：音频文件固定为 audio.wav。");
                continue;
            }

            SetField(fields, pair.Key, value);
        }

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(wav);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(fileContent, "file", "audio.wav");
        foreach (var field in fields)
        {
            form.Add(new StringContent(field.Value, Encoding.UTF8), field.Key);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = form };
        AsrHttpSupport.ApplyBearerAuth(request, _profile.ApiKey);
        AsrHttpSupport.ApplyUserAgent(request);

        var stopwatch = Stopwatch.StartNew();
        var body = await AsrHttpSupport.PostForBodyAsync(
            _httpClient,
            request,
            _profile.TimeoutSeconds,
            cancellationToken,
            Descriptor.TypeId,
            "转录接口",
            _logger).ConfigureAwait(false);
        stopwatch.Stop();

        var parsed = ParseTranscription(body);
        _logger.Debug(
            $"ASR[{Descriptor.DisplayName}] 转录完成，耗时 {stopwatch.ElapsedMilliseconds} ms，文本长度 {parsed.Text.Length}。");

        return new AsrResult(
            parsed.Text,
            Descriptor.TypeId,
            stopwatch.Elapsed,
            clip.Duration,
            parsed.Language);
    }

    private (string Text, string? Language) ParseTranscription(string body)
    {
        var trimmed = body?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            throw AsrHttpSupport.CreateInvalidResponseException(
                Descriptor.TypeId,
                "转录接口返回了空响应。",
                body);
        }

        JsonNode? node = null;
        var parseFailed = false;
        try
        {
            node = JsonNode.Parse(trimmed);
        }
        catch (JsonException)
        {
            parseFailed = true;
        }

        if (node is JsonObject obj)
        {
            var textNode = obj["text"];
            if (textNode is null)
            {
                throw AsrHttpSupport.CreateInvalidResponseException(
                    Descriptor.TypeId,
                    "转录接口响应缺少 text 字段。",
                    body);
            }

            return (AsrHttpSupport.GetString(textNode) ?? "", AsrHttpSupport.GetString(obj["language"]));
        }

        // 少数兼容服务在 response_format=text 时直接返回纯文本；JSON 解析失败且内容不像 JSON 时兜底。
        if (parseFailed && !(trimmed.StartsWith('{') || trimmed.StartsWith('[')))
        {
            return (trimmed, null);
        }

        throw AsrHttpSupport.CreateInvalidResponseException(
            Descriptor.TypeId,
            "无法解析转录接口响应（期望包含 text 字段的 JSON 对象）。",
            body);
    }

    private static void SetField(List<KeyValuePair<string, string>> fields, string name, string value)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (string.Equals(fields[i].Key, name, StringComparison.OrdinalIgnoreCase))
            {
                fields[i] = new KeyValuePair<string, string>(name, value);
                return;
            }
        }

        fields.Add(new KeyValuePair<string, string>(name, value));
    }
}
