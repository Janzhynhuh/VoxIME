using VoxIME.Core.Abstractions;
using VoxIME.Core.Configuration;

namespace VoxIME.Core.Asr;

/// <summary>
/// 内置 ASR 适配器目录：所有厂商预设（AsrProviderPreset）集中在这里定义。
/// Registry 与各 Provider 都从这里取元数据，保证 UI 列表与实际创建逻辑一致。
/// </summary>
internal static class AsrProviderCatalog
{
    private static readonly Lazy<IReadOnlyList<AsrProviderDescriptor>> LazyDescriptors = new(BuildDescriptors);

    private static readonly Lazy<IReadOnlyDictionary<string, AsrProviderDescriptor>> LazyByTypeId = new(() =>
        LazyDescriptors.Value.ToDictionary(descriptor => descriptor.TypeId, StringComparer.OrdinalIgnoreCase));

    /// <summary>全部适配器描述（含预设）。调用方不要修改其中的模板；Registry 会深拷贝。</summary>
    public static IReadOnlyList<AsrProviderDescriptor> Descriptors => LazyDescriptors.Value;

    /// <summary>按 TypeId 查找适配器描述。</summary>
    public static AsrProviderDescriptor? GetDescriptor(string? typeId)
    {
        if (string.IsNullOrWhiteSpace(typeId))
        {
            return null;
        }

        return LazyByTypeId.Value.TryGetValue(typeId.Trim(), out var descriptor) ? descriptor : null;
    }

    /// <summary>深拷贝描述对象，避免 UI 修改预设模板影响其他注册表实例。</summary>
    public static AsrProviderDescriptor Clone(AsrProviderDescriptor source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new AsrProviderDescriptor
        {
            TypeId = source.TypeId,
            DisplayName = source.DisplayName,
            Description = source.Description,
            Category = source.Category,
            RequiresApiKey = source.RequiresApiKey,
            RequiresBaseUrl = source.RequiresBaseUrl,
            ApiKeyLabel = source.ApiKeyLabel,
            DocsUrl = source.DocsUrl,
            Presets = source.Presets
                .Select(preset => preset with { Template = preset.Template.Clone() })
                .ToList()
        };
    }

    private static IReadOnlyList<AsrProviderDescriptor> BuildDescriptors() => new List<AsrProviderDescriptor>
    {
        BuildOpenAiTranscriptionDescriptor(),
        BuildOpenAiAudioChatDescriptor(),
        BuildLocalCliDescriptor(),
        BuildTencentDescriptor(),
        BuildXfyunDescriptor(),
        BuildMockDescriptor()
    };

    // ------------------------------------------------------------ OpenAI 兼容转录

    private static AsrProviderDescriptor BuildOpenAiTranscriptionDescriptor() => new()
    {
        TypeId = AsrProviderTypeIds.OpenAiTranscription,
        DisplayName = "OpenAI 兼容转录接口",
        Description = "POST {BaseUrl}/audio/transcriptions 多段表单，适用于 OpenAI Whisper、硅基流动 SenseVoice、LM Studio / whisper.cpp server 等兼容实现。",
        Category = AsrCategories.Cloud,
        RequiresApiKey = false,
        RequiresBaseUrl = true,
        ApiKeyLabel = "API Key（本地服务可留空）",
        DocsUrl = "https://platform.openai.com/docs/api-reference/audio/createTranscription",
        Presets = new List<AsrProviderPreset>
        {
            new(
                "OpenAI Whisper API",
                "OpenAI 官方 Whisper（whisper-1）。按分钟计费，需要海外网络与 API Key。",
                CreateCloudTemplate(
                    name: "OpenAI Whisper",
                    typeId: AsrProviderTypeIds.OpenAiTranscription,
                    baseUrl: "https://api.openai.com/v1",
                    model: "whisper-1",
                    language: "zh",
                    extraJson: null,
                    timeoutSeconds: 120)),
            new(
                "硅基流动 SiliconFlow（SenseVoiceSmall）",
                "国内可直连的 OpenAI 兼容转录接口，FunAudioLLM/SenseVoiceSmall 支持中英等多语种，按音频时长计费。",
                CreateCloudTemplate(
                    name: "硅基流动 SenseVoice",
                    typeId: AsrProviderTypeIds.OpenAiTranscription,
                    baseUrl: "https://api.siliconflow.cn/v1",
                    model: "FunAudioLLM/SenseVoiceSmall",
                    language: "zh",
                    extraJson: null,
                    timeoutSeconds: 120)),
            new(
                "LM Studio / 本地 OpenAI 兼容服务",
                "本地模型（LM Studio、llama.cpp server、whisper.cpp server 等）通常无需 API Key；使用 http://127.0.0.1:1234/v1。",
                CreateCloudTemplate(
                    name: "LM Studio 本地",
                    typeId: AsrProviderTypeIds.OpenAiTranscription,
                    baseUrl: "http://127.0.0.1:1234/v1",
                    model: "whisper-1",
                    language: "auto",
                    extraJson: null,
                    timeoutSeconds: 300))
        }
    };

    // ------------------------------------------------------------ OpenAI 音频对话

    private static AsrProviderDescriptor BuildOpenAiAudioChatDescriptor() => new()
    {
        TypeId = AsrProviderTypeIds.OpenAiAudioChat,
        DisplayName = "OpenAI 音频对话接口（input_audio）",
        Description = "POST {BaseUrl}/chat/completions，以 input_audio 内容块提交音频，适用于小米 MiMo ASR、通义千问 qwen3-asr-flash 等。",
        Category = AsrCategories.Cloud,
        RequiresApiKey = true,
        RequiresBaseUrl = true,
        ApiKeyLabel = "API Key",
        DocsUrl = "https://platform.xiaomimimo.com/docs/en-US/usage-guide/Speech-Recognition",
        Presets = new List<AsrProviderPreset>
        {
            new(
                "小米 MiMo ASR",
                "小米 MiMo-V2.5-ASR，OpenAI 兼容 chat/completions，音频以 base64（或 data URL）传入；默认 Bearer 鉴权。",
                CreateCloudTemplate(
                    name: "小米 MiMo ASR",
                    typeId: AsrProviderTypeIds.OpenAiAudioChat,
                    baseUrl: "https://api.xiaomimimo.com/v1",
                    model: "mimo-v2.5-asr",
                    language: "zh",
                    extraJson: null,
                    timeoutSeconds: 120)),
            new(
                "阿里云 DashScope（qwen3-asr-flash）",
                "百炼 OpenAI 兼容模式，qwen3-asr-flash 需要把音频编码成 data:audio/wav;base64 的 Data URI（预设已配置 audioDataFormat=data-url）。",
                CreateCloudTemplate(
                    name: "DashScope qwen3-asr-flash",
                    typeId: AsrProviderTypeIds.OpenAiAudioChat,
                    baseUrl: "https://dashscope.aliyuncs.com/compatible-mode/v1",
                    model: "qwen3-asr-flash",
                    language: "zh",
                    extraJson: "{\"audioDataFormat\":\"data-url\"}",
                    timeoutSeconds: 120))
        }
    };

    // ------------------------------------------------------------ 本地 CLI

    private static AsrProviderDescriptor BuildLocalCliDescriptor() => new()
    {
        TypeId = AsrProviderTypeIds.LocalCli,
        DisplayName = "本地命令行识别器",
        Description = "调用本机可执行程序完成识别，支持 stdout / 输出文件 / JSON 路径三种取值方式，适用于 whisper.cpp、FunASR、sherpa-onnx 等。",
        Category = AsrCategories.Local,
        RequiresApiKey = false,
        RequiresBaseUrl = false,
        ApiKeyLabel = "API Key（不需要）",
        DocsUrl = "https://github.com/ggml-org/whisper.cpp",
        Presets = new List<AsrProviderPreset>
        {
            new(
                "whisper.cpp（本地离线）",
                "使用 whisper-cli 识别 16k 单声道 WAV，-otxt 输出到文件。请把 Model 改成你的 ggml 模型路径，Command 改成实际可执行文件路径。",
                CreateCliTemplate(
                    name: "whisper.cpp 本地",
                    command: "whisper-cli",
                    arguments: "-m {model} -f {wav} -l {lang} -nt -otxt -of {out}",
                    outputMode: "file",
                    outputFileTemplate: "{out}.txt",
                    model: "models/ggml-base.bin",
                    language: "zh",
                    timeoutSeconds: 300)),
            new(
                "FunASR CLI（本地离线）",
                "FunASR 命令行识别（Paraformer 中文）。参数模板随 FunASR 版本可能变化，请按本机实际版本核对；默认从 stdout 取识别结果。",
                CreateCliTemplate(
                    name: "FunASR 本地",
                    command: "funasr",
                    arguments: "++model={model} ++wav={wav} ++output_dir={out}",
                    outputMode: "stdout",
                    outputFileTemplate: null,
                    model: "paraformer-zh",
                    language: "zh",
                    timeoutSeconds: 300)),
            new(
                "sherpa-onnx-offline（本地离线）",
                "sherpa-onnx 离线 Paraformer 模型识别，结果从 stdout 获取。请按本机模型目录调整 Model 与参数模板。",
                CreateCliTemplate(
                    name: "sherpa-onnx 本地",
                    command: "sherpa-onnx-offline",
                    arguments: "--tokens={model}/tokens.txt --paraformer={model}/model.onnx --wav={wav}",
                    outputMode: "stdout",
                    outputFileTemplate: null,
                    model: "models/sherpa-onnx-paraformer-zh",
                    language: "zh",
                    timeoutSeconds: 300))
        }
    };

    // ------------------------------------------------------------ 腾讯云

    private static AsrProviderDescriptor BuildTencentDescriptor() => new()
    {
        TypeId = AsrProviderTypeIds.Tencent,
        DisplayName = "腾讯云一句话识别",
        Description = "腾讯云 SentenceRecognition（TC3-HMAC-SHA256 签名，≤60 秒短音频）。需要 SecretId + SecretKey（SecretKey 填在 API Key 字段）。",
        Category = AsrCategories.Cloud,
        RequiresApiKey = true,
        RequiresBaseUrl = true,
        ApiKeyLabel = "SecretKey",
        DocsUrl = "https://cloud.tencent.com/document/api/1093/35646",
        Presets = new List<AsrProviderPreset>
        {
            new(
                "腾讯云一句话识别",
                "默认地域 ap-guangzhou、引擎 16k_zh；可用 ExtraJson 覆盖 region / EngSerViceType / FilterDirty 等参数。",
                CreateCloudTemplate(
                    name: "腾讯云一句话识别",
                    typeId: AsrProviderTypeIds.Tencent,
                    baseUrl: "https://asr.tencentcloudapi.com",
                    model: "",
                    language: "zh",
                    extraJson: "{\"region\":\"ap-guangzhou\"}",
                    timeoutSeconds: 60))
        }
    };

    // ------------------------------------------------------------ 讯飞

    private static AsrProviderDescriptor BuildXfyunDescriptor() => new()
    {
        TypeId = AsrProviderTypeIds.Xfyun,
        DisplayName = "科大讯飞语音听写（流式）",
        Description = "讯飞 IAT WebSocket 实时听写（wss://iat-api.xfyun.cn/v2/iat），需要 APPID + APIKey + APISecret；音频分帧 1280 字节。",
        Category = AsrCategories.Cloud,
        RequiresApiKey = true,
        RequiresBaseUrl = true,
        ApiKeyLabel = "APIKey",
        DocsUrl = "https://www.xfyun.cn/doc/asr/voicedictation/API.html",
        Presets = new List<AsrProviderPreset>
        {
            new(
                "科大讯飞语音听写（流式版）",
                "默认中文普通话（zh_cn / iat / mandarin），ExtraJson 可覆盖 business 参数（例如 {\"eos\":3000,\"ptt\":1}）。",
                CreateCloudTemplate(
                    name: "讯飞语音听写",
                    typeId: AsrProviderTypeIds.Xfyun,
                    baseUrl: "wss://iat-api.xfyun.cn/v2/iat",
                    model: "",
                    language: "zh",
                    extraJson: null,
                    timeoutSeconds: 60))
        }
    };

    // ------------------------------------------------------------ Mock

    private static AsrProviderDescriptor BuildMockDescriptor() => new()
    {
        TypeId = AsrProviderTypeIds.Mock,
        DisplayName = "Mock 离线演示",
        Description = "不联网、不调用任何 API，延迟 300-800ms 返回可预测中文文本，用于 UI 调试、演示与单元测试。",
        Category = AsrCategories.Local,
        RequiresApiKey = false,
        RequiresBaseUrl = false,
        ApiKeyLabel = "API Key（不需要）",
        DocsUrl = null,
        Presets = new List<AsrProviderPreset>
        {
            new(
                "Mock 离线演示",
                "返回“你好，这是 VoxIME Mock 识别结果，音频 x.x 秒”，可用 ExtraJson {\"text\":\"自定义文本\"} 覆盖。",
                new ProviderProfile
                {
                    Id = "",
                    Name = "Mock 离线演示",
                    TypeId = AsrProviderTypeIds.Mock,
                    BaseUrl = "",
                    Model = "mock",
                    Language = "zh",
                    TimeoutSeconds = 30,
                    Enabled = true
                })
        }
    };

    // ------------------------------------------------------------ 模板工厂

    private static ProviderProfile CreateCloudTemplate(
        string name,
        string typeId,
        string baseUrl,
        string model,
        string language,
        string? extraJson,
        int timeoutSeconds) => new()
    {
        Id = "",
        Name = name,
        TypeId = typeId,
        BaseUrl = baseUrl,
        Model = model,
        Language = language,
        ExtraJson = extraJson,
        TimeoutSeconds = timeoutSeconds,
        Enabled = true
    };

    private static ProviderProfile CreateCliTemplate(
        string name,
        string command,
        string arguments,
        string outputMode,
        string? outputFileTemplate,
        string model,
        string language,
        int timeoutSeconds) => new()
    {
        Id = "",
        Name = name,
        TypeId = AsrProviderTypeIds.LocalCli,
        BaseUrl = "",
        Model = model,
        Language = language,
        TimeoutSeconds = timeoutSeconds,
        Enabled = true,
        Command = command,
        CommandArguments = arguments,
        OutputMode = outputMode,
        OutputFileTemplate = outputFileTemplate
    };
}
