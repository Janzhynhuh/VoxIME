using System.Text.Json;

namespace VoxIME.Core.Configuration;

/// <summary>导入/导出配置文件的外层信封，便于未来兼容旧格式。</summary>
public sealed class ConfigExchangeEnvelope
{
    public string Format { get; set; } = ConfigExchangeService.FormatName;
    public int Version { get; set; } = ConfigExchangeService.CurrentVersion;
    public string AppVersion { get; set; } = AppInfo.Version;
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.Now;
    public bool IncludesSecrets { get; set; }
    public AppConfig Config { get; set; } = new();
}

public sealed class ConfigExchangeException : Exception
{
    public ConfigExchangeException(string message) : base(message)
    {
    }
}

/// <summary>
/// 配置导入导出。配置文件中的 API Key 为明文，导出时可选择排除；
/// 导入只处理配置，不包含历史记录、日志、模型文件。
/// </summary>
public static class ConfigExchangeService
{
    public const string FormatName = "voxime-config";
    public const int CurrentVersion = 1;
    private const int MaxConfigBytes = 8 * 1024 * 1024;

    public static string Export(AppConfig config, bool includeSecrets)
    {
        ArgumentNullException.ThrowIfNull(config);

        var clone = ConfigJson.CloneAppConfig(config);
        foreach (var profile in clone.ProviderProfiles)
        {
            // 导出文件永远不写旧版 DPAPI 密文字段。
            profile.ApiKeyProtected = null;
            profile.ApiSecretProtected = null;
            profile.SecretIdProtected = null;

            if (!includeSecrets)
            {
                profile.ApiKey = "";
                profile.ApiSecret = "";
                profile.SecretId = "";
            }
        }

        var envelope = new ConfigExchangeEnvelope
        {
            Version = CurrentVersion,
            AppVersion = AppInfo.Version,
            ExportedAt = DateTimeOffset.Now,
            IncludesSecrets = includeSecrets,
            Config = clone
        };

        return JsonSerializer.Serialize(envelope, ConfigJson.Options);
    }

    public static AppConfig Import(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ConfigExchangeException("配置文件为空。");
        }

        if (json.Length > MaxConfigBytes)
        {
            throw new ConfigExchangeException("配置文件过大，无法导入。");
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ConfigExchangeException("配置文件根节点必须是 JSON 对象。");
            }

            AppConfig? config;
            if (root.TryGetProperty("format", out var formatElement))
            {
                var format = formatElement.GetString();
                if (!string.Equals(format, FormatName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ConfigExchangeException($"不是 VoxIME 配置文件（format={format}）。");
                }

                if (!root.TryGetProperty("version", out var versionElement) ||
                    !versionElement.TryGetInt32(out var version))
                {
                    throw new ConfigExchangeException("配置文件缺少版本号。");
                }

                if (version > CurrentVersion)
                {
                    throw new ConfigExchangeException($"配置文件版本 {version} 高于当前程序支持的版本 {CurrentVersion}，请升级 VoxIME。");
                }

                if (!root.TryGetProperty("config", out var configElement) ||
                    configElement.ValueKind != JsonValueKind.Object)
                {
                    throw new ConfigExchangeException("配置文件缺少 config 内容。");
                }

                config = configElement.Deserialize<AppConfig>(ConfigJson.Options);
            }
            else
            {
                // 兼容直接导入旧版 config.json。
                config = root.Deserialize<AppConfig>(ConfigJson.Options);
            }

            if (config is null)
            {
                throw new ConfigExchangeException("无法解析配置文件内容。");
            }

            config.Normalize();
            if (config.ProviderProfiles.Count == 0)
            {
                throw new ConfigExchangeException("配置中没有任何识别服务，导入已取消。");
            }

            return config;
        }
        catch (ConfigExchangeException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new ConfigExchangeException("配置文件不是有效的 JSON：" + ex.Message);
        }
        catch (Exception ex)
        {
            throw new ConfigExchangeException("读取配置文件失败：" + ex.Message);
        }
    }
}
