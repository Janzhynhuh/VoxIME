using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using VoxIME.App.Infrastructure;
using VoxIME.Core;
using VoxIME.Core.Abstractions;
using VoxIME.Core.Appearance;
using VoxIME.Core.Configuration;
using VoxIME.Core.History;
using VoxIME.Core.Logging;
using VoxIME.Core.Services;

namespace VoxIME.App.ViewModels;

/// <summary>
/// 设置窗口 ViewModel：持有配置草稿（克隆），保存时收集为 AppConfig → configStore.Save → controller.ApplyConfig。
/// UI 只依赖 Core 的接口与配置模型，不直接引用具体实现类。
/// </summary>
public sealed class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly IConfigStore _configStore;
    private readonly IVoiceInputController _controller;
    private readonly IAsrProviderRegistry? _registry;
    private readonly IHistoryStore _historyStore;
    private readonly IAppLogger _logger;

    private AppConfig _working;
    private bool _disposed;
    private SettingsPage _currentPage;
    private int _selectedPageIndex;
    private string _statusText = "";
    private string _saveStatusText = "";
    private int _saveStatusVersion;

    // ---- 服务 ----
    private ProviderItemViewModel? _selectedProvider;
    private AsrProviderDescriptor? _newDescriptor;
    private AsrProviderPreset? _newPreset;
    private string _defaultProviderId = "";
    private string _testResultText = "";
    private bool _hasTestResult;
    private bool _isTestSuccess;
    private bool _isTesting;

    // ---- 录音 ----
    private AudioDeviceInfo? _selectedAudioDevice;
    private string _audioDeviceHint = "";
    private string? _pushToTalkError;
    private string? _toggleHotkeyError;
    private string? _cancelHotkeyError;
    private bool _enablePushToTalk;
    private bool _enableToggle;
    private RecordingMode _recordingMode;
    private int _maxDurationSeconds;
    private int _silenceTimeoutMs;
    private int _minSpeechMs;
    private double _vadThreshold = 0.003;
    private bool _autoStopOnSilence;
    private bool _playCues;
    private bool _isMicTesting;
    private double _micTestLevel;
    private double _micTestMaxLevel;
    private string _micTestStatus = "";
    private AppContextMode _appContextMode;
    private bool _exportSecrets = true;

    public ObservableCollection<AppContextOption> AppContextOptions { get; } = new()
    {
        new("关闭（不读取、不发送）", AppContextMode.Off),
        new("仅应用类型（推荐）", AppContextMode.AppOnly),
        new("应用 + 窗口标题（识别更准，标题会发送给 ASR 服务）", AppContextMode.AppAndWindowTitle)
    };

    // ---- 上屏 ----
    private InsertionMode _insertionMode;
    private bool _autoInsert;
    private bool _previewBeforeInsert;
    private bool _restoreClipboard;
    private bool _trimWhitespace;
    private bool _appendSpace;
    private bool _pressEnterAfterInsert;
    private int _pasteDelayMs;
    private bool _showHud;
    private bool _showLiveTranscript;
    private bool _overlayAutoInsert;
    private int _overlayAutoHideMs;

    // ---- 桌宠 ----
    private bool _live2DEnabled;
    private bool _clickThrough;
    private bool _alwaysOnTop;
    private bool _eyeFollow;
    private int _eyeFollowFps = 30;
    private bool _eyeFollowInvertY;
    private bool _idleMotion;
    private bool _mouthSync;
    private bool _showBubble;
    private string _modelDirectory = "";
    private double _live2DWidth;
    private double _live2DHeight;
    private bool _autoFitWindow = true;
    private bool _snapToEdge = true;
    private bool _keepAboveTaskbar = true;
    private double _live2DScale;
    private double _modelOffsetX;
    private double _modelOffsetY;
    private double _live2DOpacity;
    private double _mouthSensitivity;
    private string _idleMotionGroup = "Idle";
    private string _listeningMotionGroup = "";
    private string _thinkingMotionGroup = "";
    private string _speakingMotionGroup = "";
    private string _errorMotionGroup = "";
    private string _motionPreviewStatus = "";
    private string _expressionPreviewStatus = "";
    private string _listeningExpression = "";
    private string _thinkingExpression = "";
    private string _speakingExpression = "";
    private string _errorExpression = "";
    private string _fallbackAvatarTheme = "violet";
    private string _live2DStatusText = "";
    private string _expressionCatalogHint = "";
    private string _expressionMapModelDirectory = "";
    private Live2DModelInfo? _selectedLive2DModel;
    private string _modelLibraryDirectory = AppInfo.DefaultModelDirectory;

    // ---- 朗读 ----
    private bool _ttsEnabled;
    private bool _autoSpeakResult;
    private bool _ttsMouthSync;
    private string? _selectedTtsVoice;
    private int _ttsRate;
    private int _ttsVolume;
    private string _ttsStatusText = "";

    // ---- 通用 ----
    private bool _startWithWindows;
    private bool _minimizeToTray;
    private bool _showTrayIcon;
    private bool _showWelcomeOnStartup;
    private int _historyLimit;
    private Choice? _selectedLogLevel;

    // ---- 外观（主题 + 配色） ----
    private string _selectedThemeMode = ThemeModes.Dark;
    private string _selectedColorSchemeId = ThemeCatalog.DefaultColorSchemeId;
    private bool _appearanceInitializing;
    private bool _appearanceApplying;

    // ---- 背景（自定义图片/纯色/渐变）与动效档位 ----
    private string _backgroundMode = BackgroundConfig.ModeTheme;
    private string? _backgroundImagePath;
    private double _backgroundImageOpacity = 0.92;
    private double _backgroundBlurRadius = 22;
    private double _backgroundDim = 0.42;
    private string _backgroundFit = "cover";
    private string? _backgroundSolidColor;
    private string? _backgroundGradientStart;
    private string? _backgroundGradientEnd;
    private double _backgroundSurfaceOpacity = 0.82;
    private bool _backgroundInvertText;
    private string _selectedMotionMode = MotionModes.Expressive;
    private DispatcherTimer? _backgroundPreviewTimer;

    public SettingsViewModel(
        IConfigStore configStore,
        IVoiceInputController controller,
        IAsrProviderRegistry? registry,
        IHistoryStore historyStore,
        IAppLogger logger)
    {
        _configStore = configStore;
        _controller = controller;
        _registry = registry;
        _historyStore = historyStore;
        _logger = logger;
        _working = ConfigJson.CloneAppConfig(configStore.Current);

        Descriptors = new ObservableCollection<AsrProviderDescriptor>(
            registry?.Descriptors ?? Array.Empty<AsrProviderDescriptor>());
        LogLevels = new ObservableCollection<Choice>
        {
            new("详细信息（调试）", "trace"),
            new("调试", "debug"),
            new("信息（推荐）", "info"),
            new("警告", "warn"),
            new("仅错误", "error")
        };

        SaveCommand = new RelayCommand(Save);
        ResetCommand = new RelayCommand(ResetToDefaults);
        OpenConfigFolderCommand = new RelayCommand(() => WindowHelper.OpenFolder(AppInfo.AppDataDirectory));
        OpenLogsFolderCommand = new RelayCommand(() => WindowHelper.OpenFolder(AppInfo.DefaultLogDirectory));
        OpenHistoryCommand = new RelayCommand(() => HistoryRequested?.Invoke(this, EventArgs.Empty));
        OpenAboutCommand = new RelayCommand(() => AboutRequested?.Invoke(this, EventArgs.Empty));
        ExportConfigCommand = new RelayCommand(ExportConfig);
        ImportConfigCommand = new RelayCommand(ImportConfig);

        AddProviderCommand = new RelayCommand(AddProvider, () => NewDescriptor is not null);
        RemoveProviderCommand = new RelayCommand(RemoveProvider,
            () => SelectedProvider is not null && Providers.Count > 1);
        SetDefaultProviderCommand = new RelayCommand(SetDefaultProvider, () => SelectedProvider is not null);
        TestProviderCommand = new AsyncRelayCommand(TestProviderAsync, () => SelectedProvider is not null && !IsTesting);
        OpenProviderDocsCommand = new RelayCommand(OpenProviderDocs,
            () => !string.IsNullOrWhiteSpace(SelectedProvider?.Descriptor?.DocsUrl));

        TestMicCommand = new AsyncRelayCommand(TestMicrophoneAsync, () => !IsMicTesting);
        BrowseModelLibraryDirectoryCommand = new RelayCommand(BrowseModelLibraryDirectory);
        ImportLive2DModelCommand = new RelayCommand(ImportLive2DModel);
        ImportLive2DModelZipCommand = new RelayCommand(ImportLive2DModelZip);
        DeleteLive2DModelCommand = new RelayCommand(DeleteLive2DModel, CanDeleteLive2DModel);
        RefreshLive2DModelsCommand = new RelayCommand(RefreshLive2DModelLibrary);
        OpenLive2DModelsFolderCommand = new RelayCommand(OpenLive2DModelsFolder);
        ShowLive2DCommand = new RelayCommand(() => SetLive2DVisible(true));
        HideLive2DCommand = new RelayCommand(() => SetLive2DVisible(false));
        ResetLive2DPositionCommand = new RelayCommand(ResetLive2DPosition);
        PreviewIdleMotionCommand = new RelayCommand(PreviewIdleMotion);
        PreviewStateMotionCommand = new RelayCommand(param => PreviewStateMotion(param as string));
        PreviewExpressionCommand = new RelayCommand(param => PreviewExpression(param as string));
        AddPushToTalkHotkeyCommand = new RelayCommand(() => AddHotkeyRow(PushToTalkHotkeys));
        RemovePushToTalkHotkeyCommand = new RelayCommand(param => RemoveHotkeyRow(PushToTalkHotkeys, param as HotkeyBindingViewModel));
        AddToggleHotkeyCommand = new RelayCommand(() => AddHotkeyRow(ToggleHotkeys));
        RemoveToggleHotkeyCommand = new RelayCommand(param => RemoveHotkeyRow(ToggleHotkeys, param as HotkeyBindingViewModel));
        AddCancelHotkeyCommand = new RelayCommand(() => AddHotkeyRow(CancelHotkeys));
        RemoveCancelHotkeyCommand = new RelayCommand(param => RemoveHotkeyRow(CancelHotkeys, param as HotkeyBindingViewModel));
        TestTtsCommand = new AsyncRelayCommand(TestTtsAsync, () => !_ttsEnabledTesting);
        AddReplacementCommand = new RelayCommand(() => Replacements.Add(new ReplacementItemViewModel("", "")));
        RemoveReplacementCommand = new RelayCommand(param =>
        {
            if (param is ReplacementItemViewModel item)
            {
                Replacements.Remove(item);
            }
        });
        ResetOverlayPositionCommand = new RelayCommand(() =>
        {
            OverlayPositionResetRequested?.Invoke(this, EventArgs.Empty);
            StatusText = "已重置悬浮窗位置，将在下次显示时回到屏幕底部居中";
        });
        SelectColorSchemeCommand = new RelayCommand(param => SelectColorScheme(param as ColorSchemeOption));
        PickBackgroundImageCommand = new RelayCommand(PickBackgroundImage);
        ClearBackgroundImageCommand = new RelayCommand(ClearBackgroundImage);
        ResetBackgroundCommand = new RelayCommand(ResetBackground);

        InitializeFrom(_working);
        _controller.MicrophoneLevelChanged += OnMicrophoneLevelChanged;
        ThemeManager.ThemeChanged += OnThemeManagerChanged;
        SelectedPageIndex = 0;
    }

    // ==================== 事件 ====================

    /// <summary>配置已保存（AppHost 用于刷新 HUD / 托盘可见性等）。</summary>
    public event EventHandler<AppConfig>? Saved;

    /// <summary>请求打开历史窗口。</summary>
    public event EventHandler? HistoryRequested;

    /// <summary>请求打开关于窗口。</summary>
    public event EventHandler? AboutRequested;

    /// <summary>请求重置 HUD 位置（由 AppHost 交给 HudWindow 处理）。</summary>
    public event EventHandler? OverlayPositionResetRequested;

    // ==================== 导航 ====================

    public SettingsPage CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (SetProperty(ref _currentPage, value))
            {
                OnPropertiesChanged(nameof(PageTitle), nameof(PageSubtitle));
            }
        }
    }

    public int SelectedPageIndex
    {
        get => _selectedPageIndex;
        set
        {
            if (value < 0)
            {
                return;
            }

            if (SetProperty(ref _selectedPageIndex, value))
            {
                CurrentPage = (SettingsPage)Math.Clamp(value, 0, (int)SettingsPage.Appearance);
            }
        }
    }

    public string PageTitle => CurrentPage switch
    {
        SettingsPage.Providers => "服务",
        SettingsPage.Audio => "录音",
        SettingsPage.Insertion => "上屏",
        SettingsPage.Live2D => "桌宠",
        SettingsPage.Tts => "朗读",
        SettingsPage.Appearance => "外观",
        _ => "通用"
    };

    public string PageSubtitle => CurrentPage switch
    {
        SettingsPage.Providers => "配置识别服务：云端 API、本地命令行或离线演示。",
        SettingsPage.Audio => "选择麦克风与热键，调整录音、静音检测参数。",
        SettingsPage.Insertion => "识别结果如何送入当前应用，以及悬浮提示窗的行为。",
        SettingsPage.Live2D => "桌宠模型、尺寸、透明度、点击穿透与动作联动。",
        SettingsPage.Tts => "使用 Windows 内置语音合成朗读识别结果。",
        SettingsPage.Appearance => "明暗主题与配色方案，选择后立即预览；保存后下次启动保持。",
        _ => "启动方式、数据目录、历史与日志、版本与许可。"
    };

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (SetProperty(ref _statusText, value))
            {
                OnPropertyChanged(nameof(HasStatus));
            }
        }
    }

    public bool HasStatus => !string.IsNullOrEmpty(_statusText);

    /// <summary>保存按钮旁的短暂反馈（几秒后清空），不弹托盘通知。</summary>
    public string SaveStatusText
    {
        get => _saveStatusText;
        private set
        {
            if (SetProperty(ref _saveStatusText, value))
            {
                OnPropertyChanged(nameof(HasSaveStatus));
            }
        }
    }

    public bool HasSaveStatus => !string.IsNullOrEmpty(_saveStatusText);

    private void ShowSaveStatus(string text)
    {
        SaveStatusText = text;
        var version = ++_saveStatusVersion;
        _ = HideSaveStatusLaterAsync(version);
    }

    private async Task HideSaveStatusLaterAsync(int version)
    {
        await Task.Delay(3500).ConfigureAwait(true);
        if (!_disposed && version == _saveStatusVersion)
        {
            SaveStatusText = "";
        }
    }

    public string VersionText => $"{AppInfo.GetVersionLabel()}";

    public string ConfigFilePath => _configStore.ConfigFilePath;

    public string DataDirectory => AppInfo.AppDataDirectory;

    public string LogDirectory => AppInfo.DefaultLogDirectory;

    public string HistoryPath => _historyStore.FilePath;

    private static bool LooksLikeVirtualMicrophone(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var value = name.ToLowerInvariant();
        return value.Contains("虚拟", StringComparison.Ordinal)
               || value.Contains("virtual", StringComparison.Ordinal)
               || value.Contains("remote", StringComparison.Ordinal)
               || value.Contains("远程", StringComparison.Ordinal)
               || value.Contains("uu", StringComparison.Ordinal)
               || value.Contains("vb-audio", StringComparison.Ordinal)
               || value.Contains("cable", StringComparison.Ordinal)
               || value.Contains("loopback", StringComparison.Ordinal);
    }

    /// <summary>重新枚举麦克风与语音包（每次打开设置窗口时调用）。</summary>
    public void RefreshDevices()    {
        try
        {
            var selectedDeviceId = SelectedAudioDevice?.Id;
            AudioDevices.Clear();
            AudioDevices.Add(new AudioDeviceInfo { Id = "", Name = "系统默认设备", IsDefault = true });
            foreach (var device in _controller.GetInputDevices())
            {
                AudioDevices.Add(device);
            }

            SelectedAudioDevice = AudioDevices.FirstOrDefault(d =>
                                      string.Equals(d.Id, selectedDeviceId, StringComparison.Ordinal))
                                  ?? AudioDevices[0];

            var defaultDevice = AudioDevices.Skip(1).FirstOrDefault(d => d.IsDefault);
            if (defaultDevice is null)
            {
                AudioDeviceHint = "";
            }
            else if (LooksLikeVirtualMicrophone(defaultDevice.Name))
            {
                AudioDeviceHint = $"当前 Windows 默认输入：{defaultDevice.Name}（看起来是虚拟/远程音频设备，可能采集不到本机说话声；请选择真实麦克风并点击保存）";
            }
            else
            {
                AudioDeviceHint = $"当前 Windows 默认输入：{defaultDevice.Name}";
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"刷新录音设备失败：{ex.Message}");
        }

        try
        {
            var selectedVoice = SelectedTtsVoice;
            TtsVoices.Clear();
            foreach (var voice in _controller.GetTtsVoices())
            {
                TtsVoices.Add(voice);
            }

            SelectedTtsVoice = !string.IsNullOrEmpty(selectedVoice) && TtsVoices.Contains(selectedVoice)
                ? selectedVoice
                : TtsVoices.FirstOrDefault();
            OnPropertyChanged(nameof(IsTtsAvailable));
            TtsStatusText = TtsVoices.Count == 0
                ? "未检测到 Windows 语音包，朗读功能不可用。"
                : $"已检测到 {TtsVoices.Count} 个语音包。";
        }
        catch (Exception ex)
        {
            _logger.Warn($"刷新语音包失败：{ex.Message}");
        }

        RefreshLive2DStatus();
    }

    // ==================== 通用命令 ====================

    public RelayCommand SaveCommand { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand OpenConfigFolderCommand { get; }
    public RelayCommand OpenLogsFolderCommand { get; }
    public RelayCommand OpenHistoryCommand { get; }
    public RelayCommand OpenAboutCommand { get; }
    public RelayCommand ExportConfigCommand { get; }
    public RelayCommand ImportConfigCommand { get; }

    // ==================== 服务页 ====================

    public ObservableCollection<ProviderItemViewModel> Providers { get; } = new();

    public ObservableCollection<AsrProviderDescriptor> Descriptors { get; }

    public ObservableCollection<AsrProviderPreset> NewPresets { get; } = new();

    public ObservableCollection<Choice> LogLevels { get; }

    public ProviderItemViewModel? SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (SetProperty(ref _selectedProvider, value))
            {
                OnPropertiesChanged(nameof(HasSelectedProvider), nameof(SelectedProviderIsDefault));
                RaiseProviderCommands();
            }
        }
    }

    public bool HasSelectedProvider => _selectedProvider is not null;

    public bool SelectedProviderIsDefault => _selectedProvider is not null && _selectedProvider.IsDefault;

    public AsrProviderDescriptor? NewDescriptor
    {
        get => _newDescriptor;
        set
        {
            if (SetProperty(ref _newDescriptor, value))
            {
                NewPresets.Clear();
                if (value is not null)
                {
                    foreach (var preset in value.Presets)
                    {
                        NewPresets.Add(preset);
                    }
                }

                NewPreset = NewPresets.FirstOrDefault();
                AddProviderCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public AsrProviderPreset? NewPreset
    {
        get => _newPreset;
        set => SetProperty(ref _newPreset, value);
    }

    public bool HasDescriptorCatalog => Descriptors.Count > 0;

    public string DescriptorCatalogHint => HasDescriptorCatalog
        ? $"共 {Descriptors.Count} 种适配器"
        : "未能加载服务目录（请检查 Core 注册表实现）";

    public string TestResultText
    {
        get => _testResultText;
        private set => SetProperty(ref _testResultText, value);
    }

    public bool HasTestResult
    {
        get => _hasTestResult;
        private set => SetProperty(ref _hasTestResult, value);
    }

    public bool IsTestSuccess
    {
        get => _isTestSuccess;
        private set => SetProperty(ref _isTestSuccess, value);
    }

    public bool IsTesting
    {
        get => _isTesting;
        private set
        {
            if (SetProperty(ref _isTesting, value))
            {
                TestProviderCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public RelayCommand AddProviderCommand { get; }
    public RelayCommand RemoveProviderCommand { get; }
    public RelayCommand SetDefaultProviderCommand { get; }
    public AsyncRelayCommand TestProviderCommand { get; }
    public RelayCommand OpenProviderDocsCommand { get; }

    // ==================== 录音页 ====================

    public ObservableCollection<AudioDeviceInfo> AudioDevices { get; } = new();

    public AudioDeviceInfo? SelectedAudioDevice
    {
        get => _selectedAudioDevice;
        set => SetProperty(ref _selectedAudioDevice, value);
    }

    /// <summary>当前 Windows 默认输入设备说明；虚拟/远程设备时提示可能采集不到人声。</summary>
    public string AudioDeviceHint
    {
        get => _audioDeviceHint;
        private set => SetProperty(ref _audioDeviceHint, value);
    }

    public ObservableCollection<HotkeyBindingViewModel> PushToTalkHotkeys { get; } = new();
    public ObservableCollection<HotkeyBindingViewModel> ToggleHotkeys { get; } = new();
    public ObservableCollection<HotkeyBindingViewModel> CancelHotkeys { get; } = new();

    public RelayCommand AddPushToTalkHotkeyCommand { get; }
    public RelayCommand RemovePushToTalkHotkeyCommand { get; }
    public RelayCommand AddToggleHotkeyCommand { get; }
    public RelayCommand RemoveToggleHotkeyCommand { get; }
    public RelayCommand AddCancelHotkeyCommand { get; }
    public RelayCommand RemoveCancelHotkeyCommand { get; }

    public string? PushToTalkError
    {
        get => _pushToTalkError;
        private set
        {
            if (SetProperty(ref _pushToTalkError, value))
            {
                OnPropertyChanged(nameof(HasPushToTalkError));
            }
        }
    }

    public string? ToggleHotkeyError
    {
        get => _toggleHotkeyError;
        private set
        {
            if (SetProperty(ref _toggleHotkeyError, value))
            {
                OnPropertyChanged(nameof(HasToggleHotkeyError));
            }
        }
    }

    public string? CancelHotkeyError
    {
        get => _cancelHotkeyError;
        private set
        {
            if (SetProperty(ref _cancelHotkeyError, value))
            {
                OnPropertyChanged(nameof(HasCancelHotkeyError));
            }
        }
    }

    public bool HasPushToTalkError => !string.IsNullOrEmpty(_pushToTalkError);
    public bool HasToggleHotkeyError => !string.IsNullOrEmpty(_toggleHotkeyError);
    public bool HasCancelHotkeyError => !string.IsNullOrEmpty(_cancelHotkeyError);

    public bool EnablePushToTalk
    {
        get => _enablePushToTalk;
        set
        {
            if (SetProperty(ref _enablePushToTalk, value))
            {
                ValidateHotkeys();
            }
        }
    }

    public bool EnableToggle
    {
        get => _enableToggle;
        set
        {
            if (SetProperty(ref _enableToggle, value))
            {
                ValidateHotkeys();
            }
        }
    }

    public RecordingMode RecordingMode
    {
        get => _recordingMode;
        set
        {
            if (SetProperty(ref _recordingMode, value))
            {
                // 选择“按一下开始/结束”时必须启用切换热键，否则模式无法使用。
                if (value == RecordingMode.Toggle)
                {
                    EnableToggle = true;
                }
                else
                {
                    EnablePushToTalk = true;
                }

                OnPropertiesChanged(nameof(IsPushToTalkMode), nameof(IsToggleMode));
                ValidateHotkeys();
            }
        }
    }

    public bool IsPushToTalkMode
    {
        get => _recordingMode == RecordingMode.PushToTalk;
        set
        {
            if (value)
            {
                RecordingMode = RecordingMode.PushToTalk;
            }
        }
    }

    public bool IsToggleMode
    {
        get => _recordingMode == RecordingMode.Toggle;
        set
        {
            if (value)
            {
                RecordingMode = RecordingMode.Toggle;
            }
        }
    }

    public int MaxDurationSeconds
    {
        get => _maxDurationSeconds;
        set => SetProperty(ref _maxDurationSeconds, Math.Clamp(value, 5, 600));
    }

    public int SilenceTimeoutMs
    {
        get => _silenceTimeoutMs;
        set => SetProperty(ref _silenceTimeoutMs, Math.Clamp(value, 200, 5000));
    }

    public int MinSpeechMs
    {
        get => _minSpeechMs;
        set => SetProperty(ref _minSpeechMs, Math.Clamp(value, 100, 3000));
    }

    public double VadThreshold
    {
        get => _vadThreshold;
        set => SetProperty(ref _vadThreshold, Math.Clamp(value, 0.001, 0.2));
    }

    public bool AutoStopOnSilence
    {
        get => _autoStopOnSilence;
        set => SetProperty(ref _autoStopOnSilence, value);
    }

    public bool PlayCues
    {
        get => _playCues;
        set => SetProperty(ref _playCues, value);
    }

    public AppContextMode AppContextMode
    {
        get => _appContextMode;
        set => SetProperty(ref _appContextMode, value);
    }

    /// <summary>导出配置时是否包含 API Key 明文。</summary>
    public bool ExportSecrets
    {
        get => _exportSecrets;
        set => SetProperty(ref _exportSecrets, value);
    }

    public bool IsMicTesting
    {
        get => _isMicTesting;
        private set
        {
            if (SetProperty(ref _isMicTesting, value))
            {
                TestMicCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public double MicTestLevel
    {
        get => _micTestLevel;
        set => SetProperty(ref _micTestLevel, Math.Clamp(value, 0, 1));
    }

    public string MicTestStatus
    {
        get => _micTestStatus;
        private set => SetProperty(ref _micTestStatus, value);
    }

    public AsyncRelayCommand TestMicCommand { get; }

    // ==================== 上屏页 ====================

    public InsertionMode InsertionMode
    {
        get => _insertionMode;
        set
        {
            if (SetProperty(ref _insertionMode, value))
            {
                OnPropertiesChanged(nameof(IsClipboardPasteMode), nameof(IsSimulateTypingMode));
            }
        }
    }

    public bool IsClipboardPasteMode
    {
        get => _insertionMode == InsertionMode.ClipboardPaste;
        set
        {
            if (value)
            {
                InsertionMode = InsertionMode.ClipboardPaste;
            }
        }
    }

    public bool IsSimulateTypingMode
    {
        get => _insertionMode == InsertionMode.SimulateTyping;
        set
        {
            if (value)
            {
                InsertionMode = InsertionMode.SimulateTyping;
            }
        }
    }

    public bool AutoInsert
    {
        get => _autoInsert;
        set => SetProperty(ref _autoInsert, value);
    }

    public bool PreviewBeforeInsert
    {
        get => _previewBeforeInsert;
        set => SetProperty(ref _previewBeforeInsert, value);
    }

    public bool RestoreClipboard
    {
        get => _restoreClipboard;
        set => SetProperty(ref _restoreClipboard, value);
    }

    public bool TrimWhitespace
    {
        get => _trimWhitespace;
        set => SetProperty(ref _trimWhitespace, value);
    }

    public bool AppendSpace
    {
        get => _appendSpace;
        set => SetProperty(ref _appendSpace, value);
    }

    public bool PressEnterAfterInsert
    {
        get => _pressEnterAfterInsert;
        set => SetProperty(ref _pressEnterAfterInsert, value);
    }

    public int PasteDelayMs
    {
        get => _pasteDelayMs;
        set => SetProperty(ref _pasteDelayMs, Math.Clamp(value, 0, 2000));
    }

    public ObservableCollection<ReplacementItemViewModel> Replacements { get; } = new();

    public bool ShowHud
    {
        get => _showHud;
        set => SetProperty(ref _showHud, value);
    }

    public bool ShowLiveTranscript
    {
        get => _showLiveTranscript;
        set => SetProperty(ref _showLiveTranscript, value);
    }

    public bool OverlayAutoInsert
    {
        get => _overlayAutoInsert;
        set => SetProperty(ref _overlayAutoInsert, value);
    }

    public int OverlayAutoHideMs
    {
        get => _overlayAutoHideMs;
        set => SetProperty(ref _overlayAutoHideMs, Math.Clamp(value, 500, 30000));
    }

    public RelayCommand AddReplacementCommand { get; }
    public RelayCommand RemoveReplacementCommand { get; }
    public RelayCommand ResetOverlayPositionCommand { get; }

    // ==================== 桌宠页 ====================

    public bool Live2DEnabled
    {
        get => _live2DEnabled;
        set => SetProperty(ref _live2DEnabled, value);
    }

    public string ModelDirectory
    {
        get => _modelDirectory;
        set => SetProperty(ref _modelDirectory, value);
    }

    public double Live2DWidth
    {
        get => _live2DWidth;
        set => SetProperty(ref _live2DWidth, Math.Clamp(value, 160, 1600));
    }

    public double Live2DHeight
    {
        get => _live2DHeight;
        set => SetProperty(ref _live2DHeight, Math.Clamp(value, 160, 1600));
    }

    /// <summary>宽度随模型自动收紧（高度不变，左右各留约 5px padding）；开启时宽度滑块置灰。</summary>
    public bool AutoFitWindow
    {
        get => _autoFitWindow;
        set
        {
            if (SetProperty(ref _autoFitWindow, value))
            {
                OnPropertyChanged(nameof(IsManualWindowWidthEnabled));
            }
        }
    }

    /// <summary>自动收紧宽度时，宽度滑块不可手动调整。</summary>
    public bool IsManualWindowWidthEnabled => !AutoFitWindow;

    /// <summary>启动/切换模型时自动吸附到屏幕右下角。</summary>
    public bool SnapToEdge
    {
        get => _snapToEdge;
        set => SetProperty(ref _snapToEdge, value);
    }

    /// <summary>贴边时窗口下边缘保持在任务栏上方的工作区内。</summary>
    public bool KeepAboveTaskbar
    {
        get => _keepAboveTaskbar;
        set => SetProperty(ref _keepAboveTaskbar, value);
    }

    public double Live2DScale
    {
        get => _live2DScale;
        set => SetProperty(ref _live2DScale, Math.Clamp(value, 0.2, 3.0));
    }

    /// <summary>模型水平偏移（-1..1，按窗口宽度比例；正=向右）。</summary>
    public double ModelOffsetX
    {
        get => _modelOffsetX;
        set => SetProperty(ref _modelOffsetX, Math.Clamp(value, -1.0, 1.0));
    }

    /// <summary>模型垂直偏移（-1..1，按窗口高度比例；正=向下）。</summary>
    public double ModelOffsetY
    {
        get => _modelOffsetY;
        set => SetProperty(ref _modelOffsetY, Math.Clamp(value, -1.0, 1.0));
    }

    public double Live2DOpacity
    {
        get => _live2DOpacity;
        set => SetProperty(ref _live2DOpacity, Math.Clamp(value, 0.2, 1.0));
    }

    public double MouthSensitivity
    {
        get => _mouthSensitivity;
        set => SetProperty(ref _mouthSensitivity, Math.Clamp(value, 0.1, 5.0));
    }

    public bool ClickThrough
    {
        get => _clickThrough;
        set => SetProperty(ref _clickThrough, value);
    }

    public bool AlwaysOnTop
    {
        get => _alwaysOnTop;
        set => SetProperty(ref _alwaysOnTop, value);
    }

    public bool EyeFollow
    {
        get => _eyeFollow;
        set => SetProperty(ref _eyeFollow, value);
    }

    /// <summary>眼动跟随采样帧率（Hz，5–300）。</summary>
    public int EyeFollowFps
    {
        get => _eyeFollowFps;
        set => SetProperty(ref _eyeFollowFps, Math.Clamp(value, 5, 300));
    }

    /// <summary>眼动上下反转（按当前模型记忆；部分模型骨骼绑定方向相反）。</summary>
    public bool EyeFollowInvertY
    {
        get => _eyeFollowInvertY;
        set
        {
            if (!SetProperty(ref _eyeFollowInvertY, value))
            {
                return;
            }

            var directory = (ModelDirectory ?? "").Trim();
            if (directory.Length > 0)
            {
                _working.Live2D.EyeFollowInvertYByModel[directory] = value;
            }
        }
    }

    public bool IdleMotion
    {
        get => _idleMotion;
        set => SetProperty(ref _idleMotion, value);
    }

    public bool TtsMouthSync
    {
        get => _ttsMouthSync;
        set => SetProperty(ref _ttsMouthSync, value);
    }

    public bool ShowBubble
    {
        get => _showBubble;
        set => SetProperty(ref _showBubble, value);
    }

    public string IdleMotionGroup
    {
        get => _idleMotionGroup;
        set => SetProperty(ref _idleMotionGroup, value);
    }

    public string ListeningMotionGroup
    {
        get => _listeningMotionGroup;
        set => SetProperty(ref _listeningMotionGroup, value);
    }

    public string ThinkingMotionGroup
    {
        get => _thinkingMotionGroup;
        set => SetProperty(ref _thinkingMotionGroup, value);
    }

    public string SpeakingMotionGroup
    {
        get => _speakingMotionGroup;
        set => SetProperty(ref _speakingMotionGroup, value);
    }

    public string ErrorMotionGroup
    {
        get => _errorMotionGroup;
        set => SetProperty(ref _errorMotionGroup, value);
    }

    /// <summary>当前模型可用的动作组名（来自 model3.json + Animation 文件夹）。</summary>
    public ObservableCollection<string> AvailableMotionGroups { get; } = new();

    /// <summary>状态动作下拉选项：自动 / 不播放 / 具体动作组。</summary>
    public ObservableCollection<Choice> StateMotionChoices { get; } = new();

    public string MotionPreviewStatus
    {
        get => _motionPreviewStatus;
        private set => SetProperty(ref _motionPreviewStatus, value);
    }

    public string ExpressionPreviewStatus
    {
        get => _expressionPreviewStatus;
        private set => SetProperty(ref _expressionPreviewStatus, value);
    }

    public string ListeningExpression
    {
        get => _listeningExpression;
        set => SetProperty(ref _listeningExpression, value);
    }

    public string ThinkingExpression
    {
        get => _thinkingExpression;
        set => SetProperty(ref _thinkingExpression, value);
    }

    public string SpeakingExpression
    {
        get => _speakingExpression;
        set => SetProperty(ref _speakingExpression, value);
    }

    public string ErrorExpression
    {
        get => _errorExpression;
        set => SetProperty(ref _errorExpression, value);
    }

    /// <summary>当前所选模型可用的表情名（来自 model3.json + Exp3 文件夹）。</summary>
    public ObservableCollection<string> AvailableExpressions { get; } = new();

    /// <summary>表情/动作扫描结果说明。</summary>
    public string ExpressionCatalogHint
    {
        get => _expressionCatalogHint;
        private set => SetProperty(ref _expressionCatalogHint, value);
    }

    public string FallbackAvatarTheme
    {
        get => _fallbackAvatarTheme;
        set => SetProperty(ref _fallbackAvatarTheme, value);
    }

    public string Live2DStatusText
    {
        get => _live2DStatusText;
        private set => SetProperty(ref _live2DStatusText, value);
    }

    /// <summary>模型库中的模型 + 「内置兜底形象」，设置页下拉框选择。</summary>
    public ObservableCollection<Live2DModelInfo> AvailableLive2DModels { get; } = new();

    /// <summary>下拉框中的“内置兜底形象”选项（不加载 Live2D 模型，使用 WPF 矢量兜底）。</summary>
    public static Live2DModelInfo BuiltInFallbackModel { get; } = new("内置兜底形象（无 Live2D 模型）", string.Empty, string.Empty);

    /// <summary>模型库根目录（导入目标），默认 %APPDATA%\VoxIME\models，可在设置页修改。</summary>
    public string ModelLibraryDirectory
    {
        get => _modelLibraryDirectory;
        set
        {
            if (!SetProperty(ref _modelLibraryDirectory, value?.Trim() ?? ""))
            {
                return;
            }

            RefreshLive2DModelLibrary();
        }
    }

    public Live2DModelInfo? SelectedLive2DModel
    {
        get => _selectedLive2DModel;
        set
        {
            if (!SetProperty(ref _selectedLive2DModel, value))
            {
                return;
            }

            if (value is not null)
            {
                var directory = value.Directory ?? "";
                if (!string.Equals((ModelDirectory ?? "").Trim(), directory, StringComparison.OrdinalIgnoreCase))
                {
                    SaveExpressionMapForModel(_expressionMapModelDirectory);
                    SaveMotionMapForModel(_expressionMapModelDirectory);
                    ModelDirectory = directory;
                    _expressionMapModelDirectory = NormalizeModelDirectory(directory);
                    LoadExpressionMapForModel(_expressionMapModelDirectory, clearWhenMissing: true);
                    LoadMotionMapForModel(_expressionMapModelDirectory, clearWhenMissing: true);
                    StatusText = directory.Length == 0
                        ? "已选择内置兜底形象，点击“保存”后生效。"
                        : $"已选择模型“{value.Name}”，点击“保存”后生效。";
                }

                _eyeFollowInvertY = ResolveEyeFollowInvertYPreference(ModelDirectory) ?? false;
                OnPropertyChanged(nameof(EyeFollowInvertY));
                RefreshExpressionCatalog();
            }

            DeleteLive2DModelCommand.RaiseCanExecuteChanged();
        }
    }

    public RelayCommand BrowseModelLibraryDirectoryCommand { get; }
    public RelayCommand ImportLive2DModelCommand { get; }
    public RelayCommand ImportLive2DModelZipCommand { get; }
    public RelayCommand DeleteLive2DModelCommand { get; }
    public RelayCommand RefreshLive2DModelsCommand { get; }
    public RelayCommand OpenLive2DModelsFolderCommand { get; }
    public RelayCommand ShowLive2DCommand { get; }
    public RelayCommand HideLive2DCommand { get; }
    public RelayCommand ResetLive2DPositionCommand { get; }
    public RelayCommand PreviewIdleMotionCommand { get; }
    public RelayCommand PreviewStateMotionCommand { get; }
    public RelayCommand PreviewExpressionCommand { get; }

    // ==================== 朗读页 ====================

    public bool TtsEnabled
    {
        get => _ttsEnabled;
        set => SetProperty(ref _ttsEnabled, value);
    }

    public bool AutoSpeakResult
    {
        get => _autoSpeakResult;
        set => SetProperty(ref _autoSpeakResult, value);
    }

    public ObservableCollection<string> TtsVoices { get; } = new();

    public string? SelectedTtsVoice
    {
        get => _selectedTtsVoice;
        set => SetProperty(ref _selectedTtsVoice, value);
    }

    public int TtsRate
    {
        get => _ttsRate;
        set => SetProperty(ref _ttsRate, Math.Clamp(value, -10, 10));
    }

    public int TtsVolume
    {
        get => _ttsVolume;
        set => SetProperty(ref _ttsVolume, Math.Clamp(value, 0, 100));
    }

    public string TtsStatusText
    {
        get => _ttsStatusText;
        private set => SetProperty(ref _ttsStatusText, value);
    }

    public bool IsTtsAvailable => TtsVoices.Count > 0;

    public AsyncRelayCommand TestTtsCommand { get; }

    private bool _ttsEnabledTesting;

    // ==================== 通用页 ====================

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set => SetProperty(ref _startWithWindows, value);
    }

    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set => SetProperty(ref _minimizeToTray, value);
    }

    public bool ShowTrayIcon
    {
        get => _showTrayIcon;
        set => SetProperty(ref _showTrayIcon, value);
    }

    public bool ShowWelcomeOnStartup
    {
        get => _showWelcomeOnStartup;
        set => SetProperty(ref _showWelcomeOnStartup, value);
    }

    public int HistoryLimit
    {
        get => _historyLimit;
        set => SetProperty(ref _historyLimit, Math.Clamp(value, 10, 2000));
    }

    public Choice? SelectedLogLevel
    {
        get => _selectedLogLevel;
        set => SetProperty(ref _selectedLogLevel, value);
    }

    // ==================== 外观（主题 + 配色） ====================

    /// <summary>当前主题模式（system/light/dark）。</summary>
    public string SelectedThemeMode => _selectedThemeMode;

    /// <summary>当前选中的配色方案 ID（实际生效值，可能因明暗不匹配已回退）。</summary>
    public string SelectedColorSchemeId => _selectedColorSchemeId;

    /// <summary>跟随系统（RadioButton 三态绑定之一）。</summary>
    public bool IsThemeSystem
    {
        get => _selectedThemeMode == ThemeModes.System;
        set
        {
            if (value)
            {
                SelectThemeMode(ThemeModes.System);
            }
        }
    }

    /// <summary>浅色主题。</summary>
    public bool IsThemeLight
    {
        get => _selectedThemeMode == ThemeModes.Light;
        set
        {
            if (value)
            {
                SelectThemeMode(ThemeModes.Light);
            }
        }
    }

    /// <summary>深色主题。</summary>
    public bool IsThemeDark
    {
        get => _selectedThemeMode == ThemeModes.Dark;
        set
        {
            if (value)
            {
                SelectThemeMode(ThemeModes.Dark);
            }
        }
    }

    /// <summary>当前明暗可用的配色方案卡片。</summary>
    public ObservableCollection<ColorSchemeOption> ColorSchemes { get; } = new();

    /// <summary>选择配色方案（参数为 <see cref="ColorSchemeOption"/>）。</summary>
    public RelayCommand SelectColorSchemeCommand { get; }

    /// <summary>当前生效外观文案。</summary>
    public string AppearanceStatusText
    {
        get
        {
            var resolved = ThemeResolver.Resolve(_selectedThemeMode, _selectedColorSchemeId, ThemeManager.IsSystemLight);
            var modeText = ThemeModes.Normalize(_selectedThemeMode) switch
            {
                ThemeModes.System => $"跟随系统（系统当前：{(resolved.IsLight ? "浅色" : "深色")}）",
                ThemeModes.Light => "浅色",
                _ => "深色"
            };
            return $"当前生效：{modeText} · {resolved.Scheme.DisplayName}";
        }
    }

    /// <summary>配色卡片区提示（解释为什么只列出部分方案）。</summary>
    public string ColorSchemeHint
    {
        get
        {
            var isLight = ThemeModes.IsLight(_selectedThemeMode, ThemeManager.IsSystemLight);
            return isLight
                ? "当前是浅色主题，只列出支持浅色的配色；Catppuccin 的 Frappé / Macchiato / Mocha 是深色专用，切到深色后出现。"
                : "当前是深色主题，只列出支持深色的配色；Catppuccin Latte 是浅色专用，切到浅色后出现。";
        }
    }

    private void SelectThemeMode(string mode)
    {
        var normalized = ThemeModes.Normalize(mode);
        if (string.Equals(_selectedThemeMode, normalized, StringComparison.Ordinal))
        {
            OnPropertiesChanged(nameof(IsThemeSystem), nameof(IsThemeLight), nameof(IsThemeDark));
            return;
        }

        _selectedThemeMode = normalized;
        OnPropertiesChanged(nameof(IsThemeSystem), nameof(IsThemeLight), nameof(IsThemeDark));
        RefreshColorSchemeOptions(applyTheme: !_appearanceInitializing);
    }

    private void SelectColorScheme(ColorSchemeOption? option)
    {
        if (option is null)
        {
            return;
        }

        _selectedColorSchemeId = option.SchemeId;
        foreach (var item in ColorSchemes)
        {
            item.IsSelected = string.Equals(item.SchemeId, option.SchemeId, StringComparison.OrdinalIgnoreCase);
        }

        OnPropertiesChanged(nameof(SelectedColorSchemeId), nameof(AppearanceStatusText));
        ApplyAppearancePreview();
    }

    /// <summary>按当前主题模式重建配色卡片，并把选择回退到实际生效方案。</summary>
    private void RefreshColorSchemeOptions(bool applyTheme)
    {
        var systemIsLight = ThemeManager.IsSystemLight;
        var isLight = ThemeModes.IsLight(_selectedThemeMode, systemIsLight);
        var resolved = ThemeResolver.Resolve(_selectedThemeMode, _selectedColorSchemeId, systemIsLight);
        _selectedColorSchemeId = resolved.ColorSchemeId;

        var options = ThemeCatalog.ForMode(isLight)
            .Select(scheme => new ColorSchemeOption(
                scheme,
                isLight,
                string.Equals(scheme.Id, _selectedColorSchemeId, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        ColorSchemes.Clear();
        foreach (var option in options)
        {
            ColorSchemes.Add(option);
        }

        OnPropertiesChanged(nameof(SelectedColorSchemeId), nameof(AppearanceStatusText), nameof(ColorSchemeHint));
        if (applyTheme)
        {
            ApplyAppearancePreview();
        }
    }

    /// <summary>把当前选择实时应用到界面（未保存时是预览，保存后写入配置）。</summary>
    private void ApplyAppearancePreview()
    {
        if (_appearanceInitializing)
        {
            return;
        }

        _appearanceApplying = true;
        try
        {
            Motion.Apply(_selectedMotionMode);
            ThemeManager.ApplyAppearance(_selectedThemeMode, _selectedColorSchemeId, BuildBackgroundDraft());
        }
        finally
        {
            _appearanceApplying = false;
        }

        OnPropertiesChanged(nameof(AppearanceStatusText));
    }

    private void OnThemeManagerChanged(object? sender, ResolvedTheme theme)
    {
        if (_disposed || _appearanceApplying)
        {
            return;
        }

        // 跟随系统模式：系统明暗切换后主题已由 ThemeManager 应用，这里只刷新可用配色与文案。
        if (ThemeModes.Normalize(_selectedThemeMode) != ThemeModes.System)
        {
            return;
        }

        RefreshColorSchemeOptions(applyTheme: false);
    }

    // ==================== 背景与动效 ====================

    /// <summary>背景模式下拉选项。</summary>
    public IReadOnlyList<Choice> BackgroundModeOptions { get; } = new[]
    {
        new Choice("跟随主题", BackgroundConfig.ModeTheme),
        new Choice("本地图片", BackgroundConfig.ModeImage),
        new Choice("纯色", BackgroundConfig.ModeSolid),
        new Choice("渐变", BackgroundConfig.ModeGradient)
    };

    /// <summary>图片填充方式选项。</summary>
    public IReadOnlyList<Choice> BackgroundFitOptions { get; } = new[]
    {
        new Choice("等比铺满（裁剪）", "cover"),
        new Choice("完整显示", "contain"),
        new Choice("拉伸铺满", "fill")
    };

    /// <summary>动效档位选项。</summary>
    public IReadOnlyList<Choice> MotionOptions { get; } = new[]
    {
        new Choice("明显（默认）", MotionModes.Expressive),
        new Choice("克制", MotionModes.Subtle),
        new Choice("关闭", MotionModes.Off)
    };

    public RelayCommand PickBackgroundImageCommand { get; }
    public RelayCommand ClearBackgroundImageCommand { get; }
    public RelayCommand ResetBackgroundCommand { get; }

    /// <summary>背景模式：theme / image / solid / gradient。</summary>
    public string SelectedBackgroundMode
    {
        get => _backgroundMode;
        set
        {
            var normalized = BackgroundConfig.NormalizeMode(value);
            if (SetProperty(ref _backgroundMode, normalized))
            {
                OnPropertiesChanged(nameof(IsBackgroundImageMode), nameof(IsBackgroundSolidMode),
                    nameof(IsBackgroundGradientMode), nameof(IsBackgroundCustom), nameof(BackgroundStatusText));
                ApplyBackgroundPreviewDebounced();
            }
        }
    }

    /// <summary>动效档位：expressive / subtle / off。</summary>
    public string SelectedMotionMode
    {
        get => _selectedMotionMode;
        set
        {
            var normalized = MotionModes.Normalize(value);
            if (SetProperty(ref _selectedMotionMode, normalized))
            {
                Motion.Apply(normalized);
            }
        }
    }

    public bool IsBackgroundImageMode => _backgroundMode == BackgroundConfig.ModeImage;
    public bool IsBackgroundSolidMode => _backgroundMode == BackgroundConfig.ModeSolid;
    public bool IsBackgroundGradientMode => _backgroundMode == BackgroundConfig.ModeGradient;
    public bool IsBackgroundCustom => _backgroundMode != BackgroundConfig.ModeTheme;
    public bool HasBackgroundImage => !string.IsNullOrWhiteSpace(_backgroundImagePath);

    /// <summary>背景区状态提示。</summary>
    public string BackgroundStatusText
    {
        get
        {
            if (!IsBackgroundCustom)
            {
                return "当前使用主题背景色。";
            }

            if (IsBackgroundImageMode && !HasBackgroundImage)
            {
                return "尚未选择背景图片，暂用主题背景。";
            }

            return IsBackgroundImageMode
                ? "背景图片仅保存在本机（%APPDATA%\\VoxIME\\backgrounds），可随时更换。"
                : "自定义背景会同时让卡片呈现玻璃质感。";
        }
    }

    public string BackgroundImagePath => _backgroundImagePath ?? "";
    public string BackgroundImageName => string.IsNullOrWhiteSpace(_backgroundImagePath)
        ? "未选择图片"
        : Path.GetFileName(_backgroundImagePath);

    public double BackgroundImageOpacity
    {
        get => _backgroundImageOpacity;
        set
        {
            if (SetProperty(ref _backgroundImageOpacity, Math.Clamp(value, 0.2, 1.0)))
            {
                ApplyBackgroundPreviewDebounced();
            }
        }
    }

    public double BackgroundBlurRadius
    {
        get => _backgroundBlurRadius;
        set
        {
            if (SetProperty(ref _backgroundBlurRadius, Math.Clamp(value, 0, 60)))
            {
                ApplyBackgroundPreviewDebounced();
            }
        }
    }

    public double BackgroundDim
    {
        get => _backgroundDim;
        set
        {
            if (SetProperty(ref _backgroundDim, Math.Clamp(value, 0, 0.95)))
            {
                ApplyBackgroundPreviewDebounced();
            }
        }
    }

    public double BackgroundSurfaceOpacity
    {
        get => _backgroundSurfaceOpacity;
        set
        {
            if (SetProperty(ref _backgroundSurfaceOpacity, Math.Clamp(value, 0.35, 1.0)))
            {
                ApplyBackgroundPreviewDebounced();
            }
        }
    }

    public string SelectedBackgroundFit
    {
        get => _backgroundFit;
        set
        {
            if (SetProperty(ref _backgroundFit, BackgroundConfig.NormalizeFit(value)))
            {
                ApplyBackgroundPreviewDebounced();
            }
        }
    }

    /// <summary>文字反色：适合白色/浅色背景，避免浅色背景上看不清浅色文字。</summary>
    public bool BackgroundInvertText
    {
        get => _backgroundInvertText;
        set
        {
            if (SetProperty(ref _backgroundInvertText, value))
            {
                ApplyBackgroundPreviewDebounced();
            }
        }
    }

    public string? BackgroundSolidColor
    {
        get => _backgroundSolidColor;
        set
        {
            if (SetProperty(ref _backgroundSolidColor, value))
            {
                ApplyBackgroundPreviewDebounced();
            }
        }
    }

    public string? BackgroundGradientStart
    {
        get => _backgroundGradientStart;
        set
        {
            if (SetProperty(ref _backgroundGradientStart, value))
            {
                ApplyBackgroundPreviewDebounced();
            }
        }
    }

    public string? BackgroundGradientEnd
    {
        get => _backgroundGradientEnd;
        set
        {
            if (SetProperty(ref _backgroundGradientEnd, value))
            {
                ApplyBackgroundPreviewDebounced();
            }
        }
    }

    private BackgroundConfig BuildBackgroundDraft() => new()
    {
        Mode = _backgroundMode,
        ImagePath = _backgroundImagePath,
        ImageOpacity = _backgroundImageOpacity,
        BlurRadius = _backgroundBlurRadius,
        Dim = _backgroundDim,
        Fit = _backgroundFit,
        SolidColor = _backgroundSolidColor,
        GradientStart = _backgroundGradientStart,
        GradientEnd = _backgroundGradientEnd,
        SurfaceOpacity = _backgroundSurfaceOpacity,
        InvertText = _backgroundInvertText
    };

    private void PickBackgroundImage()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择背景图片",
            Filter = "图片文件 (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var stored = BackgroundImageStore.Import(dialog.FileName);
            if (stored is null)
            {
                return;
            }

            _backgroundImagePath = stored;
            _backgroundMode = BackgroundConfig.ModeImage;
            OnPropertiesChanged(nameof(BackgroundImagePath), nameof(BackgroundImageName), nameof(HasBackgroundImage),
                nameof(SelectedBackgroundMode), nameof(IsBackgroundImageMode), nameof(IsBackgroundSolidMode),
                nameof(IsBackgroundGradientMode), nameof(IsBackgroundCustom), nameof(BackgroundStatusText));
            ApplyAppearancePreview();
        }
        catch (Exception ex)
        {
            _logger.Warn($"导入背景图片失败：{ex.Message}");
            Ui.Warn("无法使用这张图片：" + ex.Message);
        }
    }

    private void ClearBackgroundImage()
    {
        BackgroundImageStore.Delete(_backgroundImagePath);
        _backgroundImagePath = null;
        _backgroundMode = BackgroundConfig.ModeTheme;
        OnPropertiesChanged(nameof(BackgroundImagePath), nameof(BackgroundImageName), nameof(HasBackgroundImage),
            nameof(SelectedBackgroundMode), nameof(IsBackgroundImageMode), nameof(IsBackgroundSolidMode),
            nameof(IsBackgroundGradientMode), nameof(IsBackgroundCustom), nameof(BackgroundStatusText));
        ApplyAppearancePreview();
    }

    private void ResetBackground()
    {
        var defaults = new BackgroundConfig();
        _backgroundMode = defaults.Mode;
        _backgroundImagePath = null;
        _backgroundImageOpacity = defaults.ImageOpacity;
        _backgroundBlurRadius = defaults.BlurRadius;
        _backgroundDim = defaults.Dim;
        _backgroundFit = defaults.Fit;
        _backgroundSolidColor = null;
        _backgroundGradientStart = null;
        _backgroundGradientEnd = null;
        _backgroundSurfaceOpacity = defaults.SurfaceOpacity;
        _backgroundInvertText = false;
        OnPropertiesChanged(nameof(SelectedBackgroundMode), nameof(SelectedBackgroundFit),
            nameof(BackgroundImagePath), nameof(BackgroundImageName), nameof(HasBackgroundImage),
            nameof(BackgroundImageOpacity), nameof(BackgroundBlurRadius), nameof(BackgroundDim),
            nameof(BackgroundSurfaceOpacity), nameof(BackgroundInvertText), nameof(BackgroundSolidColor),
            nameof(BackgroundGradientStart), nameof(BackgroundGradientEnd), nameof(IsBackgroundImageMode),
            nameof(IsBackgroundSolidMode), nameof(IsBackgroundGradientMode), nameof(IsBackgroundCustom),
            nameof(BackgroundStatusText));
        ApplyAppearancePreview();
    }

    private void ApplyBackgroundPreviewDebounced()
    {
        if (_appearanceInitializing)
        {
            return;
        }

        _backgroundPreviewTimer ??= CreateBackgroundPreviewTimer();
        _backgroundPreviewTimer.Stop();
        _backgroundPreviewTimer.Start();
    }

    private DispatcherTimer CreateBackgroundPreviewTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ApplyAppearancePreview();
        };
        return timer;
    }

    // ==================== 初始化 ====================

    private void InitializeFrom(AppConfig config)
    {
        _working = ConfigJson.CloneAppConfig(config);

        // 服务
        Providers.Clear();
        foreach (var profile in config.ProviderProfiles)
        {
            var item = new ProviderItemViewModel(profile, _registry);
            Providers.Add(item);
        }

        _defaultProviderId = config.ActiveProviderProfileId;
        if (Providers.All(p => p.Id != _defaultProviderId))
        {
            _defaultProviderId = Providers.FirstOrDefault(p => p.Enabled)?.Id
                                 ?? Providers.FirstOrDefault()?.Id
                                 ?? "";
        }

        UpdateDefaultFlags();
        SelectedProvider = Providers.FirstOrDefault(p => p.Id == _defaultProviderId) ?? Providers.FirstOrDefault();
        NewDescriptor = Descriptors.FirstOrDefault();
        if (NewDescriptor is null)
        {
            NewPresets.Clear();
            NewPreset = null;
        }

        // 录音
        AudioDevices.Clear();
        AudioDevices.Add(new AudioDeviceInfo { Id = "", Name = "系统默认设备", IsDefault = true });
        try
        {
            foreach (var device in _controller.GetInputDevices())
            {
                AudioDevices.Add(device);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"枚举录音设备失败：{ex.Message}");
        }

        SelectedAudioDevice = AudioDevices.FirstOrDefault(d =>
                                  string.Equals(d.Id, config.Audio.DeviceId, StringComparison.Ordinal))
                              ?? AudioDevices[0];

        LoadHotkeyRows(PushToTalkHotkeys, config.Hotkeys.PushToTalkGestures, config.Hotkeys.PushToTalk);
        LoadHotkeyRows(ToggleHotkeys, config.Hotkeys.ToggleGestures, config.Hotkeys.Toggle);
        LoadHotkeyRows(CancelHotkeys, config.Hotkeys.CancelGestures, config.Hotkeys.Cancel);
        EnablePushToTalk = config.Hotkeys.EnablePushToTalk;
        EnableToggle = config.Hotkeys.EnableToggle;
        RecordingMode = config.Hotkeys.DefaultMode;
        MaxDurationSeconds = config.Audio.MaxDurationSeconds;
        SilenceTimeoutMs = config.Audio.SilenceTimeoutMs;
        MinSpeechMs = config.Audio.MinSpeechMs;
        VadThreshold = config.Audio.VadThreshold;
        AutoStopOnSilence = config.Audio.AutoStopOnSilence;
        PlayCues = config.Audio.PlayCues;
        AppContextMode = config.Audio.AppContextMode;

        // 上屏
        InsertionMode = config.Insertion.Mode;
        AutoInsert = config.Insertion.AutoInsert;
        PreviewBeforeInsert = config.Insertion.PreviewBeforeInsert;
        RestoreClipboard = config.Insertion.RestoreClipboard;
        TrimWhitespace = config.Insertion.TrimWhitespace;
        AppendSpace = config.Insertion.AppendSpace;
        PressEnterAfterInsert = config.Insertion.PressEnterAfterInsert;
        PasteDelayMs = config.Insertion.PasteDelayMs;
        Replacements.Clear();
        foreach (var replacement in config.Insertion.Replacements)
        {
            Replacements.Add(new ReplacementItemViewModel(replacement.From, replacement.To));
        }

        ShowHud = config.Overlay.ShowHud;
        ShowLiveTranscript = config.Overlay.ShowLiveTranscript;
        OverlayAutoInsert = config.Overlay.AutoInsert;
        OverlayAutoHideMs = config.Overlay.AutoHideMs;

        // 桌宠
        Live2DEnabled = config.Live2D.Enabled;
        ModelDirectory = config.Live2D.ModelDirectory ?? "";
        Live2DWidth = config.Live2D.Width;
        Live2DHeight = config.Live2D.Height;
        AutoFitWindow = config.Live2D.AutoFitWindow;
        SnapToEdge = config.Live2D.SnapToEdge;
        KeepAboveTaskbar = config.Live2D.KeepAboveTaskbar;
        Live2DScale = config.Live2D.Scale;
        ModelOffsetX = config.Live2D.ModelOffsetX;
        ModelOffsetY = config.Live2D.ModelOffsetY;
        Live2DOpacity = config.Live2D.Opacity;
        MouthSensitivity = config.Live2D.MouthSensitivity;
        ClickThrough = config.Live2D.ClickThrough;
        AlwaysOnTop = config.Live2D.AlwaysOnTop;
        EyeFollow = config.Live2D.EyeFollow;
        EyeFollowFps = config.Live2D.EyeFollowFps;
        _eyeFollowInvertY = ResolveEyeFollowInvertYPreference(config.Live2D.ModelDirectory) ?? false;
        OnPropertyChanged(nameof(EyeFollowInvertY));
        IdleMotion = config.Live2D.IdleMotion;
        SetMouthSync(config.Live2D.MouthSync);
        ShowBubble = config.Live2D.ShowBubble;
        IdleMotionGroup = config.Live2D.IdleMotionGroup;
        ListeningMotionGroup = config.Live2D.ListeningMotionGroup ?? "";
        ThinkingMotionGroup = config.Live2D.ThinkingMotionGroup ?? "";
        SpeakingMotionGroup = config.Live2D.SpeakingMotionGroup ?? "";
        ErrorMotionGroup = config.Live2D.ErrorMotionGroup ?? "";
        ListeningExpression = config.Live2D.ListeningExpression ?? "";
        ThinkingExpression = config.Live2D.ThinkingExpression ?? "";
        SpeakingExpression = config.Live2D.SpeakingExpression ?? "";
        ErrorExpression = config.Live2D.ErrorExpression ?? "";
        FallbackAvatarTheme = config.Live2D.FallbackAvatarTheme;
        ModelLibraryDirectory = string.IsNullOrWhiteSpace(config.Live2D.ModelLibraryDirectory)
            ? AppInfo.DefaultModelDirectory
            : config.Live2D.ModelLibraryDirectory;
        RefreshLive2DStatus();
        RefreshLive2DModelLibrary();

        // 朗读
        TtsEnabled = config.Tts.Enabled;
        AutoSpeakResult = config.Tts.AutoSpeakResult;
        TtsMouthSync = config.Tts.MouthSync;
        TtsRate = config.Tts.Rate;
        TtsVolume = config.Tts.Volume;

        TtsVoices.Clear();
        try
        {
            foreach (var voice in _controller.GetTtsVoices())
            {
                TtsVoices.Add(voice);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"枚举语音包失败：{ex.Message}");
        }

        SelectedTtsVoice = !string.IsNullOrEmpty(config.Tts.VoiceName) && TtsVoices.Contains(config.Tts.VoiceName)
            ? config.Tts.VoiceName
            : TtsVoices.FirstOrDefault();
        TtsStatusText = TtsVoices.Count == 0
            ? "未检测到 Windows 语音包，朗读功能不可用。"
            : $"已检测到 {TtsVoices.Count} 个语音包。";

        // 通用
        StartWithWindows = config.Ui.StartWithWindows || StartupRegistration.IsEnabled();
        MinimizeToTray = config.Ui.MinimizeToTray;
        ShowTrayIcon = config.Ui.ShowTrayIcon;
        ShowWelcomeOnStartup = config.Ui.ShowWelcomeOnStartup;
        HistoryLimit = config.Ui.HistoryLimit;
        SelectedLogLevel = LogLevels.FirstOrDefault(l =>
                                string.Equals(l.Value, config.Logging.Level, StringComparison.OrdinalIgnoreCase))
                            ?? LogLevels[2];

        // 外观（主题 + 配色）
        _selectedThemeMode = ThemeModes.Normalize(config.Ui.Theme);
        _selectedColorSchemeId = ThemeCatalog.NormalizeId(config.Ui.ColorScheme);
        _appearanceInitializing = true;
        try
        {
            RefreshColorSchemeOptions(applyTheme: false);
        }
        finally
        {
            _appearanceInitializing = false;
        }

        OnPropertiesChanged(nameof(SelectedThemeMode), nameof(SelectedColorSchemeId),
            nameof(IsThemeSystem), nameof(IsThemeLight), nameof(IsThemeDark),
            nameof(AppearanceStatusText), nameof(ColorSchemeHint));

        // 背景与动效
        var background = config.Ui.Background ?? new BackgroundConfig();
        background.Normalize();
        _backgroundMode = background.Mode;
        _backgroundImagePath = background.ImagePath;
        _backgroundImageOpacity = background.ImageOpacity;
        _backgroundBlurRadius = background.BlurRadius;
        _backgroundDim = background.Dim;
        _backgroundFit = background.Fit;
        _backgroundSolidColor = background.SolidColor;
        _backgroundGradientStart = background.GradientStart;
        _backgroundGradientEnd = background.GradientEnd;
        _backgroundSurfaceOpacity = background.SurfaceOpacity;
        _backgroundInvertText = background.InvertText;
        _selectedMotionMode = MotionModes.Normalize(config.Ui.Motion);
        OnPropertiesChanged(nameof(SelectedBackgroundMode), nameof(SelectedBackgroundFit),
            nameof(BackgroundImagePath), nameof(BackgroundImageName), nameof(HasBackgroundImage),
            nameof(BackgroundImageOpacity), nameof(BackgroundBlurRadius), nameof(BackgroundDim),
            nameof(BackgroundSurfaceOpacity), nameof(BackgroundInvertText), nameof(BackgroundSolidColor),
            nameof(BackgroundGradientStart), nameof(BackgroundGradientEnd), nameof(IsBackgroundImageMode),
            nameof(IsBackgroundSolidMode), nameof(IsBackgroundGradientMode), nameof(IsBackgroundCustom),
            nameof(BackgroundStatusText), nameof(SelectedMotionMode));

        ValidateHotkeys();
        _expressionMapModelDirectory = NormalizeModelDirectory(ModelDirectory);
        LoadExpressionMapForModel(_expressionMapModelDirectory, clearWhenMissing: false);
        LoadMotionMapForModel(_expressionMapModelDirectory, clearWhenMissing: false);
        RefreshExpressionCatalog();
    }

    private static string NormalizeModelDirectory(string? directory) => (directory ?? "").Trim();

    private void SaveExpressionMapForModel(string? modelDirectory)
    {
        var key = NormalizeModelDirectory(modelDirectory);
        if (key.Length == 0)
        {
            return;
        }

        _working.Live2D.ExpressionByModel[key] = new Live2DExpressionMap
        {
            Listening = EmptyToNull(ListeningExpression),
            Thinking = EmptyToNull(ThinkingExpression),
            Speaking = EmptyToNull(SpeakingExpression),
            Error = EmptyToNull(ErrorExpression)
        };
    }

    private void LoadExpressionMapForModel(string? modelDirectory, bool clearWhenMissing)
    {
        var key = NormalizeModelDirectory(modelDirectory);
        if (key.Length > 0 && _working.Live2D.ExpressionByModel.TryGetValue(key, out var map) && map is not null)
        {
            ListeningExpression = map.Listening ?? "";
            ThinkingExpression = map.Thinking ?? "";
            SpeakingExpression = map.Speaking ?? "";
            ErrorExpression = map.Error ?? "";
            return;
        }

        if (clearWhenMissing)
        {
            ListeningExpression = "";
            ThinkingExpression = "";
            SpeakingExpression = "";
            ErrorExpression = "";
        }
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void SaveMotionMapForModel(string? modelDirectory)
    {
        var key = NormalizeModelDirectory(modelDirectory);
        if (key.Length == 0)
        {
            return;
        }

        _working.Live2D.MotionByModel[key] = new Live2DMotionMap
        {
            IdleGroup = string.IsNullOrWhiteSpace(IdleMotionGroup) ? "Idle" : IdleMotionGroup.Trim(),
            Listening = EmptyToNull(ListeningMotionGroup),
            Thinking = EmptyToNull(ThinkingMotionGroup),
            Speaking = EmptyToNull(SpeakingMotionGroup),
            Error = EmptyToNull(ErrorMotionGroup)
        };
    }

    private void LoadMotionMapForModel(string? modelDirectory, bool clearWhenMissing)
    {
        var key = NormalizeModelDirectory(modelDirectory);
        if (key.Length > 0 && _working.Live2D.MotionByModel.TryGetValue(key, out var map) && map is not null)
        {
            IdleMotionGroup = string.IsNullOrWhiteSpace(map.IdleGroup) ? "Idle" : map.IdleGroup;
            ListeningMotionGroup = map.Listening ?? "";
            ThinkingMotionGroup = map.Thinking ?? "";
            SpeakingMotionGroup = map.Speaking ?? "";
            ErrorMotionGroup = map.Error ?? "";
            return;
        }

        if (clearWhenMissing)
        {
            IdleMotionGroup = "Idle";
            ListeningMotionGroup = "";
            ThinkingMotionGroup = "";
            SpeakingMotionGroup = "";
            ErrorMotionGroup = "";
        }
    }

    private void RefreshExpressionCatalog()
    {
        try
        {
            var catalog = Live2DModelAssetCatalog.Load(ModelDirectory);
            AvailableExpressions.Clear();
            foreach (var expression in catalog.Expressions)
            {
                if (!AvailableExpressions.Contains(expression.Name, StringComparer.OrdinalIgnoreCase))
                {
                    AvailableExpressions.Add(expression.Name);
                }
            }

            AvailableMotionGroups.Clear();
            StateMotionChoices.Clear();
            StateMotionChoices.Add(new Choice("自动（按状态同名动作组）", ""));
            StateMotionChoices.Add(new Choice("不播放", "__none__"));
            foreach (var group in catalog.Motions
                         .Select(motion => motion.Group)
                         .Where(group => !string.IsNullOrWhiteSpace(group))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                AvailableMotionGroups.Add(group);
                StateMotionChoices.Add(new Choice(group, group));
            }

            // 旧配置里的自定义组名不在当前模型里时，也放进下拉框，避免保存后显示空白。
            foreach (var value in new[] { ListeningMotionGroup, ThinkingMotionGroup, SpeakingMotionGroup, ErrorMotionGroup })
            {
                if (!string.IsNullOrWhiteSpace(value)
                    && !string.Equals(value, "__none__", StringComparison.Ordinal)
                    && !AvailableMotionGroups.Contains(value, StringComparer.OrdinalIgnoreCase)
                    && !StateMotionChoices.Any(choice => string.Equals(choice.Value, value, StringComparison.OrdinalIgnoreCase)))
                {
                    StateMotionChoices.Add(new Choice($"自定义：{value}", value));
                }
            }

            if (string.IsNullOrWhiteSpace(ModelDirectory))
            {
                ExpressionCatalogHint = "当前使用内置兜底形象，没有表情文件；可留空或手动输入。";
            }
            else if (catalog.Expressions.Count == 0)
            {
                ExpressionCatalogHint = $"未在模型中找到表情文件（已扫描 model3.json/Exp3）；可手动输入表情名。"
                    + (catalog.Motions.Count > 0 ? $" 已发现 {catalog.Motions.Count} 个动作文件。" : "");
            }
            else
            {
                ExpressionCatalogHint = $"已发现 {catalog.Expressions.Count} 个表情"
                    + (catalog.Motions.Count > 0 ? $"、{catalog.Motions.Count} 个动作" : "")
                    + "；可从下拉框选择，也可手动输入，留空表示不切换。";
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"扫描模型表情失败：{ex.Message}");
            AvailableExpressions.Clear();
            AvailableMotionGroups.Clear();
            StateMotionChoices.Clear();
            StateMotionChoices.Add(new Choice("自动（按状态同名动作组）", ""));
            StateMotionChoices.Add(new Choice("不播放", "__none__"));
            ExpressionCatalogHint = "读取模型表情失败，可手动输入表情名。";
        }
    }

    private void PreviewIdleMotion()
    {
        var group = string.IsNullOrWhiteSpace(IdleMotionGroup) ? "Idle" : IdleMotionGroup.Trim();
        PreviewMotion(group);
    }

    private void PreviewStateMotion(string? state)
    {
        var normalized = (state ?? "").Trim().ToLowerInvariant();
        var configured = normalized switch
        {
            "listening" => ListeningMotionGroup,
            "thinking" => ThinkingMotionGroup,
            "speaking" => SpeakingMotionGroup,
            "error" => ErrorMotionGroup,
            _ => null
        };

        if (string.Equals(configured, "__none__", StringComparison.Ordinal))
        {
            MotionPreviewStatus = "该状态设置为不播放动作。";
            return;
        }

        string group;
        var statusOverride = (string?)null;
        if (string.IsNullOrWhiteSpace(configured))
        {
            var autoName = normalized switch
            {
                "listening" => "Listening",
                "thinking" => "Thinking",
                "speaking" => "Speaking",
                "error" => "Error",
                _ => ""
            };
            var actual = AvailableMotionGroups.FirstOrDefault(item =>
                string.Equals(item, autoName, StringComparison.OrdinalIgnoreCase));
            if (actual is null)
            {
                // 自动模式下没有同名组时，预览回退到模型第一个非 Idle 动作，避免按钮看起来“不可用”。
                actual = AvailableMotionGroups.FirstOrDefault(item =>
                             !string.Equals(item, "Idle", StringComparison.OrdinalIgnoreCase))
                         ?? AvailableMotionGroups.FirstOrDefault();
                if (actual is null)
                {
                    MotionPreviewStatus = autoName.Length == 0
                        ? "当前模型没有可预览的动作组。"
                        : $"自动：当前模型没有名为 {autoName} 的动作组，也没有其它动作组。请先导入带动作的模型。";
                    return;
                }

                statusOverride = autoName.Length == 0
                    ? $"已预览首个动作：{actual}"
                    : $"自动：没有名为 {autoName} 的动作组，已预览首个动作：{actual}";
            }

            group = actual;
        }
        else
        {
            group = configured.Trim();
        }

        PreviewMotion(group, statusOverride);
    }

    private void PreviewExpression(string? state)
    {
        var normalized = (state ?? "").Trim().ToLowerInvariant();
        var expression = normalized switch
        {
            "listening" => ListeningExpression,
            "thinking" => ThinkingExpression,
            "speaking" => SpeakingExpression,
            "error" => ErrorExpression,
            _ => null
        };

        if (string.IsNullOrWhiteSpace(expression))
        {
            ExpressionPreviewStatus = "该状态没有配置表情，先选择或输入一个表情名。";
            return;
        }

        var trimmed = expression.Trim();
        try
        {
            _controller.Live2D.PreviewExpression(trimmed);
            ExpressionPreviewStatus = $"已预览表情：{trimmed}（3 秒后恢复默认表情）";
        }
        catch (Exception ex)
        {
            _logger.Warn($"预览表情失败：{ex.Message}");
            ExpressionPreviewStatus = "预览失败：" + ex.Message;
        }
    }

    private void PreviewMotion(string group, string? statusOverride = null)
    {
        try
        {
            _controller.Live2D.PreviewMotion(group);
            var isIdlePreview = string.Equals(group, string.IsNullOrWhiteSpace(IdleMotionGroup) ? "Idle" : IdleMotionGroup.Trim(), StringComparison.OrdinalIgnoreCase)
                || string.Equals(group, "Idle", StringComparison.OrdinalIgnoreCase);
            MotionPreviewStatus = statusOverride ?? (isIdlePreview
                ? $"已预览待机动作：{group}（播完停 3 秒后继续待机循环）"
                : $"已预览：{group}（播完停 3 秒后恢复默认）");
        }
        catch (Exception ex)
        {
            _logger.Warn($"预览动作失败：{ex.Message}");
            MotionPreviewStatus = "预览失败：" + ex.Message;
        }
    }

    private void SetMouthSync(bool value)
    {
        _mouthSync = value;
        OnPropertyChanged(nameof(MouthSync));
    }

    public bool MouthSync
    {
        get => _mouthSync;
        set => SetProperty(ref _mouthSync, value);
    }

    // ==================== 服务页逻辑 ====================

    private void UpdateDefaultFlags()
    {
        foreach (var provider in Providers)
        {
            provider.IsDefault = string.Equals(provider.Id, _defaultProviderId, StringComparison.Ordinal);
        }

        OnPropertyChanged(nameof(SelectedProviderIsDefault));
        SetDefaultProviderCommand.RaiseCanExecuteChanged();
    }

    private void AddProvider()
    {
        var descriptor = NewDescriptor;
        if (descriptor is null)
        {
            return;
        }

        try
        {
            var template = NewPreset?.Template.Clone() ?? new ProviderProfile
            {
                TypeId = descriptor.TypeId,
                Name = descriptor.DisplayName,
                Language = "zh",
                TimeoutSeconds = 60
            };

            template.Id = Guid.NewGuid().ToString("N")[..8];
            template.TypeId = descriptor.TypeId;
            template.Name = string.IsNullOrWhiteSpace(template.Name) ? descriptor.DisplayName : template.Name;
            template.Enabled = true;

            var item = new ProviderItemViewModel(template, _registry);
            Providers.Add(item);
            SelectedProvider = item;
            StatusText = $"已添加服务“{item.Name}”，填写参数后点击“保存”。";
        }
        catch (Exception ex)
        {
            _logger.Error("添加服务失败", ex);
            Ui.Error("添加服务失败：" + ex.Message);
        }
    }

    private void RemoveProvider()
    {
        var item = SelectedProvider;
        if (item is null)
        {
            return;
        }

        if (Providers.Count <= 1)
        {
            Ui.Warn("至少需要保留一个识别服务。");
            return;
        }

        if (!Ui.Confirm($"确定删除服务“{item.Name}”吗？", "删除服务", "删除", "取消"))
        {
            return;
        }

        var index = Providers.IndexOf(item);
        Providers.Remove(item);

        if (string.Equals(_defaultProviderId, item.Id, StringComparison.Ordinal))
        {
            _defaultProviderId = Providers.FirstOrDefault(p => p.Enabled)?.Id ?? Providers[0].Id;
            UpdateDefaultFlags();
        }

        SelectedProvider = index > 0
            ? Providers[Math.Min(index - 1, Providers.Count - 1)]
            : Providers.FirstOrDefault();
        StatusText = "已删除服务，点击“保存”后生效。";
    }

    private void SetDefaultProvider()
    {
        var item = SelectedProvider;
        if (item is null)
        {
            return;
        }

        item.Enabled = true;
        _defaultProviderId = item.Id;
        UpdateDefaultFlags();
        StatusText = $"已将“{item.Name}”设为默认服务，点击“保存”后生效。";
    }

    private async Task TestProviderAsync()
    {
        var item = SelectedProvider;
        if (item is null)
        {
            return;
        }

        IsTesting = true;
        HasTestResult = true;
        IsTestSuccess = false;
        TestResultText = "正在录音 3 秒并调用识别服务，请对着麦克风说话…";
        StatusText = $"正在测试服务“{item.Name}”…";

        try
        {
            var profile = item.ToProfile();
            var result = await _controller.TestProviderAsync(profile, 3).ConfigureAwait(true);
            IsTestSuccess = true;
            TestResultText = $"识别成功（{result.Latency.TotalMilliseconds:0} ms · 音频 {result.AudioDuration.TotalSeconds:0.0}s）：{result.Text}";
            StatusText = "测试识别成功。";
        }
        catch (AsrException ex)
        {
            IsTestSuccess = false;
            var code = ex.StatusCode is int status ? $"（HTTP {status}）" : "";
            TestResultText = $"识别失败{code}：{ex.Message}";
            StatusText = "测试识别失败，请检查服务参数与网络。";
            _logger.Warn($"测试服务失败：{ex.Message}");
        }
        catch (Exception ex)
        {
            IsTestSuccess = false;
            TestResultText = "识别失败：" + ex.Message;
            StatusText = "测试识别失败。";
            _logger.Warn($"测试服务异常：{ex.Message}");
        }
        finally
        {
            IsTesting = false;
        }
    }

    private void OpenProviderDocs()
    {
        var url = SelectedProvider?.Descriptor?.DocsUrl;
        if (!string.IsNullOrWhiteSpace(url))
        {
            WindowHelper.OpenUrl(url);
        }
    }

    // ==================== 录音页逻辑 ====================

    private void ValidateHotkeys()
    {
        var push = NormalizeHotkeyRows(PushToTalkHotkeys, "按住说话", out var pushError);
        var toggle = NormalizeHotkeyRows(ToggleHotkeys, "切换录音", out var toggleError);
        var cancel = NormalizeHotkeyRows(CancelHotkeys, "取消录音", out var cancelError);

        ApplyHotkeyConflict(push, toggle, "按住说话", "切换录音", ref pushError, ref toggleError);
        ApplyHotkeyConflict(push, cancel, "按住说话", "取消录音", ref pushError, ref cancelError);
        ApplyHotkeyConflict(toggle, cancel, "切换录音", "取消录音", ref toggleError, ref cancelError);

        if (RecordingMode == RecordingMode.Toggle && !EnableToggle)
        {
            toggleError ??= "当前为“按一下开始/结束”模式，请勾选切换录音的“启用”";
        }
        else if (RecordingMode == RecordingMode.PushToTalk && !EnablePushToTalk)
        {
            pushError ??= "当前为“按住说话”模式，请勾选按住说话的“启用”";
        }

        PushToTalkError = pushError;
        ToggleHotkeyError = toggleError;
        CancelHotkeyError = cancelError;
    }

    private static List<string> NormalizeHotkeyRows(
        ObservableCollection<HotkeyBindingViewModel> rows,
        string actionName,
        out string? error)
    {
        error = null;
        var result = new List<string>();
        var index = 0;
        foreach (var row in rows)
        {
            index++;
            if (string.IsNullOrWhiteSpace(row.Gesture))
            {
                continue;
            }

            if (!HotkeyGesture.TryNormalize(row.Gesture, out var normalized, out var message))
            {
                error ??= $"{actionName}第 {index} 个热键无效：{message}";
                continue;
            }

            if (result.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                error ??= $"{actionName}存在重复热键：{normalized}";
                continue;
            }

            result.Add(normalized);
        }

        if (error is null && result.Count == 0)
        {
            error = $"{actionName}至少需要一个热键";
        }

        return result;
    }

    private static void ApplyHotkeyConflict(
        List<string> first,
        List<string> second,
        string firstName,
        string secondName,
        ref string? firstError,
        ref string? secondError)
    {
        var conflict = first.FirstOrDefault(
            gesture => second.Contains(gesture, StringComparer.OrdinalIgnoreCase));
        if (conflict is null)
        {
            return;
        }

        var message = $"热键 {conflict} 同时被“{firstName}”和“{secondName}”使用";
        firstError ??= message;
        secondError ??= message;
    }

    private void LoadHotkeyRows(
        ObservableCollection<HotkeyBindingViewModel> rows,
        List<string>? configured,
        string legacy)
    {
        foreach (var row in rows)
        {
            row.Changed -= OnHotkeyRowChanged;
        }

        rows.Clear();
        var values = configured?.Where(value => !string.IsNullOrWhiteSpace(value)).ToList() ?? new List<string>();
        if (values.Count == 0 && !string.IsNullOrWhiteSpace(legacy))
        {
            values.Add(legacy.Trim());
        }

        if (values.Count == 0)
        {
            values.Add("");
        }

        foreach (var value in values)
        {
            var row = new HotkeyBindingViewModel { Gesture = value.Trim() };
            row.Changed += OnHotkeyRowChanged;
            rows.Add(row);
        }
    }

    private void AddHotkeyRow(ObservableCollection<HotkeyBindingViewModel> rows)
    {
        if (rows.Count >= 8)
        {
            return;
        }

        var row = new HotkeyBindingViewModel();
        row.Changed += OnHotkeyRowChanged;
        rows.Add(row);
        ValidateHotkeys();
    }

    private void RemoveHotkeyRow(ObservableCollection<HotkeyBindingViewModel> rows, HotkeyBindingViewModel? row)
    {
        if (row is null || !rows.Remove(row))
        {
            return;
        }

        row.Changed -= OnHotkeyRowChanged;
        if (rows.Count == 0)
        {
            var blank = new HotkeyBindingViewModel();
            blank.Changed += OnHotkeyRowChanged;
            rows.Add(blank);
        }

        ValidateHotkeys();
    }

    private void OnHotkeyRowChanged(object? sender, EventArgs e) => ValidateHotkeys();

    private async Task TestMicrophoneAsync()
    {
        if (IsMicTesting)
        {
            return;
        }

        if (_controller.State != AppState.Idle)
        {
            MicTestStatus = "当前正在录音或识别，请稍后再试。";
            return;
        }

        var config = _configStore.Current;
        var originalPlayCues = config.Audio.PlayCues;
        string DeviceLabel()
        {
            var id = config.Audio.DeviceId;
            var device = !string.IsNullOrWhiteSpace(id)
                ? AudioDevices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.Ordinal))
                : AudioDevices.Skip(1).FirstOrDefault(d => d.IsDefault);
            return device?.Name ?? "系统默认设备";
        }

        try
        {
            // 测试时不要播放提示音：虚拟声卡/远程回录设备会把提示音当成人声，
            // 造成“测试有电平、正常说话没声”的假阳性。
            config.Audio.PlayCues = false;

            if (!_controller.StartRecording())
            {
                MicTestStatus = "无法开始录音：麦克风可能被占用或不可用。";
                return;
            }

            IsMicTesting = true;
            MicTestLevel = 0;
            _micTestMaxLevel = 0;
            MicTestStatus = "正在录音 3 秒，请对着麦克风说话…";

            try
            {
                await Task.Delay(3000).ConfigureAwait(true);
            }
            finally
            {
                _controller.Cancel();
            }

            var deviceName = DeviceLabel();
            var lowLevel = _micTestMaxLevel <= 0.001;
            if (!lowLevel)
            {
                MicTestStatus = $"录音测试完成（设备：{deviceName}）：本次最大电平 {_micTestMaxLevel:P1}；电平有波动说明麦克风工作正常。";
            }
            else
            {
                var extra = LooksLikeVirtualMicrophone(deviceName)
                    ? "这是虚拟/远程音频设备，可能采集不到本机说话声，请改选真实麦克风并保存。"
                    : "请检查 Windows 麦克风权限/设备选择，或在 设置 → 录音 里换一个麦克风。";
                MicTestStatus = $"录音测试完成（设备：{deviceName}）：几乎没有采集到电平。{extra}";
            }
        }
        catch (Exception ex)
        {
            MicTestStatus = "录音测试失败：" + ex.Message;
            _logger.Warn($"录音测试失败：{ex.Message}");
        }
        finally
        {
            config.Audio.PlayCues = originalPlayCues;
            IsMicTesting = false;
            MicTestLevel = 0;
        }
    }

    private void OnMicrophoneLevelChanged(object? sender, AudioLevelEventArgs e)
    {
        WindowHelper.RunOnUi(() =>
        {
            _micTestMaxLevel = Math.Max(_micTestMaxLevel, e.Level);
            MicTestLevel = Math.Clamp(e.Level * 9.0, 0, 1);
        });
    }

    // ==================== 桌宠页逻辑 ====================

    private bool? ResolveEyeFollowInvertYPreference(string? modelDirectory)
    {
        var directory = (modelDirectory ?? "").Trim();
        if (directory.Length == 0)
        {
            return false;
        }

        if (_configStore.Current.Live2D.EyeFollowInvertYByModel.TryGetValue(directory, out var saved))
        {
            return saved;
        }

        if (_working.Live2D.EyeFollowInvertYByModel.TryGetValue(directory, out var draft))
        {
            return draft;
        }

        return false;
    }

    private void BrowseModelLibraryDirectory()
    {
        try
        {
            var dialog = new OpenFolderDialog
            {
                Title = "选择模型库位置（导入的模型会复制到这里）",
                Multiselect = false
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            ModelLibraryDirectory = dialog.FolderName;
            StatusText = "已选择模型库位置，点击“保存”后生效；已有模型不会自动迁移，可手动移动文件夹后点“刷新列表”。";
        }
        catch (Exception ex)
        {
            _logger.Warn($"选择模型库位置失败：{ex.Message}");
            Ui.Warn("无法打开目录选择框：" + ex.Message);
        }
    }

    private Live2DModelLibrary CreateModelLibrary()
    {
        var root = string.IsNullOrWhiteSpace(ModelLibraryDirectory)
            ? AppInfo.DefaultModelDirectory
            : ModelLibraryDirectory.Trim();
        return new Live2DModelLibrary(root);
    }

    private bool IsDefaultModelLibraryDirectory()
    {
        if (string.IsNullOrWhiteSpace(ModelLibraryDirectory))
        {
            return true;
        }

        var current = ModelLibraryDirectory.Trim()
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var defaultRoot = AppInfo.DefaultModelDirectory
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(current, defaultRoot, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshLive2DModelLibrary()
    {
        try
        {
            var models = CreateModelLibrary().Scan();
            AvailableLive2DModels.Clear();
            AvailableLive2DModels.Add(BuiltInFallbackModel);
            foreach (var model in models)
            {
                AvailableLive2DModels.Add(model);
            }

            SyncSelectedLive2DModelFromDirectory();
        }
        catch (Exception ex)
        {
            _logger.Warn($"扫描 Live2D 模型库失败：{ex.Message}");
        }
    }

    private void SyncSelectedLive2DModelFromDirectory()
    {
        var directory = (ModelDirectory ?? "").Trim();
        Live2DModelInfo? match;
        if (directory.Length == 0)
        {
            match = BuiltInFallbackModel;
        }
        else
        {
            match = AvailableLive2DModels.FirstOrDefault(
                m => m.Directory.Length > 0 && string.Equals(m.Directory, directory, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                // 兼容旧配置：模型目录不在模型库中时，在下拉框显示“当前模型（外部目录）”，
                // 这样用户既能看到实际加载的模型，也可以随时切回内置兜底形象。
                var name = Path.GetFileName(directory);
                if (string.IsNullOrEmpty(name))
                {
                    name = directory;
                }

                match = new Live2DModelInfo($"当前模型（外部目录）：{name}", directory, string.Empty);
                AvailableLive2DModels.Add(match);
            }
        }

        _selectedLive2DModel = match;
        OnPropertyChanged(nameof(SelectedLive2DModel));
        DeleteLive2DModelCommand.RaiseCanExecuteChanged();
    }

    private bool CanDeleteLive2DModel()
    {
        var selected = SelectedLive2DModel;
        if (selected is null || selected.Directory.Length == 0)
        {
            return false;
        }

        try
        {
            return CreateModelLibrary().Contains(selected.Directory);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void ImportLive2DModel()
    {
        try
        {
            var dialog = new OpenFolderDialog
            {
                Title = "选择要导入的 Live2D 模型文件夹（包含 *.model3.json）",
                Multiselect = false
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var imported = CreateModelLibrary().Import(dialog.FolderName);
            RefreshLive2DModelLibrary();
            SelectImportedModel(imported);
        }
        catch (Exception ex)
        {
            _logger.Warn($"导入 Live2D 模型失败：{ex.Message}");
            Ui.Warn("导入模型失败：" + ex.Message, "Live2D 模型导入");
        }
    }

    private void ImportLive2DModelZip()
    {
        try
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择要导入的 Live2D 模型压缩包",
                Filter = "Live2D 模型包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var imported = CreateModelLibrary().ImportZip(dialog.FileName);
            RefreshLive2DModelLibrary();
            SelectImportedModel(imported);
        }
        catch (Exception ex)
        {
            _logger.Warn($"导入 Live2D 压缩包失败：{ex.Message}");
            Ui.Warn("导入模型压缩包失败：" + ex.Message, "Live2D 模型导入");
        }
    }

    private void SelectImportedModel(Live2DModelInfo imported)
    {
        var model = AvailableLive2DModels.FirstOrDefault(
            m => string.Equals(m.Directory, imported.Directory, StringComparison.OrdinalIgnoreCase));
        if (model is not null)
        {
            SelectedLive2DModel = model;
        }
        else
        {
            ModelDirectory = imported.Directory;
        }

        StatusText = $"已导入模型“{imported.Name}”，点击“保存”后生效。";
    }

    private void DeleteLive2DModel()
    {
        var selected = SelectedLive2DModel;
        if (selected is null || selected.Directory.Length == 0)
        {
            return;
        }

        if (!Ui.Confirm(
                $"确定从模型库删除“{selected.Name}”吗？\n只会删除模型库中的副本，原文件夹不受影响。",
                "删除 Live2D 模型",
                "删除",
                "取消"))
        {
            return;
        }

        try
        {
            CreateModelLibrary().Delete(selected.Directory);
            if (string.Equals((ModelDirectory ?? "").Trim(), selected.Directory, StringComparison.OrdinalIgnoreCase))
            {
                ModelDirectory = "";
            }

            RefreshLive2DModelLibrary();
            StatusText = $"已删除模型“{selected.Name}”，点击“保存”后生效。";
        }
        catch (Exception ex)
        {
            _logger.Warn($"删除 Live2D 模型失败：{ex.Message}");
            Ui.Warn("删除模型失败：" + ex.Message, "Live2D 模型库");
        }
    }

    private void OpenLive2DModelsFolder()
    {
        try
        {
            var library = CreateModelLibrary();
            Directory.CreateDirectory(library.RootDirectory);
            WindowHelper.OpenFolder(library.RootDirectory);
        }
        catch (Exception ex)
        {
            _logger.Warn($"打开模型库目录失败：{ex.Message}");
            Ui.Warn("无法打开模型库目录：" + ex.Message, "Live2D 模型库");
        }
    }

    private void SetLive2DVisible(bool visible)
    {
        try
        {
            if (visible)
            {
                _controller.Live2D.Show();
            }
            else
            {
                _controller.Live2D.Hide();
            }

            RefreshLive2DStatus();
        }
        catch (Exception ex)
        {
            _logger.Warn($"切换桌宠显示失败：{ex.Message}");
        }
    }

    private void ResetLive2DPosition()
    {
        try
        {
            _controller.ResetLive2DPosition();
            RefreshLive2DStatus();
            ShowSaveStatus("桌宠位置已重置");
        }
        catch (Exception ex)
        {
            _logger.Warn($"重置桌宠位置失败：{ex.Message}");
            Ui.Error("重置桌宠位置失败：" + ex.Message);
        }
    }

    private void RefreshLive2DStatus()
    {
        try
        {
            var live2D = _controller.Live2D;
            Live2DStatusText = live2D switch
            {
                { IsAvailable: true, IsVisible: true } => "Live2D 桌宠运行中。",
                { IsAvailable: true } => "Live2D 桌宠已隐藏。",
                { LastError: not null } => $"Live2D 不可用，已使用内置兜底形象：{live2D.LastError}",
                _ => "当前使用内置兜底形象（未加载 Live2D 模型）。"
            };
        }
        catch (Exception ex)
        {
            Live2DStatusText = "无法读取桌宠状态：" + ex.Message;
        }
    }

    // ==================== 朗读页逻辑 ====================

    private async Task TestTtsAsync()
    {
        if (_ttsEnabledTesting)
        {
            return;
        }

        _ttsEnabledTesting = true;
        TestTtsCommand.RaiseCanExecuteChanged();
        TtsStatusText = "正在朗读测试文本…";

        try
        {
            await _controller.SpeakAsync("你好，我是 VoxIME 声语，这是一段语音朗读测试。").ConfigureAwait(true);
            TtsStatusText = "朗读测试完成。";
        }
        catch (Exception ex)
        {
            TtsStatusText = "朗读失败：" + ex.Message;
            _logger.Warn($"朗读测试失败：{ex.Message}");
        }
        finally
        {
            _ttsEnabledTesting = false;
            TestTtsCommand.RaiseCanExecuteChanged();
        }
    }

    // ==================== 保存 ====================

    private void Save()
    {
        ValidateHotkeys();
        if (HasPushToTalkError || HasToggleHotkeyError || HasCancelHotkeyError)
        {
            StatusText = "热键设置有误，请先修正标红的输入框。";
            Ui.Warn("热键格式有误，请检查录音页中的热键设置。", "无法保存");
            SelectedPageIndex = (int)SettingsPage.Audio;
            return;
        }

        if (Providers.Count == 0)
        {
            Ui.Warn("至少需要保留一个识别服务。", "无法保存");
            return;
        }

        try
        {
            var config = CollectConfig();
            _configStore.Save(config);
            _controller.ApplyConfig(_configStore.Current);

            StatusText = "";
            if (!StartupRegistration.SetEnabled(config.Ui.StartWithWindows))
            {
                ShowSaveStatus("已保存（开机自启写入失败）");
            }
            else
            {
                ShowSaveStatus("已保存");
            }

            _working = ConfigJson.CloneAppConfig(_configStore.Current);
            Saved?.Invoke(this, _configStore.Current);
            RefreshLive2DStatus();
        }
        catch (Exception ex)
        {
            _logger.Error("保存设置失败", ex);
            StatusText = "保存失败：" + ex.Message;
            Ui.Error("保存设置失败：" + ex.Message);
        }
    }

    private AppConfig CollectConfig()
    {
        SaveExpressionMapForModel(_expressionMapModelDirectory);
        SaveMotionMapForModel(_expressionMapModelDirectory);
        var config = ConfigJson.CloneAppConfig(_working);

        config.ProviderProfiles = Providers.Select(p => p.ToProfile()).ToList();
        if (config.ProviderProfiles.Count == 0)
        {
            throw new InvalidOperationException("没有可用的识别服务配置");
        }

        // 兜底：UI 未携带明文凭据时，保留已保存的加密凭据，避免保存热键/桌宠等其它设置把 API Key 清空。
        var persistedProfiles = _configStore.Current.ProviderProfiles
            .ToDictionary(p => p.Id, StringComparer.Ordinal);
        foreach (var profile in config.ProviderProfiles)
        {
            if (!persistedProfiles.TryGetValue(profile.Id, out var persisted))
            {
                continue;
            }

            if (string.IsNullOrEmpty(profile.ApiKey))
            {
                profile.ApiKey = persisted.ApiKey;
                profile.ApiKeyProtected ??= persisted.ApiKeyProtected;
            }

            if (string.IsNullOrEmpty(profile.ApiSecret))
            {
                profile.ApiSecret = persisted.ApiSecret;
                profile.ApiSecretProtected ??= persisted.ApiSecretProtected;
            }

            if (string.IsNullOrEmpty(profile.SecretId))
            {
                profile.SecretId = persisted.SecretId;
                profile.SecretIdProtected ??= persisted.SecretIdProtected;
            }
        }

        var defaultId = _defaultProviderId;
        if (config.ProviderProfiles.All(p => p.Id != defaultId))
        {
            defaultId = config.ProviderProfiles.FirstOrDefault(p => p.Enabled)?.Id
                        ?? config.ProviderProfiles[0].Id;
        }

        config.ActiveProviderProfileId = defaultId;

        var push = NormalizeHotkeyRows(PushToTalkHotkeys, "按住说话", out _);
        var toggle = NormalizeHotkeyRows(ToggleHotkeys, "切换录音", out _);
        var cancel = NormalizeHotkeyRows(CancelHotkeys, "取消录音", out _);

        config.Hotkeys = new HotkeyConfig
        {
            PushToTalk = push.FirstOrDefault() ?? "",
            Toggle = toggle.FirstOrDefault() ?? "",
            Cancel = cancel.FirstOrDefault() ?? "",
            PushToTalkGestures = push,
            ToggleGestures = toggle,
            CancelGestures = cancel,
            EnablePushToTalk = EnablePushToTalk,
            EnableToggle = EnableToggle,
            DefaultMode = RecordingMode
        };

        config.Audio = new AudioConfig
        {
            DeviceId = string.IsNullOrWhiteSpace(SelectedAudioDevice?.Id) ? null : SelectedAudioDevice!.Id,
            SampleRate = 16000,
            MaxDurationSeconds = MaxDurationSeconds,
            AutoStopOnSilence = AutoStopOnSilence,
            SilenceTimeoutMs = SilenceTimeoutMs,
            MinSpeechMs = MinSpeechMs,
            VadThreshold = VadThreshold,
            PlayCues = PlayCues,
            AppContextMode = AppContextMode
        };

        config.Insertion = new InsertionConfig
        {
            Mode = InsertionMode,
            AutoInsert = AutoInsert,
            PreviewBeforeInsert = PreviewBeforeInsert,
            RestoreClipboard = RestoreClipboard,
            PasteDelayMs = PasteDelayMs,
            TrimWhitespace = TrimWhitespace,
            AppendSpace = AppendSpace,
            PressEnterAfterInsert = PressEnterAfterInsert,
            Replacements = Replacements
                .Where(r => !string.IsNullOrEmpty(r.From))
                .Select(r => new TextReplacement { From = r.From, To = r.To })
                .ToList()
        };

        var currentOverlay = _configStore.Current.Overlay;
        config.Overlay = new OverlayConfig
        {
            ShowHud = ShowHud,
            ShowLiveTranscript = ShowLiveTranscript,
            AutoInsert = OverlayAutoInsert,
            AutoHideMs = OverlayAutoHideMs,
            Left = currentOverlay.Left,
            Top = currentOverlay.Top
        };

        var invertMap = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _working.Live2D.EyeFollowInvertYByModel)
        {
            invertMap[pair.Key] = pair.Value;
        }

        var currentModelDirectory = (ModelDirectory ?? "").Trim();
        if (currentModelDirectory.Length > 0)
        {
            invertMap[currentModelDirectory] = EyeFollowInvertY;
        }

        config.Live2D = new Live2DConfig
        {
            Enabled = Live2DEnabled,
            ModelDirectory = string.IsNullOrWhiteSpace(ModelDirectory) ? null : ModelDirectory.Trim(),
            ModelLibraryDirectory = IsDefaultModelLibraryDirectory() ? null : ModelLibraryDirectory.Trim(),
            Width = Live2DWidth,
            Height = Live2DHeight,
            AutoFitWindow = AutoFitWindow,
            SnapToEdge = SnapToEdge,
            KeepAboveTaskbar = KeepAboveTaskbar,
            Scale = Live2DScale,
            ModelOffsetX = ModelOffsetX,
            ModelOffsetY = ModelOffsetY,
            Opacity = Live2DOpacity,
            ClickThrough = ClickThrough,
            AlwaysOnTop = AlwaysOnTop,
            EyeFollow = EyeFollow,
            EyeFollowFps = EyeFollowFps,
            EyeFollowInvertY = EyeFollowInvertY,
            EyeFollowInvertYByModel = invertMap,
            IdleMotion = IdleMotion,
            MouthSync = MouthSync,
            MouthSensitivity = MouthSensitivity,
            ShowBubble = ShowBubble,
            IdleMotionGroup = string.IsNullOrWhiteSpace(IdleMotionGroup) ? "Idle" : IdleMotionGroup.Trim(),
            ListeningExpression = NullIfEmpty(ListeningExpression),
            ThinkingExpression = NullIfEmpty(ThinkingExpression),
            SpeakingExpression = NullIfEmpty(SpeakingExpression),
            ErrorExpression = NullIfEmpty(ErrorExpression),
            ListeningMotionGroup = NullIfEmpty(ListeningMotionGroup),
            ThinkingMotionGroup = NullIfEmpty(ThinkingMotionGroup),
            SpeakingMotionGroup = NullIfEmpty(SpeakingMotionGroup),
            ErrorMotionGroup = NullIfEmpty(ErrorMotionGroup),
            ExpressionByModel = ConfigJson.Clone(_working.Live2D.ExpressionByModel),
            MotionByModel = ConfigJson.Clone(_working.Live2D.MotionByModel),
            FallbackAvatarTheme = string.IsNullOrWhiteSpace(FallbackAvatarTheme) ? "violet" : FallbackAvatarTheme.Trim(),
            Left = _configStore.Current.Live2D.Left,
            Top = _configStore.Current.Live2D.Top
        };

        config.Tts = new TtsConfig
        {
            Enabled = TtsEnabled,
            AutoSpeakResult = AutoSpeakResult,
            VoiceName = string.IsNullOrWhiteSpace(SelectedTtsVoice) ? null : SelectedTtsVoice,
            Rate = TtsRate,
            Volume = TtsVolume,
            MouthSync = TtsMouthSync
        };

        config.Ui = new UiConfig
        {
            Language = "zh-CN",
            StartWithWindows = StartWithWindows,
            MinimizeToTray = MinimizeToTray,
            ShowTrayIcon = ShowTrayIcon,
            ShowHistory = true,
            HistoryLimit = HistoryLimit,
            Theme = _selectedThemeMode,
            ColorScheme = _selectedColorSchemeId,
            Motion = _selectedMotionMode,
            Background = BuildBackgroundDraft(),
            ShowWelcomeOnStartup = ShowWelcomeOnStartup
        };

        config.Logging = new LoggingConfig
        {
            Level = SelectedLogLevel?.Value ?? "info",
            Directory = _working.Logging.Directory,
            MaxFileSizeKb = _working.Logging.MaxFileSizeKb,
            RetainDays = _working.Logging.RetainDays
        };

        return config;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void ExportConfig()
    {
        try
        {
            var config = CollectConfig();
            var dialog = new SaveFileDialog
            {
                Title = "导出 VoxIME 配置",
                Filter = "VoxIME 配置 (*.json)|*.json|所有文件 (*.*)|*.*",
                DefaultExt = ".json",
                AddExtension = true,
                FileName = $"VoxIME-config-{DateTime.Now:yyyyMMdd-HHmm}.json"
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var json = ConfigExchangeService.Export(config, ExportSecrets);
            File.WriteAllText(dialog.FileName, json);
            ShowSaveStatus("配置已导出");
            if (ExportSecrets)
            {
                Ui.Warn(
                    "导出文件包含 API Key 明文，请妥善保管，不要上传到公开位置。",
                    "配置已导出");
            }
            else
            {
                Ui.Info("配置已导出（未包含 API Key）。", "配置已导出");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("导出配置失败", ex);
            Ui.Error("导出配置失败：" + ex.Message);
        }
    }

    private void ImportConfig()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入 VoxIME 配置",
            Filter = "VoxIME 配置 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(dialog.FileName);
            var imported = ConfigExchangeService.Import(json);
            var serviceCount = imported.ProviderProfiles.Count;
            var hasSecrets = imported.ProviderProfiles.Any(p => p.HasAnySecret);
            var message =
                $"将用导入的配置替换当前全部设置（共 {serviceCount} 个识别服务）。\n\n" +
                "历史记录、日志和模型文件不受影响。\n" +
                (hasSecrets
                    ? "导入文件包含 API Key。"
                    : "导入文件不包含 API Key，导入后需要重新填写。") +
                "\n\n导入前会自动备份当前配置。";
            if (!Ui.Confirm(message, "导入配置", "导入", "取消"))
            {
                return;
            }

            BackupConfigBeforeImport();
            _configStore.Save(imported);
            _controller.ApplyConfig(_configStore.Current);
            StartupRegistration.SetEnabled(imported.Ui.StartWithWindows);
            InitializeFrom(_configStore.Current);
            _working = ConfigJson.CloneAppConfig(_configStore.Current);
            Saved?.Invoke(this, _configStore.Current);
            RefreshLive2DStatus();
            ShowSaveStatus("配置已导入");
            Ui.Info("配置已导入。建议重启 VoxIME，确保热键、音频设备和桌宠完全生效。", "导入成功");
        }
        catch (Exception ex)
        {
            _logger.Error("导入配置失败", ex);
            Ui.Error("导入配置失败：" + ex.Message);
        }
    }

    private void BackupConfigBeforeImport()
    {
        try
        {
            var path = _configStore.ConfigFilePath;
            if (!File.Exists(path))
            {
                return;
            }

            var backup = path + $".bak-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(path, backup, overwrite: false);
            _logger.Info($"导入配置前备份：{backup}");
        }
        catch (Exception ex)
        {
            _logger.Warn($"导入前备份配置失败：{ex.Message}");
        }
    }

    private void ResetToDefaults()
    {
        if (!Ui.Confirm("恢复所有设置为默认值？当前配置将被覆盖（历史记录不受影响）。", "恢复默认设置", "恢复", "取消"))
        {
            return;
        }

        try
        {
            _configStore.ResetToDefaults();
            _controller.ApplyConfig(_configStore.Current);
            InitializeFrom(_configStore.Current);
            StatusText = "已恢复默认设置。";
            Saved?.Invoke(this, _configStore.Current);
        }
        catch (Exception ex)
        {
            _logger.Error("恢复默认设置失败", ex);
            Ui.Error("恢复默认设置失败：" + ex.Message);
        }
    }

    private void RaiseProviderCommands()
    {
        RemoveProviderCommand.RaiseCanExecuteChanged();
        SetDefaultProviderCommand.RaiseCanExecuteChanged();
        TestProviderCommand.RaiseCanExecuteChanged();
        OpenProviderDocsCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _controller.MicrophoneLevelChanged -= OnMicrophoneLevelChanged;
        ThemeManager.ThemeChanged -= OnThemeManagerChanged;
        _backgroundPreviewTimer?.Stop();
        _backgroundPreviewTimer = null;
    }
}
