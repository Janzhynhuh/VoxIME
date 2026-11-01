using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace VoxIME.Core.Asr.Providers;

/// <summary>
/// 腾讯云 API 3.0（TC3-HMAC-SHA256）签名工具。纯函数、可单测；腾讯云一句话识别固定 Service=asr。
/// 文档：https://cloud.tencent.com/document/api/1131/76642 （签名方法 v3）。
/// </summary>
public static class TencentTc3Signer
{
    public const string Algorithm = "TC3-HMAC-SHA256";
    public const string Service = "asr";
    public const string RequestTimestampFormat = "yyyy-MM-dd";

    /// <summary>参与签名的头（顺序固定、全小写）。</summary>
    public const string SignedHeaders = "content-type;host";

    /// <summary>SHA256 十六进制（小写）。</summary>
    public static string Sha256Hex(string data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>HMAC-SHA256 十六进制（小写）。</summary>
    public static string HmacSha256Hex(string key, string data)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(data);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>HMAC-SHA256 后 Base64（腾讯云签名密钥推导/签名都基于字节链，这里用于测试核对）。</summary>
    public static string HmacSha256Base64(string key, string data)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(data);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        return Convert.ToBase64String(hash);
    }

    /// <summary>构造规范请求头字符串（结尾保留换行，符合 TC3 规范）。</summary>
    public static string BuildCanonicalHeaders(string host, string contentType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(contentType);
        return $"content-type:{contentType}\nhost:{host}\n";
    }

    /// <summary>构造规范请求（CanonicalRequest）。</summary>
    public static string BuildCanonicalRequest(
        string method,
        string canonicalUri,
        string canonicalQueryString,
        string canonicalHeaders,
        string signedHeaders,
        string hashedRequestPayload)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(canonicalUri);
        ArgumentNullException.ThrowIfNull(canonicalQueryString);
        ArgumentNullException.ThrowIfNull(canonicalHeaders);
        ArgumentNullException.ThrowIfNull(signedHeaders);
        ArgumentNullException.ThrowIfNull(hashedRequestPayload);
        return $"{method}\n{canonicalUri}\n{canonicalQueryString}\n{canonicalHeaders}\n{signedHeaders}\n{hashedRequestPayload}";
    }

    /// <summary>构造待签名字符串（StringToSign）。</summary>
    public static string BuildStringToSign(long timestamp, string date, string service, string hashedCanonicalRequest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(date);
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentNullException.ThrowIfNull(hashedCanonicalRequest);
        return $"{Algorithm}\n{timestamp}\n{date}/{service}/tc3_request\n{hashedCanonicalRequest}";
    }

    /// <summary>由 TC3 密钥链计算最终签名（小写十六进制）。</summary>
    public static string ComputeSignature(string secretKey, string date, string service, string stringToSign)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(date);
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentNullException.ThrowIfNull(stringToSign);

        var secretDate = HmacSha256(Encoding.UTF8.GetBytes("TC3" + secretKey), date);
        var secretService = HmacSha256(secretDate, service);
        var secretSigning = HmacSha256(secretService, "tc3_request");
        return Convert.ToHexString(HmacSha256(secretSigning, stringToSign)).ToLowerInvariant();
    }

    /// <summary>构造 Authorization 头。</summary>
    public static string BuildAuthorization(string secretId, string date, string service, string signature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretId);
        ArgumentException.ThrowIfNullOrWhiteSpace(date);
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentNullException.ThrowIfNull(signature);
        return $"{Algorithm} Credential={secretId}/{date}/{service}/tc3_request, " +
               $"SignedHeaders={SignedHeaders}, Signature={signature}";
    }

    /// <summary>
    /// 一站式生成腾讯云一句话识别的 Authorization：POST /、Content-Type 与 host 参与签名。
    /// </summary>
    public static string CreateAuthorization(
        string secretId,
        string secretKey,
        string host,
        string contentType,
        string requestPayload,
        long timestamp,
        string date)
    {
        ArgumentNullException.ThrowIfNull(requestPayload);
        var hashedPayload = Sha256Hex(requestPayload);
        var canonicalRequest = BuildCanonicalRequest(
            "POST",
            "/",
            "",
            BuildCanonicalHeaders(host, contentType),
            SignedHeaders,
            hashedPayload);
        var stringToSign = BuildStringToSign(timestamp, date, Service, Sha256Hex(canonicalRequest));
        var signature = ComputeSignature(secretKey, date, Service, stringToSign);
        return BuildAuthorization(secretId, date, Service, signature);
    }

    /// <summary>把时钟转成腾讯云要求的日期字符串（UTC，yyyy-MM-dd）。</summary>
    public static string FormatDate(DateTimeOffset timestamp) =>
        timestamp.UtcDateTime.ToString(RequestTimestampFormat, CultureInfo.InvariantCulture);

    private static byte[] HmacSha256(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }
}
