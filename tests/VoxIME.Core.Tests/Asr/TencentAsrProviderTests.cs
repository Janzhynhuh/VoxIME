using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VoxIME.Core.Abstractions;
using VoxIME.Core.Asr.Providers;
using VoxIME.Core.Configuration;
using Xunit;

namespace VoxIME.Core.Tests.Asr;

public sealed class TencentAsrProviderTests
{
    private static readonly DateTimeOffset FixedClock = DateTimeOffset.FromUnixTimeSeconds(1700000000);

    private static TencentAsrProvider Create(
        ProviderProfile profile,
        FakeHttpMessageHandler handler,
        FakeAppLogger? logger = null,
        DateTimeOffset? clock = null)
    {
        var provider = new TencentAsrProvider(
            profile,
            AsrTestData.HttpClient(handler),
            logger ?? new FakeAppLogger());
        provider.Clock = () => clock ?? FixedClock;
        return provider;
    }

    private static string SuccessJson(string text = "腾讯识别结果", string requestId = "req-123") =>
        "{\"Response\":{\"Result\":\"" + text + "\",\"RequestId\":\"" + requestId + "\",\"AudioDuration\":1500}}";

    [Fact]
    public async Task 请求_TC3签名头与请求体结构()
    {
        var handler = FakeHttpMessageHandler.Json(SuccessJson());
        var provider = Create(AsrTestData.TencentProfile(), handler);

        await provider.TranscribeAsync(AsrTestData.Silence(2), new AsrCallOptions());

        var request = handler.SingleRequest;
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://asr.tencentcloudapi.com/", request.RequestUri.ToString());

        var authorization = request.Header("Authorization")!;
        Assert.StartsWith("TC3-HMAC-SHA256 Credential=AKIDEXAMPLE/2023-11-14/asr/tc3_request", authorization);
        Assert.Contains("SignedHeaders=content-type;host", authorization);
        Assert.Matches(new Regex("Signature=[0-9a-f]{64}$"), authorization);
        Assert.Equal("asr.tencentcloudapi.com", request.Header("Host"));
        Assert.Equal("SentenceRecognition", request.Header("X-TC-Action"));
        Assert.Equal("2019-06-14", request.Header("X-TC-Version"));
        Assert.Equal("1700000000", request.Header("X-TC-Timestamp"));
        Assert.Equal("ap-guangzhou", request.Header("X-TC-Region"));
        Assert.Contains("VoxIME/0.1", request.Header("User-Agent"));

        var body = request.JsonBody!;
        Assert.Equal("16k_zh", body["EngSerViceType"]!.GetValue<string>());
        Assert.Equal(1, body["SourceType"]!.GetValue<int>());
        Assert.Equal("wav", body["VoiceFormat"]!.GetValue<string>());
        Assert.Equal(0, body["FilterDirty"]!.GetValue<int>());
        Assert.Equal(0, body["FilterModal"]!.GetValue<int>());
        Assert.Equal(1, body["ConvertNumMode"]!.GetValue<int>());
        var audioBytes = Convert.FromBase64String(body["Data"]!.GetValue<string>());
        Assert.Equal("RIFF"u8.ToArray(), audioBytes[..4]);
        Assert.Equal(audioBytes.Length, body["DataLen"]!.GetValue<int>());
    }

    [Fact]
    public async Task 签名可通过独立实现核对()
    {
        var handler = FakeHttpMessageHandler.Json(SuccessJson());
        var provider = Create(AsrTestData.TencentProfile(secretId: "AKIDEXAMPLE", secretKey: "SECRETKEYEXAMPLE"), handler);

        await provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions());

        var request = handler.SingleRequest;
        var contentType = request.Header("Content-Type")!;
        var expectedSignature = ComputeExpectedSignature(
            "SECRETKEYEXAMPLE",
            "2023-11-14",
            request.BodyText,
            "asr.tencentcloudapi.com",
            contentType);
        var authorization = request.Header("Authorization")!;
        Assert.EndsWith("Signature=" + expectedSignature, authorization);
    }

    [Theory]
    [InlineData("zh", "16k_zh")]
    [InlineData("zh-CN", "16k_zh")]
    [InlineData("auto", "16k_zh")]
    [InlineData("", "16k_zh")]
    [InlineData("en", "16k_en")]
    [InlineData("yue", "16k_yue")]
    [InlineData("ja", "16k_ja")]
    [InlineData("16k_zh-PY", "16k_zh-PY")]
    [InlineData("8k_zh", "8k_zh")]
    public void 语言映射_生成腾讯云引擎类型(string language, string expected)
    {
        Assert.Equal(expected, TencentAsrProvider.MapEngineType(language));
    }

    [Fact]
    public async Task ExtraJson_可覆盖region与引擎类型()
    {
        var handler = FakeHttpMessageHandler.Json(SuccessJson());
        var provider = Create(
            AsrTestData.TencentProfile(extraJson: "{\"region\":\"ap-beijing\",\"EngSerViceType\":\"16k_en\"}"),
            handler);

        await provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions());

        Assert.Equal("ap-beijing", handler.SingleRequest.Header("X-TC-Region"));
        Assert.Equal("16k_en", handler.SingleRequest.JsonBody!["EngSerViceType"]!.GetValue<string>());
    }

    [Fact]
    public async Task 解析Response_Result与RequestId()
    {
        var handler = FakeHttpMessageHandler.Json(SuccessJson("你好腾讯云", "req-abc"));
        var provider = Create(AsrTestData.TencentProfile(), handler);

        var result = await provider.TranscribeAsync(AsrTestData.Silence(2), new AsrCallOptions());

        Assert.Equal("你好腾讯云", result.Text);
        Assert.Equal("req-abc", result.RequestId);
        Assert.Equal("16k_zh", result.DetectedLanguage);
        Assert.Equal(2, result.AudioDuration.TotalSeconds, 2);
        Assert.Equal(AsrProviderTypeIds.Tencent, result.ProviderId);
    }

    [Fact]
    public async Task 响应Error节点_抛中文AsrException并带错误码()
    {
        var handler = FakeHttpMessageHandler.Json(
            "{\"Response\":{\"Error\":{\"Code\":\"FailedOperation.ServiceIsolate\",\"Message\":\"服务未开通\"},\"RequestId\":\"r1\"}}");
        var provider = Create(AsrTestData.TencentProfile(), handler);

        var exception = await Assert.ThrowsAsync<AsrException>(
            () => provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions()));

        Assert.Contains("FailedOperation.ServiceIsolate", exception.Message);
        Assert.Contains("服务未开通", exception.Message);
    }

    [Fact]
    public async Task 非2xx_抛带状态码的AsrException()
    {
        var handler = FakeHttpMessageHandler.Json("{\"error\":\"boom\"}", System.Net.HttpStatusCode.InternalServerError);
        var provider = Create(AsrTestData.TencentProfile(), handler);

        var exception = await Assert.ThrowsAsync<AsrException>(
            () => provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions()));

        Assert.Equal(500, exception.StatusCode);
        Assert.Contains("500", exception.Message);
    }

    [Fact]
    public async Task 时钟注入_决定签名日期与时间戳()
    {
        var handler = FakeHttpMessageHandler.Json(SuccessJson());
        var clock = DateTimeOffset.Parse("2030-01-02T03:04:05Z", CultureInfo.InvariantCulture);
        var provider = Create(AsrTestData.TencentProfile(), handler, clock: clock);

        await provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions());

        Assert.Equal(clock.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            handler.SingleRequest.Header("X-TC-Timestamp"));
        Assert.Contains("/2030-01-02/asr/tc3_request", handler.SingleRequest.Header("Authorization"));
    }

    [Fact]
    public async Task 超时映射为AsrException()
    {
        var handler = new FakeHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
            return FakeHttpMessageHandler.JsonResponse(SuccessJson());
        });
        var provider = Create(AsrTestData.TencentProfile(timeoutSeconds: 1), handler);

        var exception = await Assert.ThrowsAsync<AsrException>(
            () => provider.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions()));

        Assert.Contains("超时", exception.Message);
    }

    [Fact]
    public void 缺少SecretId或SecretKey时_构造抛中文异常()
    {
        var handler = FakeHttpMessageHandler.Json(SuccessJson());

        var missingSecretId = Assert.Throws<AsrException>(() =>
            Create(AsrTestData.TencentProfile(secretId: ""), handler));
        Assert.Contains("SecretId", missingSecretId.Message);

        var missingSecretKey = Assert.Throws<AsrException>(() =>
            Create(AsrTestData.TencentProfile(secretKey: ""), handler));
        Assert.Contains("SecretKey", missingSecretKey.Message);
    }

    [Fact]
    public void 签名工具_规范请求与待签名字符串结构正确()
    {
        const string contentType = "application/json; charset=utf-8";
        const string host = "asr.tencentcloudapi.com";
        var canonical = TencentTc3Signer.BuildCanonicalRequest(
            "POST",
            "/",
            "",
            TencentTc3Signer.BuildCanonicalHeaders(host, contentType),
            TencentTc3Signer.SignedHeaders,
            "abc123");
        Assert.Equal(
            "POST\n/\n\ncontent-type:application/json; charset=utf-8\nhost:asr.tencentcloudapi.com\n\ncontent-type;host\nabc123",
            canonical);

        var stringToSign = TencentTc3Signer.BuildStringToSign(1700000000, "2023-11-14", "asr", "hashed");
        Assert.Equal("TC3-HMAC-SHA256\n1700000000\n2023-11-14/asr/tc3_request\nhashed", stringToSign);

        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            TencentTc3Signer.Sha256Hex("abc"));
    }

    private static string ComputeExpectedSignature(
        string secretKey,
        string date,
        string requestPayload,
        string host,
        string contentType)
    {
        var hashedPayload = Sha256Hex(requestPayload);
        var canonicalRequest =
            $"POST\n/\n\ncontent-type:{contentType}\nhost:{host}\n\ncontent-type;host\n{hashedPayload}";
        var stringToSign = $"TC3-HMAC-SHA256\n{FixedClock.ToUnixTimeSeconds()}\n{date}/asr/tc3_request\n{Sha256Hex(canonicalRequest)}";

        var secretDate = Hmac(Encoding.UTF8.GetBytes("TC3" + secretKey), date);
        var secretService = Hmac(secretDate, "asr");
        var secretSigning = Hmac(secretService, "tc3_request");
        var signature = Convert.ToHexString(Hmac(secretSigning, stringToSign)).ToLowerInvariant();
        return signature;
    }

    private static string Sha256Hex(string data) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data))).ToLowerInvariant();

    private static byte[] Hmac(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }
}
