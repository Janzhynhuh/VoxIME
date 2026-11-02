using System.Text.Json;
using VoxIME.Core.Logging;

namespace VoxIME.Core.Configuration;

public interface IConfigStore
{
    string ConfigFilePath { get; }
    AppConfig Current { get; }
    event EventHandler<AppConfig>? ConfigChanged;

    /// <summary>从磁盘加载；文件不存在时创建默认配置并落盘。</summary>
    AppConfig Load();

    /// <summary>修正字段后原子写入磁盘并更新 Current。</summary>
    void Save(AppConfig config);

    void ResetToDefaults();

    string? GetApiKey(ProviderProfile profile);
    void SetApiKey(ProviderProfile profile, string? apiKey);
}

/// <summary>JSON 配置存储。敏感字段直接以明文保存，便于备份/导入；*Protected 字段仅用于旧版迁移。</summary>
public sealed class JsonConfigStore : IConfigStore
{
    private readonly ISecretProtector _legacyProtector;
    private readonly IAppLogger _logger;
    private readonly object _sync = new();
    private AppConfig _current = AppConfig.CreateDefault();

    public JsonConfigStore(string? configPath = null, ISecretProtector? protector = null, IAppLogger? logger = null)
    {
        ConfigFilePath = string.IsNullOrWhiteSpace(configPath) ? AppInfo.DefaultConfigPath : configPath!;
        // 旧版 DPAPI 配置迁移用；新配置不再加密写入。
        _legacyProtector = protector ?? new DpapiSecretProtector();
        _logger = logger ?? NullLogger.Instance;
    }

    public string ConfigFilePath { get; }
    public AppConfig Current => _current;
    public event EventHandler<AppConfig>? ConfigChanged;

    public AppConfig Load()
    {
        lock (_sync)
        {
            try
            {
                if (File.Exists(ConfigFilePath))
                {
                    var json = File.ReadAllText(ConfigFilePath);
                    var config = JsonSerializer.Deserialize<AppConfig>(json, ConfigJson.Options);
                    if (config is not null)
                    {
                        var migrated = MigrateLegacySecrets(config);
                        config.Normalize();
                        if (migrated)
                        {
                            TryPersist(config);
                        }

                        _current = config;
                        return _current;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"读取配置失败，将使用默认配置：{ex.Message}");
                TryBackupBrokenConfig();
            }

            _current = AppConfig.CreateDefault();
            _current.Normalize();
            TryPersist(_current);
            return _current;
        }
    }

    public void Save(AppConfig config)
    {
        lock (_sync)
        {
            MigrateLegacySecrets(config);
            config.Normalize();
            TryPersist(config);
            _current = config;
        }
        ConfigChanged?.Invoke(this, _current);
    }

    public void ResetToDefaults()
    {
        var config = AppConfig.CreateDefault();
        Save(config);
    }

    public string? GetApiKey(ProviderProfile profile)
    {
        if (!string.IsNullOrEmpty(profile.ApiKey))
        {
            return profile.ApiKey;
        }

        return string.IsNullOrEmpty(profile.ApiKeyProtected) ? null : _legacyProtector.Unprotect(profile.ApiKeyProtected);
    }

    public void SetApiKey(ProviderProfile profile, string? apiKey)
    {
        profile.ApiKey = apiKey?.Trim() ?? "";
        profile.ApiKeyProtected = null;
    }

    private void TryPersist(AppConfig config)
    {
        try
        {
            var directory = Path.GetDirectoryName(ConfigFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(config, ConfigJson.Options);
            var tempPath = ConfigFilePath + ".tmp";
            File.WriteAllText(tempPath, json);
            if (File.Exists(ConfigFilePath))
            {
                File.Replace(tempPath, ConfigFilePath, null);
            }
            else
            {
                File.Move(tempPath, ConfigFilePath);
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"保存配置失败：{ex.Message}", ex);
            throw;
        }
    }

    private void TryBackupBrokenConfig()
    {
        try
        {
            if (!File.Exists(ConfigFilePath))
            {
                return;
            }

            var backup = ConfigFilePath + ".bad.json";
            File.Copy(ConfigFilePath, backup, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.Warn($"备份损坏配置失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 把旧版 DPAPI 字段解密到明文并清空 *Protected。
    /// 新配置写盘时只保留明文；旧配置在首次加载/保存时自动完成迁移。
    /// </summary>
    private bool MigrateLegacySecrets(AppConfig config)
    {
        var migrated = false;
        foreach (var profile in config.ProviderProfiles)
        {
            if (string.IsNullOrEmpty(profile.ApiKey) && !string.IsNullOrEmpty(profile.ApiKeyProtected))
            {
                profile.ApiKey = _legacyProtector.Unprotect(profile.ApiKeyProtected);
                migrated = true;
            }

            if (string.IsNullOrEmpty(profile.ApiSecret) && !string.IsNullOrEmpty(profile.ApiSecretProtected))
            {
                profile.ApiSecret = _legacyProtector.Unprotect(profile.ApiSecretProtected);
                migrated = true;
            }

            if (string.IsNullOrEmpty(profile.SecretId) && !string.IsNullOrEmpty(profile.SecretIdProtected))
            {
                profile.SecretId = _legacyProtector.Unprotect(profile.SecretIdProtected);
                migrated = true;
            }

            if (profile.ApiKeyProtected is not null || profile.ApiSecretProtected is not null || profile.SecretIdProtected is not null)
            {
                migrated = true;
            }

            profile.ApiKeyProtected = null;
            profile.ApiSecretProtected = null;
            profile.SecretIdProtected = null;
        }

        return migrated;
    }
}
