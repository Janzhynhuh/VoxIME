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

/// <summary>JSON 配置存储：API Key 等敏感字段以 DPAPI 密文落盘。</summary>
public sealed class JsonConfigStore : IConfigStore
{
    private readonly ISecretProtector _protector;
    private readonly IAppLogger _logger;
    private readonly object _sync = new();
    private AppConfig _current = AppConfig.CreateDefault();

    public JsonConfigStore(string? configPath = null, ISecretProtector? protector = null, IAppLogger? logger = null)
    {
        ConfigFilePath = string.IsNullOrWhiteSpace(configPath) ? AppInfo.DefaultConfigPath : configPath!;
        _protector = protector ?? new DpapiSecretProtector();
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
                        DecryptSecrets(config);
                        config.Normalize();
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
            config.Normalize();
            EncryptSecrets(config);
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

        return string.IsNullOrEmpty(profile.ApiKeyProtected) ? null : _protector.Unprotect(profile.ApiKeyProtected);
    }

    public void SetApiKey(ProviderProfile profile, string? apiKey)
    {
        profile.ApiKey = apiKey?.Trim() ?? "";
        profile.ApiKeyProtected = string.IsNullOrEmpty(profile.ApiKey) ? null : _protector.Protect(profile.ApiKey);
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

    private void EncryptSecrets(AppConfig config)
    {
        foreach (var profile in config.ProviderProfiles)
        {
            // 明文存在时重新加密；明文为空但已有加密值时必须保留，否则“保存其它设置”会把凭据清空
            // （ConfigJson.Clone 会丢掉 JsonIgnore 的明文，但保留 ApiKeyProtected）。
            if (!string.IsNullOrEmpty(profile.ApiKey))
            {
                profile.ApiKeyProtected = _protector.Protect(profile.ApiKey);
            }

            if (!string.IsNullOrEmpty(profile.ApiSecret))
            {
                profile.ApiSecretProtected = _protector.Protect(profile.ApiSecret);
            }

            if (!string.IsNullOrEmpty(profile.SecretId))
            {
                profile.SecretIdProtected = _protector.Protect(profile.SecretId);
            }
        }
    }

    private void DecryptSecrets(AppConfig config)
    {
        foreach (var profile in config.ProviderProfiles)
        {
            profile.ApiKey = string.IsNullOrEmpty(profile.ApiKeyProtected) ? "" : _protector.Unprotect(profile.ApiKeyProtected);
            profile.ApiSecret = string.IsNullOrEmpty(profile.ApiSecretProtected) ? "" : _protector.Unprotect(profile.ApiSecretProtected);
            profile.SecretId = string.IsNullOrEmpty(profile.SecretIdProtected) ? "" : _protector.Unprotect(profile.SecretIdProtected);
        }
    }
}
