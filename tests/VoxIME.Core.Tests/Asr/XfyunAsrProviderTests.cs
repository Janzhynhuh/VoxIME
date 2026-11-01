using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using VoxIME.Core.Abstractions;
using VoxIME.Core.Asr.Providers;
using VoxIME.Core.Configuration;
using Xunit;

namespace VoxIME.Core.Tests.Asr;

public sealed class XfyunAsrProviderTests
{
    private static readonly DateTimeOffset FixedClock = new(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private sealed class FakeIatSocket : IXfyunIatSocket
    {
        public List<string> Sent { get; } = new();
        public Queue<string> ReceiveQueue { get; } = new();
        public Func<CancellationToken, Task<string?>>? ReceiveOverride { get; set; }
        public bool Disposed { get; private set; }

        public Task SendTextAsync(string json, CancellationToken cancellationToken)
        {
            Sent.Add(json);
            return Task.CompletedTask;
        }

        public Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
        {
            if (ReceiveOverride is not null)
            {
                return ReceiveOverride(cancellationToken);
            }

            return Task.FromResult(ReceiveQueue.Count > 0 ? ReceiveQueue.Dequeue() : null);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private static XfyunAsrProvider Create(
        ProviderProfile profile,
        FakeAppLogger? logger = null)
    {
        var provider = new XfyunAsrProvider(
            profile,
            AsrTestData.HttpClient(new FakeHttpMessageHandler(_ =>
                throw new InvalidOperationException("讯飞 WebSocket 测试不应使用 HttpClient。"))),
            logger ?? new FakeAppLogger())
        {
            Clock = () => FixedClock,
            FrameInterval = TimeSpan.Zero
        };
        return provider;
    }

    private static string ResultJson(int sn, string text, int status, bool last = false) =>
        "{\"code\":0,\"sid\":\"sid-1\",\"data\":{\"status\":" + status +
        ",\"result\":{\"sn\":" + sn + ",\"ls\":" + (last ? "true" : "false") +
        ",\"ws\":[{\"cw\":[{\"w\":\"" + text + "\"}]}]}}}";

    private static Dictionary<string, string> ParseQuery(Uri uri)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var query = uri.Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2)
            {
                result[Uri.UnescapeDataString(parts[0])] = Uri.UnescapeDataString(parts[1]);
            }
        }

        return result;
    }

    [Fact]
    public void 鉴权URL_包含host_date与HMAC签名()
    {
        var baseUri = new Uri("wss://iat-api.xfyun.cn/v2/iat");
        var url = XfyunIatProtocol.BuildAuthUrl(baseUri, "test-key-1", "test-secret-1", FixedClock);

        var uri = new Uri(url);
        var query = ParseQuery(uri);
        Assert.Equal("iat-api.xfyun.cn", query["host"]);
        Assert.Equal("Tue, 02 Jan 2024 03:04:05 GMT", query["date"]);

        var authorization = Encoding.UTF8.GetString(Convert.FromBase64String(query["authorization"]));
        Assert.StartsWith("api_key=\"test-key-1\", algorithm=\"hmac-sha256\", headers=\"host date request-line\", signature=\"",
            authorization);

        var signatureOrigin =
            "host: iat-api.xfyun.cn\ndate: Tue, 02 Jan 2024 03:04:05 GMT\nGET /v2/iat HTTP/1.1";
        var expectedSignature = Convert.ToBase64String(
            new HMACSHA256(Encoding.UTF8.GetBytes("test-secret-1"))
                .ComputeHash(Encoding.UTF8.GetBytes(signatureOrigin)));
        Assert.Contains($"signature=\"{expectedSignature}\"", authorization);
    }

    [Fact]
    public void 帧序列_首帧带common与business_中间帧status1_末帧status2()
    {
        var common = new JsonObject { ["app_id"] = "app-1" };
        var business = new JsonObject
        {
            ["language"] = "zh_cn",
            ["domain"] = "iat",
            ["accent"] = "mandarin",
            ["vad_eos"] = 10000
        };
        var pcm = new byte[2560];

        var frames = XfyunIatProtocol.BuildFrames(pcm, 16000, common, business);

        Assert.Equal(3, frames.Count);
        var first = JsonNode.Parse(frames[0])!.AsObject();
        Assert.Equal("app-1", first["common"]!["app_id"]!.GetValue<string>());
        Assert.Equal("zh_cn", first["business"]!["language"]!.GetValue<string>());
        Assert.Equal("iat", first["business"]!["domain"]!.GetValue<string>());
        Assert.Equal("mandarin", first["business"]!["accent"]!.GetValue<string>());
        Assert.Equal(10000, first["business"]!["vad_eos"]!.GetValue<int>());
        Assert.Equal(0, first["data"]!["status"]!.GetValue<int>());
        Assert.Equal("audio/L16;rate=16000", first["data"]!["format"]!.GetValue<string>());
        Assert.Equal("raw", first["data"]!["encoding"]!.GetValue<string>());
        Assert.Equal(1280, Convert.FromBase64String(first["data"]!["audio"]!.GetValue<string>()).Length);

        var middle = JsonNode.Parse(frames[1])!.AsObject();
        Assert.Null(middle["common"]);
        Assert.Null(middle["business"]);
        Assert.Equal(1, middle["data"]!["status"]!.GetValue<int>());
        Assert.Equal(1280, Convert.FromBase64String(middle["data"]!["audio"]!.GetValue<string>()).Length);

        var last = JsonNode.Parse(frames[2])!.AsObject();
        Assert.Equal(2, last["data"]!["status"]!.GetValue<int>());
        Assert.Null(last["data"]!["audio"]);
    }

    [Fact]
    public void 音频分帧_最后一帧允许不足1280字节()
    {
        var chunks = XfyunIatProtocol.SplitAudioFrames(new byte[3060]);
        Assert.Equal(3, chunks.Count);
        Assert.Equal(1280, chunks[0].Length);
        Assert.Equal(1280, chunks[1].Length);
        Assert.Equal(500, chunks[2].Length);

        var singleChunkFrames = XfyunIatProtocol.BuildFrames(
            new byte[1280], 16000, new JsonObject { ["app_id"] = "a" }, new JsonObject { ["language"] = "zh_cn" });
        Assert.Equal(2, singleChunkFrames.Count);
    }

    [Fact]
    public async Task 完整识别流程_发送帧并拼装文本()
    {
        var provider = Create(AsrTestData.XfyunProfile());
        var socket = new FakeIatSocket();
        socket.ReceiveQueue.Enqueue(ResultJson(1, "你好", 1));
        socket.ReceiveQueue.Enqueue(ResultJson(2, "世界", 2, last: true));
        Uri? capturedUri = null;
        provider.SocketFactory = (uri, _) =>
        {
            capturedUri = uri;
            return Task.FromResult<IXfyunIatSocket>(socket);
        };

        var result = await provider.TranscribeAsync(AsrTestData.Pcm(2560), new AsrCallOptions());

        Assert.Equal("你好世界", result.Text);
        Assert.Equal("sid-1", result.RequestId);
        Assert.Equal("zh_cn", result.DetectedLanguage);
        Assert.Equal(AsrProviderTypeIds.Xfyun, result.ProviderId);
        Assert.Equal(3, socket.Sent.Count);
        Assert.True(socket.Disposed);

        Assert.NotNull(capturedUri);
        var query = ParseQuery(capturedUri!);
        Assert.Equal("iat-api.xfyun.cn", query["host"]);
        Assert.True(query.ContainsKey("authorization"));

        var firstFrame = JsonNode.Parse(socket.Sent[0])!.AsObject();
        Assert.Equal("test-appid", firstFrame["common"]!["app_id"]!.GetValue<string>());
        Assert.Equal(0, firstFrame["data"]!["status"]!.GetValue<int>());
        var lastFrame = JsonNode.Parse(socket.Sent[^1])!.AsObject();
        Assert.Equal(2, lastFrame["data"]!["status"]!.GetValue<int>());
    }

    [Fact]
    public async Task 服务端错误码_抛带错误码的AsrException()
    {
        var provider = Create(AsrTestData.XfyunProfile());
        var socket = new FakeIatSocket();
        socket.ReceiveQueue.Enqueue("{\"code\":10165,\"message\":\"客户端帧可能乱序\",\"sid\":\"s1\"}");
        provider.SocketFactory = (_, _) => Task.FromResult<IXfyunIatSocket>(socket);

        var exception = await Assert.ThrowsAsync<AsrException>(
            () => provider.TranscribeAsync(AsrTestData.Pcm(2560), new AsrCallOptions()));

        Assert.Contains("10165", exception.Message);
        Assert.Contains("客户端帧可能乱序", exception.Message);
        Assert.True(socket.Disposed);
    }

    [Fact]
    public async Task 未返回文本时_抛中文异常()
    {
        var provider = Create(AsrTestData.XfyunProfile());
        var socket = new FakeIatSocket();
        socket.ReceiveQueue.Enqueue("{\"code\":0,\"data\":{\"status\":2}}");
        provider.SocketFactory = (_, _) => Task.FromResult<IXfyunIatSocket>(socket);

        var exception = await Assert.ThrowsAsync<AsrException>(
            () => provider.TranscribeAsync(AsrTestData.Pcm(2560), new AsrCallOptions()));

        Assert.Contains("没有返回识别文本", exception.Message);
    }

    [Fact]
    public async Task 超时映射为AsrException()
    {
        var provider = Create(AsrTestData.XfyunProfile(timeoutSeconds: 1));
        var socket = new FakeIatSocket
        {
            ReceiveOverride = async cancellationToken =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return null;
            }
        };
        provider.SocketFactory = (_, _) => Task.FromResult<IXfyunIatSocket>(socket);

        var exception = await Assert.ThrowsAsync<AsrException>(
            () => provider.TranscribeAsync(AsrTestData.Pcm(2560), new AsrCallOptions()));

        Assert.Contains("超时", exception.Message);
        Assert.True(socket.Disposed);
    }

    [Fact]
    public async Task 调用方取消_抛出OperationCanceledException()
    {
        var provider = Create(AsrTestData.XfyunProfile());
        var socket = new FakeIatSocket
        {
            ReceiveOverride = async cancellationToken =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return null;
            }
        };
        provider.SocketFactory = (_, _) => Task.FromResult<IXfyunIatSocket>(socket);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(200);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.TranscribeAsync(AsrTestData.Pcm(2560), new AsrCallOptions(), cts.Token));
        Assert.True(socket.Disposed);
    }

    [Fact]
    public async Task 音频为空或采样率声道不支持时_抛中文异常()
    {
        var provider = Create(AsrTestData.XfyunProfile());
        var empty = await Assert.ThrowsAsync<AsrException>(
            () => provider.TranscribeAsync(AsrTestData.Pcm(0), new AsrCallOptions()));
        Assert.Contains("音频数据为空", empty.Message);

        var wrongRate = await Assert.ThrowsAsync<AsrException>(
            () => provider.TranscribeAsync(AudioClip.FromPcm16(new byte[100], 44100), new AsrCallOptions()));
        Assert.Contains("16k / 8k", wrongRate.Message);

        var wrongChannels = await Assert.ThrowsAsync<AsrException>(
            () => provider.TranscribeAsync(AudioClip.FromPcm16(new byte[100], 16000, 2), new AsrCallOptions()));
        Assert.Contains("单声道", wrongChannels.Message);
    }

    [Fact]
    public void 结果拼装_支持动态修正apd与rpl()
    {
        var assembler = new XfyunTranscriptAssembler();
        assembler.Append(ParseResult("{\"sn\":1,\"ls\":false,\"pgs\":\"apd\",\"ws\":[{\"cw\":[{\"w\":\"你好\"}]}]}"));
        Assert.Equal("你好", assembler.GetText());

        assembler.Append(ParseResult(
            "{\"sn\":2,\"ls\":false,\"pgs\":\"rpl\",\"rg\":[1,1],\"ws\":[{\"cw\":[{\"w\":\"您好\"}]}]}"));
        Assert.Equal("您好", assembler.GetText());

        assembler.Append(ParseResult("{\"sn\":3,\"ls\":false,\"pgs\":\"apd\",\"ws\":[{\"cw\":[{\"w\":\"世界\"}]}]}"));
        Assert.Equal("您好世界", assembler.GetText());

        assembler.Reset();
        Assert.Equal("", assembler.GetText());
    }

    [Fact]
    public void 结果拼装_兼容rl字段与多候选只取第一个()
    {
        var assembler = new XfyunTranscriptAssembler();
        assembler.Append(ParseResult(
            "{\"sn\":1,\"ws\":[{\"cw\":[{\"w\":\"梁\"},{\"w\":\"良\"}]},{\"cw\":[{\"w\":\"先生\"}]}]}"));
        Assert.Equal("梁先生", assembler.GetText());

        assembler.Append(ParseResult("{\"sn\":2,\"pgs\":\"rpl\",\"rl\":[1,1],\"ws\":[{\"cw\":[{\"w\":\"王\"}]}]}"));
        Assert.Equal("王", assembler.GetText());
    }

    [Fact]
    public void business参数_ExtraJson可覆盖且eos与vad_eos同步()
    {
        var provider = Create(AsrTestData.XfyunProfile());
        var extra = JsonNode.Parse("{\"eos\":3000,\"ptt\":1,\"business\":{\"dwa\":\"wpgs\"}}")!.AsObject();

        var business = provider.BuildBusiness("en", extra);

        Assert.Equal("en_us", business["language"]!.GetValue<string>());
        Assert.Equal("iat", business["domain"]!.GetValue<string>());
        Assert.Equal("mandarin", business["accent"]!.GetValue<string>());
        Assert.Equal(3000, business["eos"]!.GetValue<int>());
        Assert.Equal(3000, business["vad_eos"]!.GetValue<int>());
        Assert.Equal(1, business["ptt"]!.GetValue<int>());
        Assert.Equal("wpgs", business["dwa"]!.GetValue<string>());
    }

    [Fact]
    public void business参数_vad_eos覆盖时同步到eos()
    {
        var provider = Create(AsrTestData.XfyunProfile());
        var extra = JsonNode.Parse("{\"vad_eos\":5000}")!.AsObject();

        var business = provider.BuildBusiness("zh", extra);

        Assert.Equal(5000, business["eos"]!.GetValue<int>());
        Assert.Equal(5000, business["vad_eos"]!.GetValue<int>());
    }

    [Fact]
    public async Task 日志不包含密钥()
    {
        var logger = new FakeAppLogger();
        var provider = Create(
            AsrTestData.XfyunProfile(appId: "appid-secret", apiKey: "key-secret", apiSecret: "secret-secret"),
            logger);
        var socket = new FakeIatSocket();
        socket.ReceiveQueue.Enqueue(ResultJson(1, "测试", 2, last: true));
        provider.SocketFactory = (_, _) => Task.FromResult<IXfyunIatSocket>(socket);

        await provider.TranscribeAsync(AsrTestData.Pcm(1280), new AsrCallOptions());

        Assert.DoesNotContain("key-secret", logger.AllText);
        Assert.DoesNotContain("secret-secret", logger.AllText);
    }

    [Fact]
    public void 缺少凭据时_构造抛中文异常()
    {
        var missingAppId = Assert.Throws<AsrException>(() =>
            Create(AsrTestData.XfyunProfile(appId: "")));
        Assert.Contains("AppId", missingAppId.Message);

        var missingApiKey = Assert.Throws<AsrException>(() =>
            Create(AsrTestData.XfyunProfile(apiKey: "")));
        Assert.Contains("APIKey", missingApiKey.Message);

        var missingApiSecret = Assert.Throws<AsrException>(() =>
            Create(AsrTestData.XfyunProfile(apiSecret: "")));
        Assert.Contains("APISecret", missingApiSecret.Message);
    }

    private static JsonObject ParseResult(string json) => JsonNode.Parse(json)!.AsObject();
}
