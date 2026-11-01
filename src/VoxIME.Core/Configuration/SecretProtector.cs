using System.Security.Cryptography;
using System.Text;

namespace VoxIME.Core.Configuration;

/// <summary>敏感凭据保护器。真实实现使用 Windows DPAPI（CurrentUser）。</summary>
public interface ISecretProtector
{
    string Protect(string plainText);
    string Unprotect(string protectedText);
}

/// <summary>DPAPI 实现：密文只能被当前 Windows 用户解密。</summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VoxIME.Secret.v1");

    public string Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            return "";
        }

        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public string Unprotect(string protectedText)
    {
        if (string.IsNullOrEmpty(protectedText))
        {
            return "";
        }

        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedText), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception)
        {
            // 配置被复制到其他用户/机器时无法解密，视为无凭据。
            return "";
        }
    }
}

/// <summary>明文透传（仅用于测试）。</summary>
public sealed class NullSecretProtector : ISecretProtector
{
    public string Protect(string plainText) => plainText;
    public string Unprotect(string protectedText) => protectedText;
}
