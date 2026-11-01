using VoxIME.Core.Configuration;

namespace VoxIME.Core.Abstractions;

/// <summary>内置 ASR 适配器的类型 ID（ProviderProfile.TypeId）。</summary>
public static class AsrProviderTypeIds
{
    /// <summary>OpenAI /audio/transcriptions 多段表单接口（Whisper、SenseVoice、whisper.cpp server、LM Studio 等）。</summary>
    public const string OpenAiTranscription = "openai-transcription";

    /// <summary>OpenAI chat/completions + input_audio（小米 MiMo-ASR、通义 qwen3-asr-flash 等）。</summary>
    public const string OpenAiAudioChat = "openai-audio-chat";

    /// <summary>本地命令行程序（whisper.cpp / FunASR / sherpa-onnx）。</summary>
    public const string LocalCli = "local-cli";

    /// <summary>腾讯云一句话识别（TC3-HMAC-SHA256 签名）。</summary>
    public const string Tencent = "tencent-sentence";

    /// <summary>科大讯飞实时听写（WebSocket IAT）。</summary>
    public const string Xfyun = "xfyun-iat";

    /// <summary>离线 Mock，用于 UI 调试与单元测试。</summary>
    public const string Mock = "mock";
}

public static class AsrCategories
{
    public const string Cloud = "cloud";
    public const string Local = "local";
    public const string Custom = "custom";
}

public sealed class AsrCallOptions
{
    /// <summary>语言代码：zh / en / auto 等，留空用 ProviderProfile.Language。</summary>
    public string? Language { get; init; }

    /// <summary>提示词（Whisper prompt 或识别上下文）。</summary>
    public string? Prompt { get; init; }

    /// <summary>厂商特定参数的 JSON 对象字符串，会合并进请求体（可空）。</summary>
    public string? ExtraJson { get; init; }
}

public sealed record AsrResult(
    string Text,
    string ProviderId,
    TimeSpan Latency,
    TimeSpan AudioDuration,
    string? DetectedLanguage = null,
    string? RequestId = null);

public sealed class AsrException : Exception
{
    public AsrException(
        string message,
        string? providerId = null,
        int? statusCode = null,
        string? responseBody = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ProviderId = providerId;
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public string? ProviderId { get; }
    public int? StatusCode { get; }
    public string? ResponseBody { get; }
}

/// <summary>一个 ProviderProfile 对应的识别器实例。</summary>
public interface IAsrProvider
{
    AsrProviderDescriptor Descriptor { get; }
    Task<AsrResult> TranscribeAsync(AudioClip clip, AsrCallOptions options, CancellationToken cancellationToken = default);
}

/// <summary>适配器注册表：暴露可接入的厂商目录（含预设），并按配置创建实例。</summary>
public interface IAsrProviderRegistry
{
    /// <summary>所有已支持的适配器（UI "新增服务" 列表）。</summary>
    IReadOnlyList<AsrProviderDescriptor> Descriptors { get; }

    AsrProviderDescriptor? FindDescriptor(string typeId);

    /// <summary>按配置创建识别器；配置不合法或类型未知时返回 null。</summary>
    IAsrProvider? Create(ProviderProfile profile);

    /// <summary>按配置创建识别器；失败时抛出 AsrException。</summary>
    IAsrProvider CreateOrThrow(ProviderProfile profile);
}

/// <summary>适配器元数据 + 该适配器的厂商预设。</summary>
public sealed class AsrProviderDescriptor
{
    public required string TypeId { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public string Category { get; init; } = AsrCategories.Cloud;
    public bool RequiresApiKey { get; init; }
    public bool RequiresBaseUrl { get; init; } = true;
    public string ApiKeyLabel { get; init; } = "API Key";
    public string? DocsUrl { get; init; }
    public IReadOnlyList<AsrProviderPreset> Presets { get; init; } = Array.Empty<AsrProviderPreset>();
}

/// <summary>厂商/部署预设：填充一份配置模板（Id/ApiKey 由 UI 重新生成）。</summary>
public sealed record AsrProviderPreset(string Name, string Description, ProviderProfile Template);
