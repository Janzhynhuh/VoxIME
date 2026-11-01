using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VoxIME.Core.Abstractions;
using VoxIME.Core.Configuration;
using VoxIME.Core.Logging;

namespace VoxIME.Core.Asr.Providers;

/// <summary>
/// 讯飞 IAT 的 WebSocket 抽象（便于离线单测；生产实现使用 <see cref="ClientWebSocket"/>）。
/// </summary>
public interface IXfyunIatSocket : IAsyncDisposable
{
    /// <summary>发送一条文本帧（JSON）。</summary>
    Task SendTextAsync(string json, CancellationToken cancellationToken);

    /// <summary>接收一条完整文本消息；连接正常关闭时返回 null。</summary>
    Task<string?> ReceiveTextAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 科大讯飞语音听写（流式版 IAT）：wss://iat-api.xfyun.cn/v2/iat，Query 携带 HMAC-SHA256 鉴权，
/// 首帧带 common/business，音频按 1280 字节（40ms）分帧上传，末帧 status=2，按 sn/pgs/rg 拼装结果。
/// </summary>
/// <remarks>
/// 凭据约定：AppId = <see cref="ProviderProfile.AppId"/>，APIKey = <see cref="ProviderProfile.ApiKey"/>，
/// APISecret = <see cref="ProviderProfile.ApiSecret"/>。
/// ExtraJson 中除 business 外的键会作为 business 参数覆盖（如 eos / ptt / dwa），也支持
/// {"business": {...}} 的嵌套写法；兼容旧字段 vad_eos（同时写 eos 与 vad_eos）。
/// </remarks>
public sealed class XfyunAsrProvider : IAsrProvider
{
    private readonly ProviderProfile _profile;
    private readonly IAppLogger _logger;
    private readonly string _appId;
    private readonly string _apiKey;
    private readonly string _apiSecret;
    private readonly Uri _baseUri;

    public XfyunAsrProvider(ProviderProfile profile, HttpClient httpClient, IAppLogger logger)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        ArgumentNullException.ThrowIfNull(httpClient);
        _logger = logger ?? NullLogger.Instance;

        if (string.IsNullOrWhiteSpace(profile.AppId))
        {
            throw new AsrException(
                "科大讯飞识别器缺少 AppId（控制台的 APPID），请填写 AppId 字段。",
                AsrProviderTypeIds.Xfyun);
        }

        if (string.IsNullOrWhiteSpace(profile.ApiKey))
        {
            throw new AsrException(
                "科大讯飞识别器缺少 APIKey，请填写 API Key 字段。",
                AsrProviderTypeIds.Xfyun);
        }

        if (string.IsNullOrWhiteSpace(profile.ApiSecret))
        {
            throw new AsrException(
                "科大讯飞识别器缺少 APISecret，请填写 API Secret 字段（不是 API Key）。",
                AsrProviderTypeIds.Xfyun);
        }

        var baseUrl = string.IsNullOrWhiteSpace(profile.BaseUrl)
            ? XfyunIatProtocol.DefaultWebSocketUrl
            : profile.BaseUrl.Trim();
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != "wss" && baseUri.Scheme != "ws"))
        {
            throw new AsrException(
                $"科大讯飞识别器的 BaseUrl 不合法：“{profile.BaseUrl}”，应形如 {XfyunIatProtocol.DefaultWebSocketUrl}。",
                AsrProviderTypeIds.Xfyun);
        }

        _baseUri = baseUri;
        _appId = profile.AppId.Trim();
        _apiKey = profile.ApiKey.Trim();
        _apiSecret = profile.ApiSecret.Trim();
    }

    /// <inheritdoc />
    public AsrProviderDescriptor Descriptor { get; } =
        AsrProviderCatalog.GetDescriptor(AsrProviderTypeIds.Xfyun)
        ?? throw new InvalidOperationException("内置 ASR 目录缺少 xfyun-iat 描述。");

    /// <summary>可注入时钟（默认 UTC 当前时间），用于单测鉴权签名。</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>帧间发送间隔（默认 40ms，1 帧 = 1280 字节 = 40ms 音频）。测试可设为 0。</summary>
    public TimeSpan FrameInterval { get; set; } = TimeSpan.FromMilliseconds(XfyunIatProtocol.FrameIntervalMilliseconds);

    /// <summary>WebSocket 工厂（默认连接真实讯飞服务；测试注入假实现）。</summary>
    public Func<Uri, CancellationToken, Task<IXfyunIatSocket>>? SocketFactory { get; set; }

    /// <inheritdoc />
    public async Task<AsrResult> TranscribeAsync(
        AudioClip clip,
        AsrCallOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clip);
        cancellationToken.ThrowIfCancellationRequested();

        if (clip.Channels != 1)
        {
            throw new AsrException(
                $"讯飞语音听写仅支持单声道 PCM，当前为 {clip.Channels} 声道。",
                AsrProviderTypeIds.Xfyun);
        }

        if (clip.SampleRate != 16000 && clip.SampleRate != 8000)
        {
            throw new AsrException(
                $"讯飞语音听写仅支持 16k / 8k 采样率，当前为 {clip.SampleRate} Hz。",
                AsrProviderTypeIds.Xfyun);
        }

        options ??= new AsrCallOptions();
        var language = AsrHttpSupport.ResolveLanguage(options.Language, _profile.Language);
        var extra = AsrHttpSupport.MergeExtraJson(_profile.ExtraJson, options.ExtraJson, Descriptor.TypeId);
        var business = BuildBusiness(language, extra);
        var common = new JsonObject { ["app_id"] = _appId };
        var frames = XfyunIatProtocol.BuildFrames(clip.Pcm16, clip.SampleRate, common, business);

        var detectedLanguage = AsrHttpSupport.GetString(business["language"]) ?? "zh_cn";
        _logger.Info(
            $"ASR[{Descriptor.DisplayName}] 连接讯飞 {_baseUri.Host}{_baseUri.AbsolutePath}，" +
            $"语言 {detectedLanguage}，音频 {clip.Duration.TotalSeconds:F1} 秒，共 {frames.Count} 帧。");

        var authUrl = XfyunIatProtocol.BuildAuthUrl(_baseUri, _apiKey, _apiSecret, Clock());
        var stopwatch = Stopwatch.StartNew();
        using var timeoutCts = AsrHttpSupport.CreateTimeoutSource(_profile.TimeoutSeconds, cancellationToken);
        var token = timeoutCts.Token;

        IXfyunIatSocket? socket = null;
        try
        {
            socket = await ConnectAsync(authUrl, token).ConfigureAwait(false);

            string? sessionId = null;
            var assembler = new XfyunTranscriptAssembler();
            for (var index = 0; index < frames.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                await socket.SendTextAsync(frames[index], token).ConfigureAwait(false);
                if (index < frames.Count - 1 && FrameInterval > TimeSpan.Zero)
                {
                    await Task.Delay(FrameInterval, token).ConfigureAwait(false);
                }
            }

            while (true)
            {
                var message = await socket.ReceiveTextAsync(token).ConfigureAwait(false);
                if (message is null)
                {
                    break;
                }

                var root = AsrHttpSupport.TryParseJsonObject(message)
                           ?? throw AsrHttpSupport.CreateInvalidResponseException(
                               Descriptor.TypeId,
                               "无法解析讯飞返回的 WebSocket 消息。",
                               message);
                sessionId ??= AsrHttpSupport.GetString(root["sid"]);

                var code = AsrHttpSupport.GetInt(root["code"]) ?? 0;
                if (code != 0)
                {
                    var errorMessage = AsrHttpSupport.GetString(root["message"]) ?? "未知错误";
                    _logger.Warn($"ASR[{Descriptor.DisplayName}] 讯飞返回错误码 {code}：{errorMessage}");
                    throw new AsrException(
                        $"讯飞识别失败：{code} - {errorMessage}（sid={sessionId ?? "无"}）",
                        Descriptor.TypeId,
                        responseBody: AsrHttpSupport.Truncate(message));
                }

                if (root["data"] is not JsonObject data)
                {
                    continue;
                }

                if (data["result"] is JsonObject result)
                {
                    assembler.Append(result);
                    if (AsrHttpSupport.GetBool(result["ls"]) == true)
                    {
                        break;
                    }
                }

                if (AsrHttpSupport.GetInt(data["status"]) == 2)
                {
                    break;
                }
            }

            var text = assembler.GetText().Trim();
            stopwatch.Stop();
            if (text.Length == 0)
            {
                throw new AsrException(
                    "讯飞识别结束但没有返回识别文本（可能是音频过短、静音或未授权）。",
                    Descriptor.TypeId);
            }

            _logger.Debug(
                $"ASR[{Descriptor.DisplayName}] 讯飞识别完成，耗时 {stopwatch.ElapsedMilliseconds} ms，文本长度 {text.Length}。");

            return new AsrResult(
                text,
                Descriptor.TypeId,
                stopwatch.Elapsed,
                clip.Duration,
                detectedLanguage,
                sessionId);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.Warn($"ASR[{Descriptor.DisplayName}] 讯飞识别超时（{_profile.TimeoutSeconds} 秒）。");
            throw new AsrException(
                $"讯飞识别超时（{_profile.TimeoutSeconds} 秒内未完成）。讯飞整个会话最多 60 秒，请缩短录音或调大超时时间。",
                Descriptor.TypeId,
                innerException: ex);
        }
        catch (WebSocketException ex)
        {
            _logger.Warn($"ASR[{Descriptor.DisplayName}] 讯飞 WebSocket 通信失败：{ex.Message}");
            throw new AsrException(
                $"讯飞 WebSocket 通信失败：{ex.Message}。请检查网络、APPID 权限、IP 白名单与本机时钟偏差（需在 5 分钟内）。",
                Descriptor.TypeId,
                innerException: ex);
        }
        finally
        {
            if (socket is not null)
            {
                await DisposeQuietlyAsync(socket).ConfigureAwait(false);
            }
        }
    }

    /// <summary>构造 business 参数（默认中文普通话 iat，ExtraJson 可覆盖）。</summary>
    public JsonObject BuildBusiness(string language, JsonObject extra)
    {
        ArgumentNullException.ThrowIfNull(extra);
        var business = new JsonObject
        {
            ["language"] = XfyunIatProtocol.MapLanguage(language),
            ["domain"] = "iat",
            ["accent"] = "mandarin"
        };

        if (extra["business"] is JsonObject nestedBusiness)
        {
            AsrHttpSupport.DeepMerge(business, nestedBusiness);
            extra.Remove("business");
        }

        AsrHttpSupport.DeepMerge(business, extra);

        // 当前讯飞文档字段为 eos，旧版文档/大量示例使用 vad_eos；统一成同一值并两个都发送，兼容不同引擎版本。
        var eos = business["eos"] ?? business["vad_eos"] ?? JsonValue.Create(10000);
        business["eos"] = eos?.DeepClone();
        business["vad_eos"] = eos?.DeepClone();
        return business;
    }

    private async Task<IXfyunIatSocket> ConnectAsync(string authUrl, CancellationToken cancellationToken)
    {
        var uri = new Uri(authUrl);
        if (SocketFactory is not null)
        {
            return await SocketFactory(uri, cancellationToken).ConfigureAwait(false);
        }

        var client = new ClientWebSocketIatSocket(uri);
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task DisposeQuietlyAsync(IXfyunIatSocket socket)
    {
        try
        {
            await socket.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 关闭连接失败不影响识别结果/异常。
        }
    }

    /// <summary>生产实现：基于 <see cref="ClientWebSocket"/> 的文本帧收发。</summary>
    private sealed class ClientWebSocketIatSocket : IXfyunIatSocket
    {
        private const int ReceiveBufferSize = 64 * 1024;

        private readonly ClientWebSocket _socket = new();
        private readonly Uri _uri;

        public ClientWebSocketIatSocket(Uri uri)
        {
            _uri = uri;
            _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
            // 握手本身也是一次 HTTP 请求，统一下带 User-Agent。
            _socket.Options.SetRequestHeader("User-Agent", AsrHttpSupport.UserAgent);
        }

        public Task ConnectAsync(CancellationToken cancellationToken) =>
            _socket.ConnectAsync(_uri, cancellationToken);

        public async Task SendTextAsync(string json, CancellationToken cancellationToken)
        {
            var payload = Encoding.UTF8.GetBytes(json);
            await _socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }

        public async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[ReceiveBufferSize];
            using var stream = new MemoryStream();
            while (true)
            {
                var result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                stream.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    break;
                }
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // 忽略关闭失败。
            }
            finally
            {
                _socket.Dispose();
            }
        }
    }
}
