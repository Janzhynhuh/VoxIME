using VoxIME.Core.Abstractions;
using VoxIME.Core.Asr.Providers;
using VoxIME.Core.Configuration;
using VoxIME.Core.Logging;

namespace VoxIME.Core.Asr;

/// <summary>
/// 可插拔 ASR 适配器注册表：暴露内置厂商目录（含预设模板），并按 <see cref="ProviderProfile.TypeId"/>
/// 创建对应的识别器实例。所有具体适配器都实现了 <see cref="IAsrProvider"/>。
/// </summary>
public sealed class AsrProviderRegistry : IAsrProviderRegistry
{
    private readonly HttpClient _httpClient;
    private readonly IAppLogger _logger;
    private readonly IReadOnlyList<AsrProviderDescriptor> _descriptors;

    /// <summary>
    /// 构造注册表。<paramref name="httpClient"/> 建议由外部以单例方式提供，并将
    /// <see cref="HttpClient.Timeout"/> 设成 <see cref="Timeout.InfiniteTimeSpan"/>
    /// （单次请求超时由 <see cref="ProviderProfile.TimeoutSeconds"/> 控制）。
    /// </summary>
    public AsrProviderRegistry(HttpClient httpClient, IAppLogger logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? NullLogger.Instance;
        _descriptors = AsrProviderCatalog.Descriptors.Select(AsrProviderCatalog.Clone).ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<AsrProviderDescriptor> Descriptors => _descriptors;

    /// <inheritdoc />
    public AsrProviderDescriptor? FindDescriptor(string typeId) =>
        _descriptors.FirstOrDefault(descriptor =>
            string.Equals(descriptor.TypeId, typeId?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 按配置创建识别器。配置不合法（缺 BaseUrl / Model / 密钥 / 命令等）或 TypeId 未知时返回 null，
    /// 并把中文错误原因写入日志；需要拿到具体异常时请用 <see cref="CreateOrThrow"/>。
    /// </summary>
    public IAsrProvider? Create(ProviderProfile profile)
    {
        if (profile is null)
        {
            _logger.Warn("创建 ASR 识别器失败：服务配置为 null。");
            return null;
        }

        try
        {
            return CreateOrThrow(profile);
        }
        catch (AsrException ex)
        {
            _logger.Warn($"创建 ASR 识别器失败（服务“{profile.Name}”，类型 {profile.TypeId}）：{ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            _logger.Error($"创建 ASR 识别器时发生未知错误（服务“{profile.Name}”，类型 {profile.TypeId}）。", ex);
            return null;
        }
    }

    /// <summary>按配置创建识别器；配置不合法或类型未知时抛出带中文说明的 <see cref="AsrException"/>。</summary>
    public IAsrProvider CreateOrThrow(ProviderProfile profile)
    {
        if (profile is null)
        {
            throw new AsrException("服务配置为空（profile 为 null），无法创建识别器。");
        }

        var typeId = profile.TypeId?.Trim() ?? "";
        if (typeId.Length == 0)
        {
            throw new AsrException("服务配置缺少 TypeId（适配器类型），请在设置中重新选择厂商预设。", responseBody: null);
        }

        IAsrProvider provider = typeId switch
        {
            AsrProviderTypeIds.OpenAiTranscription => new OpenAiCompatibleTranscriptionProvider(profile, _httpClient, _logger),
            AsrProviderTypeIds.OpenAiAudioChat => new OpenAiAudioChatProvider(profile, _httpClient, _logger),
            AsrProviderTypeIds.LocalCli => new LocalCliAsrProvider(profile, _httpClient, _logger),
            AsrProviderTypeIds.Tencent => new TencentAsrProvider(profile, _httpClient, _logger),
            AsrProviderTypeIds.Xfyun => new XfyunAsrProvider(profile, _httpClient, _logger),
            AsrProviderTypeIds.Mock => new MockAsrProvider(profile, _httpClient, _logger),
            _ => throw new AsrException(
                $"不支持的 ASR 适配器类型：“{typeId}”。可选类型：{string.Join("、", _descriptors.Select(d => d.TypeId))}。",
                typeId)
        };

        _logger.Info($"ASR 识别器已创建：{profile.Name}（类型 {typeId}，模型 {profile.Model}）。");
        return provider;
    }
}
