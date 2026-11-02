using System.Text.Json;
using VoxIME.Core.Configuration;
using Xunit;

namespace VoxIME.Core.Tests.Configuration;

public sealed class ConfigExchangeServiceTests
{
    private static AppConfig Sample()
    {
        var config = AppConfig.CreateDefault();
        config.ProviderProfiles[0].Name = "MiMo";
        config.ProviderProfiles[0].BaseUrl = "https://api.example.com/v1";
        config.ProviderProfiles[0].ApiKey = "sk-secret-123";
        config.ProviderProfiles[0].ApiSecret = "secret-456";
        config.Hotkeys.PushToTalk = "F2";
        config.Audio.AppContextMode = AppContextMode.AppOnly;
        return config;
    }

    [Fact]
    public void ExportIncludeSecrets_ThenImport_RoundTrips()
    {
        var json = ConfigExchangeService.Export(Sample(), includeSecrets: true);

        Assert.Contains("sk-secret-123", json);
        var imported = ConfigExchangeService.Import(json);

        Assert.Single(imported.ProviderProfiles);
        Assert.Equal("MiMo", imported.ProviderProfiles[0].Name);
        Assert.Equal("sk-secret-123", imported.ProviderProfiles[0].ApiKey);
        Assert.Equal("secret-456", imported.ProviderProfiles[0].ApiSecret);
        Assert.Equal("F2", imported.Hotkeys.PushToTalk);
        Assert.Equal(AppContextMode.AppOnly, imported.Audio.AppContextMode);
    }

    [Fact]
    public void ExportWithoutSecrets_StillImportsOtherSettings()
    {
        var json = ConfigExchangeService.Export(Sample(), includeSecrets: false);

        Assert.DoesNotContain("sk-secret-123", json);
        Assert.DoesNotContain("secret-456", json);
        var imported = ConfigExchangeService.Import(json);
        Assert.Equal("MiMo", imported.ProviderProfiles[0].Name);
        Assert.Equal("", imported.ProviderProfiles[0].ApiKey);
        Assert.Equal("", imported.ProviderProfiles[0].ApiSecret);
    }

    [Fact]
    public void Import_AcceptLegacyRawConfigJson()
    {
        var raw = JsonSerializer.Serialize(Sample(), ConfigJson.Options);

        var imported = ConfigExchangeService.Import(raw);

        Assert.Equal("sk-secret-123", imported.ProviderProfiles[0].ApiKey);
    }

    [Fact]
    public void Import_InvalidJson_ThrowsChineseError()
    {
        var ex = Assert.Throws<ConfigExchangeException>(() => ConfigExchangeService.Import("{bad json"));

        Assert.Contains("JSON", ex.Message);
    }

    [Fact]
    public void Import_UnknownFormatOrFutureVersion_IsRejected()
    {
        var wrongFormat = "{\"format\":\"other\",\"version\":1,\"config\":{}}";
        Assert.Throws<ConfigExchangeException>(() => ConfigExchangeService.Import(wrongFormat));

        var future = "{\"format\":\"voxime-config\",\"version\":99,\"config\":{}}";
        var ex = Assert.Throws<ConfigExchangeException>(() => ConfigExchangeService.Import(future));
        Assert.Contains("版本", ex.Message);
    }
}
