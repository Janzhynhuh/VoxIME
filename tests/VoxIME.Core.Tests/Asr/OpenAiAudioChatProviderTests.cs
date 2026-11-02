using System.Net;
using System.Text.Json.Nodes;
using VoxIME.Core.Abstractions;
using VoxIME.Core.Asr.Providers;
using VoxIME.Core.Configuration;
using Xunit;

namespace VoxIME.Core.Tests.Asr;

public sealed class OpenAiAudioChatProviderTests
{
    private static OpenAiAudioChatProvider Create(
        ProviderProfile profile,
        FakeHttpMessageHandler handler,
        FakeAppLogger? logger = null) =>
        new(profile, AsrTestData.HttpClient(handler), logger ?? new FakeAppLogger());

    private static string SuccessJson(string text = "你好，世界", string id = "chatcmpl-123") =>
        "{\"id\":\"" + id + "\",\"object\":\"chat.completion\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"" +
        text + "\"},\"finish_reason\":\"stop\"}]}";

    [Fact]
    public async Task 配置了提示词时_加入system消息()
    {
        var handler = FakeHttpMessageHandler.Json(SuccessJson());
        var provider = Create(AsrTestData.AudioChatProfile(), handler);

        await provider.TranscribeAsync(
            AsrTestData.Silence(1),
            new AsrCallOptions { Prompt = "当前应用：浏览器（chrome）" });

        var messages = handler.SingleRequest.JsonBody!["messages"]!.AsArray();
        Assert.Equal(2, messages.Count);
        Assert.Equal("system", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal("当前应用：浏览器（chrome）", messages[0]!["content"]!.GetValue<string>());
        Assert.Equal("user", messages[1]!["role"]!.GetValue<string>());
    }

    [Fact]
    public async Task 无提示词时_不加入system消息()
    {
        var handler = FakeHttpMessageHandler.Json(SuccessJson());
        var provider = Create(AsrTestData.AudioChatProfile(), handler);

        await provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions());

        var messages = handler.SingleRequest.JsonBody!["messages"]!.AsArray();
        Assert.Single(messages);
        Assert.Equal("user", messages[0]!["role"]!.GetValue<string>());
    }

    [Fact]
    public async Task 请求_使用chat_completions端点与UserAgent()
    {
        var handler = FakeHttpMessageHandler.Json(SuccessJson());
        var provider = Create(AsrTestData.AudioChatProfile(), handler);

        await provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions());

        var request = handler.SingleRequest;
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.xiaomimimo.com/v1/chat/completions", request.RequestUri.ToString());
        Assert.Contains("VoxIME/0.1", request.Header("User-Agent"));
        Assert.Contains("application/json", request.ContentType);
    }

    [Fact]
    public async Task 请求体_包含input_audio的base64与wav格式()
    {
        var handler = FakeHttpMessageHandler.Json(SuccessJson());
        var provider = Create(AsrTestData.AudioChatProfile(), handler);

        await provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions());

        var body = handler.SingleRequest.JsonBody!;
        Assert.Equal("mimo-v2.5-asr", body["model"]!.GetValue<string>());
        var message = body["messages"]!.AsArray()[0]!.AsObject();
        Assert.Equal("user", message["role"]!.GetValue<string>());
        var content = message["content"]!.AsArray()[0]!.AsObject();
        Assert.Equal("input_audio", content["type"]!.GetValue<string>());
        var inputAudio = content["input_audio"]!.AsObject();
        Assert.Equal("wav", inputAudio["format"]!.GetValue<string>());
        var base64 = inputAudio["data"]!.GetValue<string>();
        var bytes = Convert.FromBase64String(base64);
        Assert.Equal("RIFF"u8.ToArray(), bytes[..4]);
        Assert.Equal("WAVE"u8.ToArray(), bytes[8..12]);
    }

    [Fact]
    public async Task 语言非auto时_加入asr_options_language()
    {
        var handler = FakeHttpMessageHandler.Json(SuccessJson());
        var provider = Create(AsrTestData.AudioChatProfile(language: "zh"), handler);

        await provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions());

        var body = handler.SingleRequest.JsonBody!;
        Assert.Equal("zh", body["asr_options"]!["language"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("")]
    public async Task 语言为auto时_省略asr_options(string language)
    {
        var handler = FakeHttpMessageHandler.Json(SuccessJson());
        var provider = Create(AsrTestData.AudioChatProfile(language: language), handler);

        await provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions());

        Assert.Null(handler.SingleRequest.JsonBody!["asr_options"]);
    }

    [Fact]
    public async Task ExtraJson_深度合并进请求体且控制字段不进入请求体()
    {
        var handler = FakeHttpMessageHandler.Json(SuccessJson());
        var profile = AsrTestData.AudioChatProfile(
            extraJson: "{\"authHeader\":\"api-key\",\"temperature\":0,\"asr_options\":{\"enable_itn\":true}}");
        var provider = Create(profile, handler);

        await provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions());

        var body = handler.SingleRequest.JsonBody!;
        Assert.Equal(0, body["temperature"]!.GetValue<int>());
        Assert.True(body["asr_options"]!["enable_itn"]!.GetValue<bool>());
        Assert.Equal("zh", body["asr_options"]!["language"]!.GetValue<string>());
        Assert.Null(body["authHeader"]);
        Assert.Null(body["audioDataFormat"]);
    }

    [Fact]
    public async Task data_url模式_音频data为DataURI且省略format()
    {
        var handler = FakeHttpMessageHandler.Json(SuccessJson());
        var profile = AsrTestData.AudioChatProfile(extraJson: "{\"audioDataFormat\":\"data-url\"}");
        var provider = Create(profile, handler);

        await provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions());

        var inputAudio = handler.SingleRequest.JsonBody!["messages"]!.AsArray()[0]!["content"]!.AsArray()[0]!["input_audio"]!.AsObject();
        var data = inputAudio["data"]!.GetValue<string>();
        Assert.StartsWith("data:audio/wav;base64,", data);
        Assert.Null(inputAudio["format"]);
        var decoded = Convert.FromBase64String(data["data:audio/wav;base64,".Length..]);
        Assert.Equal("RIFF"u8.ToArray(), decoded[..4]);
    }

    [Fact]
    public async Task 默认Bearer鉴权_并可通过ExtraJson切换api_key头()
    {
        var handler = FakeHttpMessageHandler.Json(SuccessJson());
        var bearerProvider = Create(AsrTestData.AudioChatProfile(), handler);
        await bearerProvider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions());
        Assert.Equal("Bearer sk-test-secret-123456", handler.SingleRequest.Header("Authorization"));

        var apiKeyHandler = FakeHttpMessageHandler.Json(SuccessJson());
        var apiKeyProvider = Create(
            AsrTestData.AudioChatProfile(extraJson: "{\"authHeader\":\"api-key\"}"),
            apiKeyHandler);
        await apiKeyProvider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions());
        Assert.Equal("sk-test-secret-123456", apiKeyHandler.SingleRequest.Header("api-key"));
        Assert.Null(apiKeyHandler.SingleRequest.Header("Authorization"));
    }

    [Fact]
    public async Task 解析choices_message_content与requestId()
    {
        var handler = FakeHttpMessageHandler.Json(SuccessJson("这是识别文本", "chatcmpl-999"));
        var provider = Create(AsrTestData.AudioChatProfile(), handler);

        var result = await provider.TranscribeAsync(AsrTestData.Silence(2), new AsrCallOptions());

        Assert.Equal("这是识别文本", result.Text);
        Assert.Equal("chatcmpl-999", result.RequestId);
        Assert.Equal(AsrProviderTypeIds.OpenAiAudioChat, result.ProviderId);
        Assert.Equal(2, result.AudioDuration.TotalSeconds, 2);
    }

    [Fact]
    public async Task 内容为数组parts时_拼接文本()
    {
        var handler = FakeHttpMessageHandler.Json(
            "{\"id\":\"x\",\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"你好\"},{\"type\":\"text\",\"text\":\"世界\"}]}}]}");
        var provider = Create(AsrTestData.AudioChatProfile(), handler);

        var result = await provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions());

        Assert.Equal("你好世界", result.Text);
    }

    [Fact]
    public async Task 响应为错误对象时_抛中文AsrException()
    {
        var handler = FakeHttpMessageHandler.Json("{\"error\":{\"message\":\"model not found\",\"code\":\"invalid_model\"}}");
        var provider = Create(AsrTestData.AudioChatProfile(), handler);

        var exception = await Assert.ThrowsAsync<AsrException>(
            () => provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions()));

        Assert.Contains("model not found", exception.Message);
    }

    [Fact]
    public async Task 非2xx_携带状态码()
    {
        var handler = FakeHttpMessageHandler.Json("{\"message\":\"rate limited\"}", HttpStatusCode.TooManyRequests);
        var provider = Create(AsrTestData.AudioChatProfile(), handler);

        var exception = await Assert.ThrowsAsync<AsrException>(
            () => provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions()));

        Assert.Equal(429, exception.StatusCode);
        Assert.Contains("rate limited", exception.ResponseBody);
    }

    [Fact]
    public async Task 响应缺少choices_抛中文异常()
    {
        var handler = FakeHttpMessageHandler.Json("{\"id\":\"x\"}");
        var provider = Create(AsrTestData.AudioChatProfile(), handler);

        var exception = await Assert.ThrowsAsync<AsrException>(
            () => provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions()));

        Assert.Contains("choices", exception.Message);
    }

    [Fact]
    public async Task BaseUrl已包含chat_completions时_不再重复拼接()
    {
        var handler = FakeHttpMessageHandler.Json(SuccessJson());
        var provider = Create(
            AsrTestData.AudioChatProfile(baseUrl: "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions"),
            handler);

        await provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions());

        Assert.Equal(
            "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions",
            handler.SingleRequest.RequestUri.ToString());
    }

    [Fact]
    public async Task 日志不包含APIKey()
    {
        var logger = new FakeAppLogger();
        var handler = FakeHttpMessageHandler.Json(SuccessJson());
        var provider = Create(AsrTestData.AudioChatProfile(apiKey: "sk-mimo-secret"), handler, logger);

        await provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions());

        Assert.DoesNotContain("sk-mimo-secret", logger.AllText);
    }

    [Fact]
    public async Task 超时映射为AsrException()
    {
        var handler = new FakeHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
            return FakeHttpMessageHandler.JsonResponse(SuccessJson());
        });
        var provider = Create(AsrTestData.AudioChatProfile(timeoutSeconds: 1), handler);

        var exception = await Assert.ThrowsAsync<AsrException>(
            () => provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions()));

        Assert.Contains("超时", exception.Message);
    }

    [Fact]
    public void 缺少BaseUrl或Model时_构造抛中文异常()
    {
        var handler = FakeHttpMessageHandler.Json(SuccessJson());
        var missingBaseUrl = Assert.Throws<AsrException>(() =>
            Create(AsrTestData.AudioChatProfile(baseUrl: ""), handler));
        Assert.Contains("BaseUrl", missingBaseUrl.Message);

        var missingModel = Assert.Throws<AsrException>(() =>
            Create(AsrTestData.AudioChatProfile(model: ""), handler));
        Assert.Contains("Model", missingModel.Message);
    }
}
