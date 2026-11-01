using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using VoxIME.Core.Abstractions;
using VoxIME.Core.Configuration;
using VoxIME.Core.Logging;

namespace VoxIME.Core.Asr.Providers;

/// <summary>
/// 腾讯云一句话识别（SentenceRecognition，≤60 秒短音频）：TC3-HMAC-SHA256 签名、
/// 音频 base64 放请求体 POST 到 asr.tencentcloudapi.com。
/// </summary>
/// <remarks>
/// 凭据约定：<see cref="ProviderProfile.SecretId"/> = SecretId，<see cref="ProviderProfile.ApiKey"/> = SecretKey。
/// ExtraJson 支持控制字段 <c>region</c>（默认 ap-guangzhou）；其余字段（EngSerViceType / FilterDirty /
/// FilterModal / ConvertNumMode / WordInfo / HotwordList 等）会直接合并进请求体覆盖默认值。
/// </remarks>
public sealed class TencentAsrProvider : IAsrProvider
{
    public const string DefaultBaseUrl = "https://asr.tencentcloudapi.com";
    public const string DefaultRegion = "ap-guangzhou";
    public const string ApiAction = "SentenceRecognition";
    public const string ApiVersion = "2019-06-14";
    public const string JsonContentType = "application/json; charset=utf-8";

    private readonly ProviderProfile _profile;
    private readonly HttpClient _httpClient;
    private readonly IAppLogger _logger;
    private readonly string _secretId;
    private readonly string _secretKey;
    private readonly Uri _endpoint;
    private readonly string _host;

    public TencentAsrProvider(ProviderProfile profile, HttpClient httpClient, IAppLogger logger)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? NullLogger.Instance;

        if (string.IsNullOrWhiteSpace(profile.SecretId))
        {
            throw new AsrException(
                "腾讯云识别器缺少 SecretId，请在服务配置的 SecretId 字段填写。",
                AsrProviderTypeIds.Tencent);
        }

        if (string.IsNullOrWhiteSpace(profile.ApiKey))
        {
            throw new AsrException(
                "腾讯云识别器缺少 SecretKey，请把 SecretKey 填到 API Key 字段。",
                AsrProviderTypeIds.Tencent);
        }

        var baseUrl = string.IsNullOrWhiteSpace(profile.BaseUrl) ? DefaultBaseUrl : profile.BaseUrl.Trim();
        if (!Uri.TryCreate(baseUrl.TrimEnd('/'), UriKind.Absolute, out var endpoint))
        {
            throw new AsrException(
                $"腾讯云识别器的 BaseUrl 不合法：“{profile.BaseUrl}”，应形如 {DefaultBaseUrl}。",
                AsrProviderTypeIds.Tencent);
        }

        _endpoint = endpoint;
        _host = endpoint.IsDefaultPort ? endpoint.Host : $"{endpoint.Host}:{endpoint.Port}";
        _secretId = profile.SecretId.Trim();
        _secretKey = profile.ApiKey.Trim();
    }

    /// <inheritdoc />
    public AsrProviderDescriptor Descriptor { get; } =
        AsrProviderCatalog.GetDescriptor(AsrProviderTypeIds.Tencent)
        ?? throw new InvalidOperationException("内置 ASR 目录缺少 tencent-sentence 描述。");

    /// <summary>可注入时钟（默认 UTC 当前时间），用于单测签名。</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public async Task<AsrResult> TranscribeAsync(
        AudioClip clip,
        AsrCallOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clip);
        cancellationToken.ThrowIfCancellationRequested();

        options ??= new AsrCallOptions();
        var language = AsrHttpSupport.ResolveLanguage(options.Language, _profile.Language);
        var engine = MapEngineType(language);
        var extra = AsrHttpSupport.MergeExtraJson(_profile.ExtraJson, options.ExtraJson, Descriptor.TypeId);

        // region 是签名/请求头参数，不进入请求体。
        var region = AsrHttpSupport.GetString(extra["region"]);
        extra.Remove("region");
        if (string.IsNullOrWhiteSpace(region))
        {
            region = DefaultRegion;
        }

        var wav = clip.ToWav();
        var requestBody = new JsonObject
        {
            ["EngSerViceType"] = engine,
            ["SourceType"] = 1,
            ["VoiceFormat"] = "wav",
            ["Data"] = Convert.ToBase64String(wav),
            ["DataLen"] = wav.Length,
            ["FilterDirty"] = 0,
            ["FilterModal"] = 0,
            ["ConvertNumMode"] = 1
        };
        AsrHttpSupport.DeepMerge(requestBody, extra);
        var json = requestBody.ToJsonString(AsrHttpSupport.JsonWriteOptions);

        var clock = Clock();
        var timestamp = clock.ToUnixTimeSeconds();
        var date = TencentTc3Signer.FormatDate(clock);
        var authorization = TencentTc3Signer.CreateAuthorization(
            _secretId,
            _secretKey,
            _host,
            JsonContentType,
            json,
            timestamp,
            date);

        _logger.Info(
            $"ASR[{Descriptor.DisplayName}] 请求腾讯云 {_endpoint}，地域 {region}，引擎 {engine}，" +
            $"音频 {clip.Duration.TotalSeconds:F1} 秒 / {wav.Length} 字节。");

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Host = _host;
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Headers.TryAddWithoutValidation("X-TC-Action", ApiAction);
        request.Headers.TryAddWithoutValidation("X-TC-Version", ApiVersion);
        request.Headers.TryAddWithoutValidation("X-TC-Timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-TC-Region", region);
        AsrHttpSupport.ApplyUserAgent(request);

        var stopwatch = Stopwatch.StartNew();
        var body = await AsrHttpSupport.PostForBodyAsync(
            _httpClient,
            request,
            _profile.TimeoutSeconds,
            cancellationToken,
            Descriptor.TypeId,
            "腾讯云一句话识别",
            _logger).ConfigureAwait(false);
        stopwatch.Stop();

        var (text, requestId) = ParseResponse(body);
        _logger.Debug(
            $"ASR[{Descriptor.DisplayName}] 腾讯云识别完成，耗时 {stopwatch.ElapsedMilliseconds} ms，文本长度 {text.Length}。");

        return new AsrResult(
            text,
            Descriptor.TypeId,
            stopwatch.Elapsed,
            clip.Duration,
            engine,
            requestId);
    }

    /// <summary>把语言代码映射成腾讯云 EngSerViceType；未知值回退 16k_zh。</summary>
    public static string MapEngineType(string? language)
    {
        var normalized = AsrHttpSupport.NormalizeLanguage(language);
        if (normalized.StartsWith("16k", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("8k", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        return normalized.ToLowerInvariant() switch
        {
            "auto" or "zh" or "zh-cn" or "zh-hans" or "zh-hant" or "cmn" or "cn" => "16k_zh",
            "en" or "en-us" or "en-gb" => "16k_en",
            "yue" or "zh-hk" or "zh-yue" or "cantonese" => "16k_yue",
            "ja" or "jp" => "16k_ja",
            "ko" or "kr" => "16k_ko",
            "vi" => "16k_vi",
            "ms" => "16k_ms",
            "id" => "16k_id",
            "fil" or "tl" => "16k_fil",
            "th" => "16k_th",
            "pt" => "16k_pt",
            "tr" => "16k_tr",
            "ar" => "16k_ar",
            "es" => "16k_es",
            "hi" => "16k_hi",
            "fr" => "16k_fr",
            "de" => "16k_de",
            _ => "16k_zh"
        };
    }

    private (string Text, string? RequestId) ParseResponse(string body)
    {
        var root = AsrHttpSupport.TryParseJsonObject(body);
        if (root is null)
        {
            throw AsrHttpSupport.CreateInvalidResponseException(
                Descriptor.TypeId,
                "无法解析腾讯云识别响应（期望 JSON 对象）。",
                body);
        }

        if (root["Response"] is not JsonObject response)
        {
            throw AsrHttpSupport.CreateInvalidResponseException(
                Descriptor.TypeId,
                "腾讯云识别响应缺少 Response 节点。",
                body);
        }

        if (response["Error"] is JsonObject error)
        {
            var code = AsrHttpSupport.GetString(error["Code"]) ?? "UnknownError";
            var message = AsrHttpSupport.GetString(error["Message"]) ?? "未知错误";
            throw new AsrException(
                $"腾讯云识别失败：{code} - {message}",
                Descriptor.TypeId,
                responseBody: AsrHttpSupport.Truncate(body));
        }

        var resultNode = response["Result"];
        if (resultNode is null)
        {
            throw AsrHttpSupport.CreateInvalidResponseException(
                Descriptor.TypeId,
                "腾讯云识别响应缺少 Response.Result。",
                body);
        }

        var text = AsrHttpSupport.GetString(resultNode) ?? "";
        var requestId = AsrHttpSupport.GetString(response["RequestId"]);
        return (text.Trim(), requestId);
    }
}
