using System.Collections.ObjectModel;
using VoxIME.App.Infrastructure;
using VoxIME.Core.Abstractions;
using VoxIME.Core.Configuration;

namespace VoxIME.App.ViewModels;

/// <summary>设置窗口左侧导航页面。</summary>
public enum SettingsPage
{
    Providers,
    Audio,
    Insertion,
    Live2D,
    Tts,
    General,
    Appearance
}

/// <summary>通用下拉选项（显示文案 + 配置值）。</summary>
public sealed class Choice
{
    public Choice(string label, string value)
    {
        Label = label;
        Value = value;
    }

    public string Label { get; }
    public string Value { get; }

    public override string ToString() => Label;
}

/// <summary>应用上下文级别的下拉选项（显示文案 + 枚举值）。</summary>
public sealed class AppContextOption
{
    public AppContextOption(string label, AppContextMode mode)
    {
        Label = label;
        Mode = mode;
    }

    public string Label { get; }
    public AppContextMode Mode { get; }

    public override string ToString() => Label;
}

/// <summary>术语替换表项。</summary>
public sealed class ReplacementItemViewModel : ObservableObject
{
    private string _from = "";
    private string _to = "";

    public ReplacementItemViewModel(string from, string to)
    {
        _from = from;
        _to = to;
    }

    public string From
    {
        get => _from;
        set => SetProperty(ref _from, value);
    }

    public string To
    {
        get => _to;
        set => SetProperty(ref _to, value);
    }
}

/// <summary>
/// 设置页单个 ASR 服务（ProviderProfile）的可编辑包装：
/// 提供变更通知与按适配器类型显隐的字段标志。
/// </summary>
public sealed class ProviderItemViewModel : ObservableObject
{
    private readonly AsrProviderDescriptor? _descriptor;
    private string _name = "";
    private string _baseUrl = "";
    private string _model = "";
    private string _language = "zh";
    private string _apiKey = "";
    private string _apiSecret = "";
    private string _secretId = "";
    private string _appId = "";
    private string _prompt = "";
    private string _extraJson = "";
    private string _command = "";
    private string _commandArguments = "";
    private string _outputMode = "stdout";
    private string _outputFileTemplate = "";
    private int _timeoutSeconds = 60;
    private bool _enabled = true;
    private bool _isDefault;

    public ProviderItemViewModel(ProviderProfile profile, IAsrProviderRegistry? registry)
    {
        Id = profile.Id;
        TypeId = profile.TypeId;
        _descriptor = registry?.FindDescriptor(profile.TypeId);
        _name = profile.Name;
        _baseUrl = profile.BaseUrl;
        _model = profile.Model;
        _language = string.IsNullOrWhiteSpace(profile.Language) ? "zh" : profile.Language;
        _apiKey = profile.ApiKey;
        _apiSecret = profile.ApiSecret;
        _secretId = profile.SecretId;
        _appId = profile.AppId ?? "";
        _prompt = profile.Prompt ?? "";
        _extraJson = profile.ExtraJson ?? "";
        _command = profile.Command ?? "";
        _commandArguments = profile.CommandArguments ?? "";
        _outputMode = string.IsNullOrWhiteSpace(profile.OutputMode) ? "stdout" : profile.OutputMode;
        _outputFileTemplate = profile.OutputFileTemplate ?? "";
        _timeoutSeconds = profile.TimeoutSeconds <= 0 ? 60 : profile.TimeoutSeconds;
        _enabled = profile.Enabled;
    }

    /// <summary>配置 ID（新建后不可改）。</summary>
    public string Id { get; }

    public string TypeId { get; }

    public AsrProviderDescriptor? Descriptor => _descriptor;

    public string TypeDisplayName => _descriptor?.DisplayName ?? (string.IsNullOrWhiteSpace(TypeId) ? "未知类型" : TypeId);

    public string CategoryLabel => _descriptor?.Category switch
    {
        AsrCategories.Cloud => "云端",
        AsrCategories.Local => "本地",
        AsrCategories.Custom => "自定义",
        _ => "服务"
    };

    public string ApiKeyLabel => _descriptor?.ApiKeyLabel ?? "API Key";

    // ---- 按类型显隐的字段（新增适配器类型时自动生效） ----
    public bool UsesBaseUrl => _descriptor?.RequiresBaseUrl ?? !IsLocalCli;
    public bool UsesModel => !IsLocalCli;
    public bool UsesPrompt => !IsLocalCli;
    public bool UsesApiKey => IsMock ? false : (_descriptor?.RequiresApiKey ?? (!IsLocalCli && !IsMock));
    public bool UsesApiSecret => TypeId == AsrProviderTypeIds.Xfyun;
    public bool UsesSecretId => TypeId == AsrProviderTypeIds.Tencent;
    public bool UsesAppId => TypeId is AsrProviderTypeIds.Tencent or AsrProviderTypeIds.Xfyun;
    public bool IsMock => TypeId == AsrProviderTypeIds.Mock;
    public bool IsLocalCli => TypeId == AsrProviderTypeIds.LocalCli;
    public bool UsesCommand => IsLocalCli;
    public bool UsesAnyCredential => UsesApiKey || UsesApiSecret || UsesSecretId || UsesAppId;
    public bool UsesAnyConnection => UsesBaseUrl || UsesModel || UsesPrompt;

    public IReadOnlyList<string> OutputModes { get; } = new[] { "stdout", "file", "json" };

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string BaseUrl
    {
        get => _baseUrl;
        set => SetProperty(ref _baseUrl, value);
    }

    public string Model
    {
        get => _model;
        set => SetProperty(ref _model, value);
    }

    public string Language
    {
        get => _language;
        set => SetProperty(ref _language, value);
    }

    public string ApiKey
    {
        get => _apiKey;
        set => SetProperty(ref _apiKey, value);
    }

    public string ApiSecret
    {
        get => _apiSecret;
        set => SetProperty(ref _apiSecret, value);
    }

    public string SecretId
    {
        get => _secretId;
        set => SetProperty(ref _secretId, value);
    }

    public string AppId
    {
        get => _appId;
        set => SetProperty(ref _appId, value);
    }

    public string Prompt
    {
        get => _prompt;
        set => SetProperty(ref _prompt, value);
    }

    public string ExtraJson
    {
        get => _extraJson;
        set => SetProperty(ref _extraJson, value);
    }

    public string Command
    {
        get => _command;
        set => SetProperty(ref _command, value);
    }

    public string CommandArguments
    {
        get => _commandArguments;
        set => SetProperty(ref _commandArguments, value);
    }

    public string OutputMode
    {
        get => _outputMode;
        set => SetProperty(ref _outputMode, value);
    }

    public string OutputFileTemplate
    {
        get => _outputFileTemplate;
        set => SetProperty(ref _outputFileTemplate, value);
    }

    public int TimeoutSeconds
    {
        get => _timeoutSeconds;
        set
        {
            var clamped = Math.Clamp(value, 5, 600);
            if (SetProperty(ref _timeoutSeconds, clamped))
            {
                OnPropertyChanged(nameof(TimeoutLabel));
            }
        }
    }

    public string TimeoutLabel => $"{_timeoutSeconds} 秒";

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
            {
                OnPropertyChanged(nameof(StatusLabel));
            }
        }
    }

    /// <summary>是否为当前默认服务（由 SettingsViewModel 统一维护）。</summary>
    public bool IsDefault
    {
        get => _isDefault;
        set
        {
            if (SetProperty(ref _isDefault, value))
            {
                OnPropertyChanged(nameof(StatusLabel));
            }
        }
    }

    public string StatusLabel => IsDefault ? "默认" : Enabled ? "已启用" : "已停用";

    public string Summary
    {
        get
        {
            if (IsLocalCli)
            {
                return string.IsNullOrWhiteSpace(_command) ? "本地命令行识别" : _command;
            }

            if (IsMock)
            {
                return "离线演示，无需网络";
            }

            var model = string.IsNullOrWhiteSpace(_model) ? "未设置模型" : _model;
            var host = TryGetHost(_baseUrl);
            return string.IsNullOrEmpty(host) ? model : $"{host} · {model}";
        }
    }

    /// <summary>转换为配置文件对象（敏感字段保留明文，由 JsonConfigStore 负责加密落盘）。</summary>
    public ProviderProfile ToProfile() => new()
    {
        Id = Id,
        Name = string.IsNullOrWhiteSpace(Name) ? TypeDisplayName : Name.Trim(),
        TypeId = TypeId,
        BaseUrl = BaseUrl?.Trim() ?? "",
        Model = Model?.Trim() ?? "",
        Language = string.IsNullOrWhiteSpace(Language) ? "zh" : Language.Trim(),
        ApiKey = ApiKey ?? "",
        ApiSecret = ApiSecret ?? "",
        SecretId = SecretId ?? "",
        AppId = string.IsNullOrWhiteSpace(AppId) ? null : AppId.Trim(),
        Prompt = string.IsNullOrWhiteSpace(Prompt) ? null : Prompt,
        ExtraJson = string.IsNullOrWhiteSpace(ExtraJson) ? null : ExtraJson.Trim(),
        Command = string.IsNullOrWhiteSpace(Command) ? null : Command.Trim(),
        CommandArguments = string.IsNullOrWhiteSpace(CommandArguments) ? null : CommandArguments.Trim(),
        OutputMode = string.IsNullOrWhiteSpace(OutputMode) ? "stdout" : OutputMode.Trim(),
        OutputFileTemplate = string.IsNullOrWhiteSpace(OutputFileTemplate) ? null : OutputFileTemplate.Trim(),
        TimeoutSeconds = TimeoutSeconds,
        Enabled = Enabled
    };

    private static string TryGetHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "";
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url.Trim();
    }
}
