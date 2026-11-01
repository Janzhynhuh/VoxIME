using System.Net;
using System.Text;
using VoxIME.Core.Abstractions;
using VoxIME.Core.Asr.Providers;
using VoxIME.Core.Configuration;
using Xunit;

namespace VoxIME.Core.Tests.Asr;

public sealed class OpenAiCompatibleTranscriptionProviderTests
{
    private static OpenAiCompatibleTranscriptionProvider Create(
        ProviderProfile profile,
        FakeHttpMessageHandler handler,
        FakeAppLogger? logger = null) =>
        new(profile, AsrTestData.HttpClient(handler), logger ?? new FakeAppLogger());

    [Fact]
    public async Task 请求_使用BaseUrl拼接audio_transcriptions并带UserAgent()
    {
        var handler = FakeHttpMessageHandler.Json("{\"text\":\"你好\"}");
        var provider = Create(AsrTestData.TranscriptionProfile(), handler);

        await provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions());

        var request = handler.SingleRequest;
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.openai.com/v1/audio/transcriptions", request.RequestUri.ToString());
        Assert.Contains("VoxIME/0.1", request.Header("User-Agent"));
        Assert.Equal("Bearer sk-test-secret-123456", request.Header("Authorization"));
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1/audio/transcriptions")]
    [InlineData("https://api.openai.com/v1/", "https://api.openai.com/v1/audio/transcriptions")]
    [InlineData("https://api.openai.com/v1/audio/transcriptions", "https://api.openai.com/v1/audio/transcriptions")]
    [InlineData("http://127.0.0.1:1234/v1/", "http://127.0.0.1:1234/v1/audio/transcriptions")]
    [InlineData("https://api.siliconflow.cn/v1  ", "https://api.siliconflow.cn/v1/audio/transcriptions")]
    public async Task BaseUrl容错_拼装出正确Endpoint(string baseUrl, string expected)
    {
        var handler = FakeHttpMessageHandler.Json("{\"text\":\"ok\"}");
        var provider = Create(AsrTestData.TranscriptionProfile(baseUrl: baseUrl), handler);

        await provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions());

        Assert.Equal(expected, handler.SingleRequest.RequestUri.ToString());
    }

    [Fact]
    public async Task Multipart_包含wav文件与默认表单字段()
    {
        var handler = FakeHttpMessageHandler.Json("{\"text\":\"hi\"}");
        var profile = AsrTestData.TranscriptionProfile(prompt: "这是提示词");
        var provider = Create(profile, handler);

        await provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions());

        var request = handler.SingleRequest;
        Assert.Contains("multipart/form-data", request.ContentType);
        Assert.Equal("audio.wav", request.MultipartFileName);
        Assert.Contains("audio/wav", request.MultipartFileContentType);
        Assert.NotNull(request.MultipartFileBytes);
        Assert.Equal("RIFF"u8.ToArray(), request.MultipartFileBytes![..4]);
        Assert.Equal(44 + 32000, request.MultipartFileBytes.Length);
        Assert.Equal("whisper-1", request.MultipartFields["model"]);
        Assert.Equal("zh", request.MultipartFields["language"]);
        Assert.Equal("这是提示词", request.MultipartFields["prompt"]);
        Assert.Equal("json", request.MultipartFields["response_format"]);
        Assert.False(request.MultipartFields.ContainsKey("temperature"));
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("")]
    public async Task 语言为auto或空时_省略language字段(string language)
    {
        var handler = FakeHttpMessageHandler.Json("{\"text\":\"ok\"}");
        var provider = Create(AsrTestData.TranscriptionProfile(language: language), handler);

        await provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions());

        Assert.False(handler.SingleRequest.MultipartFields.ContainsKey("language"));
    }

    [Fact]
    public async Task 调用级语言与提示词优先于服务级配置()
    {
        var handler = FakeHttpMessageHandler.Json("{\"text\":\"ok\"}");
        var provider = Create(AsrTestData.TranscriptionProfile(language: "zh", prompt: "服务级提示"), handler);

        await provider.TranscribeAsync(
            AsrTestData.Silence(1),
            new AsrCallOptions { Language = "en", Prompt = "调用级提示" });

        var fields = handler.SingleRequest.MultipartFields;
        Assert.Equal("en", fields["language"]);
        Assert.Equal("调用级提示", fields["prompt"]);
    }

    [Fact]
    public async Task ExtraJson_可覆盖response_format并追加表单字段()
    {
        var handler = FakeHttpMessageHandler.Json("{\"text\":\"ok\",\"language\":\"en\"}");
        var provider = Create(
            AsrTestData.TranscriptionProfile(extraJson: "{\"response_format\":\"verbose_json\",\"temperature\":0}"),
            handler);

        await provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions());

        var fields = handler.SingleRequest.MultipartFields;
        Assert.Equal("verbose_json", fields["response_format"]);
        Assert.Equal("0", fields["temperature"]);
    }

    [Fact]
    public async Task 调用级ExtraJson优先于服务级ExtraJson()
    {
        var handler = FakeHttpMessageHandler.Json("{\"text\":\"ok\"}");
        var provider = Create(
            AsrTestData.TranscriptionProfile(extraJson: "{\"response_format\":\"json\",\"temperature\":0}"),
            handler);

        await provider.TranscribeAsync(
            AsrTestData.Silence(1),
            new AsrCallOptions { ExtraJson = "{\"response_format\":\"verbose_json\",\"temperature\":0.2}" });

        var fields = handler.SingleRequest.MultipartFields;
        Assert.Equal("verbose_json", fields["response_format"]);
        Assert.Equal("0.2", fields["temperature"]);
    }

    [Fact]
    public async Task 解析普通json响应_text与language()
    {
        var handler = FakeHttpMessageHandler.Json("{\"text\":\"你好，世界\",\"language\":\"zh\"}");
        var provider = Create(AsrTestData.TranscriptionProfile(), handler);

        var result = await provider.TranscribeAsync(AsrTestData.Silence(2), new AsrCallOptions());

        Assert.Equal("你好，世界", result.Text);
        Assert.Equal("zh", result.DetectedLanguage);
        Assert.Equal(AsrProviderTypeIds.OpenAiTranscription, result.ProviderId);
        Assert.Equal(2, result.AudioDuration.TotalSeconds, 2);
    }

    [Fact]
    public async Task 解析verbose_json响应()
    {
        var handler = FakeHttpMessageHandler.Json(
            "{\"task\":\"transcribe\",\"language\":\"en\",\"duration\":1.5,\"text\":\"hello world\"}");
        var provider = Create(AsrTestData.TranscriptionProfile(), handler);

        var result = await provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions());

        Assert.Equal("hello world", result.Text);
        Assert.Equal("en", result.DetectedLanguage);
    }

    [Fact]
    public async Task 纯文本响应_兜底作为识别文本()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("plain text result", Encoding.UTF8, "text/plain")
        }));
        var provider = Create(AsrTestData.TranscriptionProfile(), handler);

        var result = await provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions());

        Assert.Equal("plain text result", result.Text);
    }

    [Fact]
    public async Task 非2xx_抛AsrException并携带状态码与响应片段()
    {
        var handler = FakeHttpMessageHandler.Json(
            "{\"error\":{\"message\":\"invalid api key\"}}", HttpStatusCode.Unauthorized);
        var provider = Create(AsrTestData.TranscriptionProfile(), handler);

        var exception = await Assert.ThrowsAsync<AsrException>(
            () => provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions()));

        Assert.Equal(401, exception.StatusCode);
        Assert.Contains("invalid api key", exception.ResponseBody);
        Assert.Contains("401", exception.Message);
    }

    [Fact]
    public async Task 响应缺少text字段_抛中文异常()
    {
        var handler = FakeHttpMessageHandler.Json("{\"foo\":1}");
        var provider = Create(AsrTestData.TranscriptionProfile(), handler);

        var exception = await Assert.ThrowsAsync<AsrException>(
            () => provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions()));

        Assert.Contains("text", exception.Message);
    }

    [Fact]
    public async Task ApiKey为空时不发送Authorization头()
    {
        var handler = FakeHttpMessageHandler.Json("{\"text\":\"ok\"}");
        var provider = Create(AsrTestData.TranscriptionProfile(apiKey: ""), handler);

        await provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions());

        Assert.Null(handler.SingleRequest.Header("Authorization"));
    }

    [Fact]
    public async Task 日志不包含APIKey()
    {
        var logger = new FakeAppLogger();
        var handler = FakeHttpMessageHandler.Json("{\"text\":\"ok\"}");
        var provider = Create(AsrTestData.TranscriptionProfile(apiKey: "sk-super-secret-key"), handler, logger);

        await provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions());

        Assert.DoesNotContain("sk-super-secret-key", logger.AllText);
        Assert.Contains(logger.Entries, entry => entry.Contains("audio/transcriptions"));
    }

    [Fact]
    public async Task 超时映射为AsrException()
    {
        var handler = new FakeHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
            return FakeHttpMessageHandler.JsonResponse("{}");
        });
        var provider = Create(AsrTestData.TranscriptionProfile(timeoutSeconds: 1), handler);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var exception = await Assert.ThrowsAsync<AsrException>(
            () => provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions()));
        stopwatch.Stop();

        Assert.Contains("超时", exception.Message);
        Assert.True(stopwatch.ElapsedMilliseconds < 15000);
    }

    [Fact]
    public async Task 调用方取消_抛出OperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = FakeHttpMessageHandler.Json("{\"text\":\"ok\"}");
        var provider = Create(AsrTestData.TranscriptionProfile(), handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions(), cts.Token));
    }

    [Fact]
    public async Task 请求中取消_抛出OperationCanceledException()
    {
        var handler = new FakeHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
            return FakeHttpMessageHandler.JsonResponse("{}");
        });
        var provider = Create(AsrTestData.TranscriptionProfile(), handler);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(200);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions(), cts.Token));
    }
}
