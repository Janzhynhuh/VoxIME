using System.Text.Json;
using VoxIME.Core.Configuration;
using VoxIME.Core.Logging;
using VoxIME.Core.Tests.Integration;

namespace VoxIME.Core.Tests.Configuration;

public sealed class ConfigStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;

    public ConfigStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "voxime-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "config.json");
    }

    [Fact]
    public void SaveAndLoad_RoundTripsConfig()
    {
        var store = new JsonConfigStore(_path, new NullSecretProtector());
        var config = AppConfig.CreateDefault();
        config.ActiveProviderProfileId = "mock";
        config.ProviderProfiles[0].Name = "本地 Mock";
        config.ProviderProfiles[0].ApiKey = "sk-test-123456";
        config.Hotkeys.PushToTalk = "F2";
        config.Live2D.Width = 420;
        config.ProviderProfiles[0].BaseUrl = "https://example.com/v1";

        store.Save(config);
        var loaded = new JsonConfigStore(_path, new NullSecretProtector()).Load();

        Assert.Equal("F2", loaded.Hotkeys.PushToTalk);
        Assert.Equal(420, loaded.Live2D.Width);
        Assert.Equal("本地 Mock", loaded.ProviderProfiles[0].Name);
        Assert.Equal("https://example.com/v1", loaded.ProviderProfiles[0].BaseUrl);
        Assert.Equal("sk-test-123456", loaded.ProviderProfiles[0].ApiKey);
    }

    [Fact]
    public void Save_EncryptsApiKey_WithDpapi()
    {
        var store = new JsonConfigStore(_path, new DpapiSecretProtector());
        var config = AppConfig.CreateDefault();
        config.ProviderProfiles[0].ApiKey = "sk-plaintext-secret";

        store.Save(config);

        var json = File.ReadAllText(_path);
        Assert.DoesNotContain("sk-plaintext-secret", json);
        Assert.Contains("apiKeyProtected", json);

        var loaded = new JsonConfigStore(_path, new DpapiSecretProtector()).Load();
        Assert.Equal("sk-plaintext-secret", loaded.ProviderProfiles[0].ApiKey);
    }

    [Fact]
    public void Load_MissingFile_CreatesDefaults()
    {
        var store = new JsonConfigStore(_path, new NullSecretProtector());

        var config = store.Load();

        Assert.True(File.Exists(_path));
        Assert.NotEmpty(config.ProviderProfiles);
        Assert.Equal("mock", config.ProviderProfiles[0].TypeId);
    }

    [Fact]
    public void Load_CorruptFile_FallsBackToDefaults_AndBacksUp()
    {
        File.WriteAllText(_path, "{ this is not valid json ");
        var logger = new TestLogger();
        var store = new JsonConfigStore(_path, new NullSecretProtector(), logger);

        var config = store.Load();

        Assert.NotEmpty(config.ProviderProfiles);
        Assert.True(File.Exists(_path + ".bad.json"));
        Assert.Contains(logger.Messages, m => m.StartsWith("Warn"));
    }

    [Fact]
    public void Save_RaisesConfigChanged()
    {
        var store = new JsonConfigStore(_path, new NullSecretProtector());
        AppConfig? raised = null;
        store.ConfigChanged += (_, config) => raised = config;

        store.Save(AppConfig.CreateDefault());

        Assert.NotNull(raised);
    }

    [Fact]
    public void ResetToDefaults_RestoresMockProfile()
    {
        var store = new JsonConfigStore(_path, new NullSecretProtector());
        var config = AppConfig.CreateDefault();
        config.ProviderProfiles.Clear();
        store.Save(config);

        store.ResetToDefaults();

        Assert.Single(store.Current.ProviderProfiles);
        Assert.Equal("mock", store.Current.ProviderProfiles[0].TypeId);
    }

    [Fact]
    public void DpapiSecretProtector_RoundTrips()
    {
        var protector = new DpapiSecretProtector();
        var cipher = protector.Protect("hello-秘密");

        Assert.NotEqual("hello-秘密", cipher);
        Assert.Equal("hello-秘密", protector.Unprotect(cipher));
        Assert.Equal("", protector.Protect(""));
        Assert.Equal("", protector.Unprotect("garbage"));
    }

    [Fact]
    public void ConfigJson_EnumSerialization_IsReadable()
    {
        var config = AppConfig.CreateDefault();
        config.Hotkeys.DefaultMode = RecordingMode.Toggle;

        var json = JsonSerializer.Serialize(config, ConfigJson.Options);
        var roundTrip = JsonSerializer.Deserialize<AppConfig>(json, ConfigJson.Options)!;

        Assert.Equal(RecordingMode.Toggle, roundTrip.Hotkeys.DefaultMode);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 忽略临时目录清理失败。
        }
    }
}
