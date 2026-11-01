using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VoxIME.Core.Abstractions;
using VoxIME.Core.Configuration;
using VoxIME.Core.Logging;

namespace VoxIME.Core.Tests.Asr;

/// <summary>捕获到的请求快照（请求被 Provider 释放后仍可断言）。</summary>
internal sealed class CapturedRequest
{
    public required Uri RequestUri { get; init; }
    public HttpMethod Method { get; init; } = HttpMethod.Post;
    public string? ContentType { get; init; }
    public byte[] BodyBytes { get; init; } = Array.Empty<byte>();
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>请求体 UTF-8 文本（multipart 也适用，用于字段名断言）。</summary>
    public string BodyText => Encoding.UTF8.GetString(BodyBytes);

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

    public JsonObject? JsonBody
    {
        get
        {
            if (BodyBytes.Length == 0 || ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
            {
                return null;
            }

            try
            {
                return JsonNode.Parse(BodyBytes) as JsonObject;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    public Dictionary<string, string> MultipartFields { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? MultipartFileName { get; private set; }
    public byte[]? MultipartFileBytes { get; private set; }
    public string? MultipartFileContentType { get; private set; }

    public static async Task<CapturedRequest> CreateAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var bodyBytes = request.Content is null
            ? Array.Empty<byte>()
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        var captured = new CapturedRequest
        {
            RequestUri = request.RequestUri ?? new Uri("http://invalid.local/"),
            Method = request.Method,
            ContentType = request.Content?.Headers.ContentType?.ToString(),
            BodyBytes = bodyBytes
        };

        foreach (var header in request.Headers)
        {
            captured.Headers[header.Key] = string.Join(",", header.Value);
        }

        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                captured.Headers[header.Key] = string.Join(",", header.Value);
            }
        }

        if (captured.ContentType?.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase) == true)
        {
            ParseMultipart(captured);
        }

        return captured;
    }

    private static void ParseMultipart(CapturedRequest captured)
    {
        var boundaryMatch = System.Text.RegularExpressions.Regex.Match(
            captured.ContentType ?? "",
            "boundary=\"?([^\";]+)\"?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!boundaryMatch.Success)
        {
            return;
        }

        var delimiter = Encoding.UTF8.GetBytes("--" + boundaryMatch.Groups[1].Value);
        var body = captured.BodyBytes;
        var offset = 0;
        while (offset < body.Length)
        {
            var start = IndexOf(body, delimiter, offset);
            if (start < 0)
            {
                break;
            }

            start += delimiter.Length;
            if (start + 1 < body.Length && body[start] == '-' && body[start + 1] == '-')
            {
                break;
            }

            if (start + 1 < body.Length && body[start] == '\r' && body[start + 1] == '\n')
            {
                start += 2;
            }

            var next = IndexOf(body, delimiter, start);
            var partEnd = next < 0 ? body.Length : next;
            var part = body[start..partEnd];
            if (part.Length >= 2 && part[^2] == '\r' && part[^1] == '\n')
            {
                part = part[..^2];
            }

            var headerEnd = IndexOf(part, "\r\n\r\n"u8.ToArray(), 0);
            if (headerEnd < 0)
            {
                offset = partEnd;
                continue;
            }

            var headers = Encoding.UTF8.GetString(part, 0, headerEnd);
            var valueBytes = part[(headerEnd + 4)..];
            var nameMatch = System.Text.RegularExpressions.Regex.Match(
                headers,
                "name=\"?([^\";\r\n]+)\"?",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (nameMatch.Success)
            {
                var name = nameMatch.Groups[1].Value.Trim();
                var filenameMatch = System.Text.RegularExpressions.Regex.Match(
                    headers,
                    "filename=\"?([^\";\r\n]+)\"?",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (filenameMatch.Success)
                {
                    captured.MultipartFileName = filenameMatch.Groups[1].Value.Trim();
                    captured.MultipartFileBytes = valueBytes;
                    var contentTypeMatch = System.Text.RegularExpressions.Regex.Match(
                        headers,
                        "Content-Type:\\s*([^\\r\\n]+)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    captured.MultipartFileContentType = contentTypeMatch.Success
                        ? contentTypeMatch.Groups[1].Value.Trim()
                        : null;
                }
                else
                {
                    captured.MultipartFields[name] = Encoding.UTF8.GetString(valueBytes);
                }
            }

            offset = partEnd;
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        for (var i = Math.Max(0, start); i <= haystack.Length - needle.Length; i++)
        {
            var matched = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>离线假 HttpMessageHandler：记录每个请求并按委托返回响应，绝不访问网络。</summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<CapturedRequest, CancellationToken, Task<HttpResponseMessage>> _responder;
    private readonly List<CapturedRequest> _requests = new();

    public FakeHttpMessageHandler(Func<CapturedRequest, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        _responder = responder ?? throw new ArgumentNullException(nameof(responder));
    }

    public FakeHttpMessageHandler(Func<CapturedRequest, HttpResponseMessage> responder)
        : this((request, _) => Task.FromResult(responder(request)))
    {
    }

    public IReadOnlyList<CapturedRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return _requests.ToList();
            }
        }
    }

    public CapturedRequest SingleRequest => Requests.Count == 1
        ? Requests[0]
        : throw new InvalidOperationException($"期望恰好 1 个请求，实际 {Requests.Count} 个。");

    public static FakeHttpMessageHandler Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new((_, _) => Task.FromResult(JsonResponse(json, status)));

    public static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var captured = await CapturedRequest.CreateAsync(request, cancellationToken).ConfigureAwait(false);
        lock (_requests)
        {
            _requests.Add(captured);
        }

        return await _responder(captured, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>记录日志内容的内存 IAppLogger（用于断言日志脱敏与错误落日志）。</summary>
internal sealed class FakeAppLogger : IAppLogger
{
    private readonly List<string> _entries = new();
    private readonly List<LogLevel> _levels = new();

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToList();
            }
        }
    }

    public bool HasLevel(LogLevel level)
    {
        lock (_levels)
        {
            return _levels.Contains(level);
        }
    }

    public string AllText
    {
        get
        {
            lock (_entries)
            {
                return string.Join("\n", _entries);
            }
        }
    }

    public string LogFilePath => "";

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        lock (_entries)
        {
            _levels.Add(level);
            _entries.Add(exception is null ? message : $"{message} | {exception.GetType().Name}: {exception.Message}");
        }
    }

    public void Trace(string message) => Log(LogLevel.Trace, message);
    public void Debug(string message) => Log(LogLevel.Debug, message);
    public void Info(string message) => Log(LogLevel.Info, message);
    public void Warn(string message) => Log(LogLevel.Warn, message);
    public void Error(string message, Exception? exception = null) => Log(LogLevel.Error, message, exception);
}

/// <summary>测试数据构造工具。</summary>
internal static class AsrTestData
{
    public static readonly string SampleWavJson = "{\"text\":\"你好，世界\"}";

    /// <summary>创建 N 秒静音音频（16k/单声道/16bit）。</summary>
    public static AudioClip Silence(double seconds) =>
        AudioClip.CreateSilence(TimeSpan.FromSeconds(seconds));

    public static AudioClip Pcm(int bytes) =>
        AudioClip.FromPcm16(new byte[bytes], 16000, 1);

    public static HttpClient HttpClient(FakeHttpMessageHandler handler) =>
        new(handler) { Timeout = Timeout.InfiniteTimeSpan };

    public static ProviderProfile TranscriptionProfile(
        string? baseUrl = "https://api.openai.com/v1",
        string model = "whisper-1",
        string language = "zh",
        string? extraJson = null,
        string? prompt = null,
        string apiKey = "sk-test-secret-123456",
        int timeoutSeconds = 30) => new()
    {
        Id = "test",
        Name = "测试服务",
        TypeId = AsrProviderTypeIds.OpenAiTranscription,
        BaseUrl = baseUrl ?? "",
        Model = model,
        Language = language,
        ExtraJson = extraJson,
        Prompt = prompt,
        ApiKey = apiKey,
        TimeoutSeconds = timeoutSeconds
    };

    public static ProviderProfile AudioChatProfile(
        string? baseUrl = "https://api.xiaomimimo.com/v1",
        string model = "mimo-v2.5-asr",
        string language = "zh",
        string? extraJson = null,
        string apiKey = "sk-test-secret-123456",
        int timeoutSeconds = 30) => new()
    {
        Id = "test-chat",
        Name = "音频对话测试服务",
        TypeId = AsrProviderTypeIds.OpenAiAudioChat,
        BaseUrl = baseUrl ?? "",
        Model = model,
        Language = language,
        ExtraJson = extraJson,
        ApiKey = apiKey,
        TimeoutSeconds = timeoutSeconds
    };

    public static ProviderProfile ProcessProfile(
        string command,
        string arguments,
        string outputMode = "stdout",
        string? outputFileTemplate = null,
        string? extraJson = null,
        int timeoutSeconds = 30,
        string language = "zh") => new()
    {
        Id = "test-cli",
        Name = "本地 CLI 测试服务",
        TypeId = AsrProviderTypeIds.LocalCli,
        Model = "",
        Language = language,
        ExtraJson = extraJson,
        Command = command,
        CommandArguments = arguments,
        OutputMode = outputMode,
        OutputFileTemplate = outputFileTemplate,
        TimeoutSeconds = timeoutSeconds
    };

    public static ProviderProfile TencentProfile(
        string secretId = "AKIDEXAMPLE",
        string secretKey = "SECRETKEYEXAMPLE",
        string language = "zh",
        string? extraJson = "{\"region\":\"ap-guangzhou\"}",
        int timeoutSeconds = 30) => new()
    {
        Id = "test-tencent",
        Name = "腾讯云测试服务",
        TypeId = AsrProviderTypeIds.Tencent,
        BaseUrl = "https://asr.tencentcloudapi.com",
        Model = "",
        Language = language,
        ExtraJson = extraJson,
        ApiKey = secretKey,
        SecretId = secretId,
        TimeoutSeconds = timeoutSeconds
    };

    public static ProviderProfile XfyunProfile(
        string appId = "test-appid",
        string apiKey = "test-apikey",
        string apiSecret = "test-apisecret",
        string language = "zh",
        string? extraJson = null,
        int timeoutSeconds = 30) => new()
    {
        Id = "test-xfyun",
        Name = "讯飞测试服务",
        TypeId = AsrProviderTypeIds.Xfyun,
        BaseUrl = "wss://iat-api.xfyun.cn/v2/iat",
        Model = "",
        Language = language,
        ExtraJson = extraJson,
        AppId = appId,
        ApiKey = apiKey,
        ApiSecret = apiSecret,
        TimeoutSeconds = timeoutSeconds
    };

    public static ProviderProfile MockProfile(string? extraJson = null) => new()
    {
        Id = "mock",
        Name = "Mock 测试服务",
        TypeId = AsrProviderTypeIds.Mock,
        Model = "mock",
        Language = "zh",
        ExtraJson = extraJson,
        TimeoutSeconds = 5
    };
}
