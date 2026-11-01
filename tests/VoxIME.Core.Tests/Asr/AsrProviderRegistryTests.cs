using VoxIME.Core.Abstractions;
using VoxIME.Core.Asr;
using VoxIME.Core.Asr.Providers;
using VoxIME.Core.Configuration;
using VoxIME.Core.Logging;
using Xunit;

namespace VoxIME.Core.Tests.Asr;

public sealed class AsrProviderRegistryTests
{
    private static AsrProviderRegistry CreateRegistry(FakeAppLogger? logger = null)
    {
        var handler = new FakeHttpMessageHandler(_ =>
            throw new InvalidOperationException("注册表测试不应发起网络请求。"));
        return new AsrProviderRegistry(AsrTestData.HttpClient(handler), logger ?? new FakeAppLogger());
    }

    [Fact]
    public void Descriptors_覆盖全部厂商与预设()
    {
        var registry = CreateRegistry();

        Assert.Equal(6, registry.Descriptors.Count);
        var presetNames = registry.Descriptors.SelectMany(d => d.Presets).Select(p => p.Name).ToList();
        Assert.Contains(presetNames, name => name.Contains("OpenAI Whisper"));
        Assert.Contains(presetNames, name => name.Contains("硅基流动") && name.Contains("SenseVoice"));
        Assert.Contains(presetNames, name => name.Contains("LM Studio"));
        Assert.Contains(presetNames, name => name.Contains("MiMo"));
        Assert.Contains(presetNames, name => name.Contains("DashScope"));
        Assert.Contains(presetNames, name => name.Contains("腾讯云"));
        Assert.Contains(presetNames, name => name.Contains("讯飞"));
        Assert.Contains(presetNames, name => name.Contains("whisper.cpp"));
        Assert.Contains(presetNames, name => name.Contains("FunASR"));
        Assert.Contains(presetNames, name => name.Contains("sherpa-onnx"));
        Assert.Contains(presetNames, name => name.Contains("Mock"));
    }

    [Fact]
    public void Descriptors_预设模板与类型一致且字段完整()
    {
        var registry = CreateRegistry();

        foreach (var descriptor in registry.Descriptors)
        {
            Assert.NotEmpty(descriptor.DisplayName);
            Assert.NotEmpty(descriptor.Description);
            Assert.NotEmpty(descriptor.Presets);
            foreach (var preset in descriptor.Presets)
            {
                Assert.Equal(descriptor.TypeId, preset.Template.TypeId);
                Assert.False(string.IsNullOrWhiteSpace(preset.Template.Name));
                Assert.False(string.IsNullOrWhiteSpace(preset.Template.Language));
                Assert.True(preset.Template.TimeoutSeconds >= 5);
                Assert.NotEqual("", preset.Name);
            }
        }

        var localCli = registry.FindDescriptor(AsrProviderTypeIds.LocalCli)!;
        Assert.Equal(3, localCli.Presets.Count);
        Assert.All(localCli.Presets, preset =>
        {
            Assert.False(string.IsNullOrWhiteSpace(preset.Template.Command));
            Assert.False(string.IsNullOrWhiteSpace(preset.Template.CommandArguments));
            Assert.Contains(preset.Template.OutputMode, new[] { "stdout", "file", "json" });
        });

        var transcription = registry.FindDescriptor(AsrProviderTypeIds.OpenAiTranscription)!;
        Assert.All(transcription.Presets, preset =>
        {
            Assert.False(string.IsNullOrWhiteSpace(preset.Template.BaseUrl));
            Assert.False(string.IsNullOrWhiteSpace(preset.Template.Model));
        });

        var audioChat = registry.FindDescriptor(AsrProviderTypeIds.OpenAiAudioChat)!;
        Assert.Contains(audioChat.Presets, preset => preset.Template.BaseUrl.Contains("xiaomimimo.com"));
        Assert.Contains(audioChat.Presets, preset => preset.Template.Model == "qwen3-asr-flash");
        Assert.Contains(audioChat.Presets, preset => preset.Template.ExtraJson?.Contains("data-url") == true);
    }

    [Fact]
    public void FindDescriptor_按类型查找并忽略大小写()
    {
        var registry = CreateRegistry();

        Assert.NotNull(registry.FindDescriptor(AsrProviderTypeIds.OpenAiTranscription));
        Assert.NotNull(registry.FindDescriptor(AsrProviderTypeIds.OpenAiAudioChat));
        Assert.NotNull(registry.FindDescriptor(AsrProviderTypeIds.LocalCli));
        Assert.NotNull(registry.FindDescriptor(AsrProviderTypeIds.Tencent));
        Assert.NotNull(registry.FindDescriptor(AsrProviderTypeIds.Xfyun));
        Assert.NotNull(registry.FindDescriptor(AsrProviderTypeIds.Mock));
        Assert.Equal(AsrProviderTypeIds.Mock, registry.FindDescriptor("MOCK")!.TypeId);
        Assert.Null(registry.FindDescriptor("not-exist"));
        Assert.Null(registry.FindDescriptor(""));
    }

    [Fact]
    public void Descriptors_返回深拷贝不污染其他注册表()
    {
        var first = CreateRegistry();
        var preset = first.Descriptors[0].Presets[0];
        preset.Template.BaseUrl = "https://mutated.example.com";
        preset.Template.Model = "mutated-model";

        var second = CreateRegistry();
        var freshPreset = second.Descriptors[0].Presets[0];
        Assert.NotEqual("https://mutated.example.com", freshPreset.Template.BaseUrl);
        Assert.NotEqual("mutated-model", freshPreset.Template.Model);
    }

    [Fact]
    public void CreateOrThrow_未知类型_抛出中文异常()
    {
        var registry = CreateRegistry();
        var profile = new ProviderProfile { Name = "未知", TypeId = "not-exist", BaseUrl = "http://x", Model = "m" };

        var exception = Assert.Throws<AsrException>(() => registry.CreateOrThrow(profile));
        Assert.Contains("不支持", exception.Message);
        Assert.Contains("not-exist", exception.Message);
    }

    [Fact]
    public void Create_配置缺失_返回null并记录警告日志()
    {
        var logger = new FakeAppLogger();
        var registry = CreateRegistry(logger);
        var profile = new ProviderProfile
        {
            Name = "缺 BaseUrl",
            TypeId = AsrProviderTypeIds.OpenAiTranscription,
            BaseUrl = "",
            Model = "whisper-1"
        };

        Assert.Null(registry.Create(profile));
        Assert.True(logger.HasLevel(LogLevel.Warn));
    }

    [Fact]
    public void CreateOrThrow_缺少模型_抛出中文异常()
    {
        var registry = CreateRegistry();
        var profile = new ProviderProfile
        {
            Name = "缺模型",
            TypeId = AsrProviderTypeIds.OpenAiTranscription,
            BaseUrl = "https://api.openai.com/v1",
            Model = ""
        };

        var exception = Assert.Throws<AsrException>(() => registry.CreateOrThrow(profile));
        Assert.Contains("Model", exception.Message);
    }

    [Fact]
    public void 所有可离线的预设模板_均可创建识别器()
    {
        var registry = CreateRegistry();
        var creatableTypes = new[]
        {
            AsrProviderTypeIds.OpenAiTranscription,
            AsrProviderTypeIds.OpenAiAudioChat,
            AsrProviderTypeIds.LocalCli,
            AsrProviderTypeIds.Mock
        };

        var created = 0;
        foreach (var descriptor in registry.Descriptors.Where(d => creatableTypes.Contains(d.TypeId)))
        {
            foreach (var preset in descriptor.Presets)
            {
                var provider = registry.CreateOrThrow(preset.Template);
                Assert.Equal(descriptor.TypeId, provider.Descriptor.TypeId);
                created++;
            }
        }

        Assert.True(created >= 8, $"应至少创建 8 个预设识别器，实际 {created}。");
    }

    [Fact]
    public void 腾讯云预设缺少密钥时_Create返回null且CreateOrThrow报中文原因()
    {
        var registry = CreateRegistry();
        var template = registry.FindDescriptor(AsrProviderTypeIds.Tencent)!.Presets[0].Template;
        template.SecretId = "";
        template.ApiKey = "";

        Assert.Null(registry.Create(template));
        var exception = Assert.Throws<AsrException>(() => registry.CreateOrThrow(template));
        Assert.Contains("SecretId", exception.Message);
    }

    [Fact]
    public void 讯飞预设缺少密钥时_CreateOrThrow报AppId()
    {
        var registry = CreateRegistry();
        var template = registry.FindDescriptor(AsrProviderTypeIds.Xfyun)!.Presets[0].Template;
        template.AppId = "";
        template.ApiKey = "";
        template.ApiSecret = "";

        var exception = Assert.Throws<AsrException>(() => registry.CreateOrThrow(template));
        Assert.Contains("AppId", exception.Message);
    }

    [Fact]
    public async Task 通过注册表创建的Mock识别器可离线识别()
    {
        var registry = CreateRegistry();
        var provider = registry.CreateOrThrow(AsrTestData.MockProfile("{\"text\":\"来自注册表的 Mock\"}"));
        Assert.IsType<MockAsrProvider>(provider);

        var mock = (MockAsrProvider)provider;
        mock.MinDelay = TimeSpan.Zero;
        mock.MaxDelay = TimeSpan.Zero;

        var result = await mock.TranscribeAsync(AsrTestData.Silence(1), new AsrCallOptions());
        Assert.Equal("来自注册表的 Mock", result.Text);
    }
}
