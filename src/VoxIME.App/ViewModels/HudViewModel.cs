using System.Windows;
using System.Windows.Media;
using VoxIME.App.Infrastructure;
using VoxIME.Core.Abstractions;
using VoxIME.Core.Appearance;
using VoxIME.Core.Configuration;
using VoxIME.Core.Logging;

namespace VoxIME.App.ViewModels;

/// <summary>HUD 悬浮窗状态：状态文案 / 状态色 / 麦克风电平 / 预览编辑。</summary>
public sealed class HudViewModel : ObservableObject
{
    // 状态色跟随当前调色板；切换主题/配色后由 ThemeManager.ThemeChanged 触发重新求值。
    private static Brush IdleBrush => ThemeBrushes.FindOrFallback("SubtleTextBrush", "#8A8AA0");
    private static Brush ListeningBrush => ThemeBrushes.FindOrFallback("AccentBrush", "#6B7280");
    private static Brush RecognizingBrush => ThemeBrushes.FindOrFallback("WarningBrush", "#FBBF24");
    private static Brush SpeakingBrush => ThemeBrushes.FindOrFallback("SuccessBrush", "#4ADE80");
    private static Brush ErrorBrush => ThemeBrushes.FindOrFallback("DangerBrush", "#FF6B6B");

    private readonly IVoiceInputController _controller;
    private readonly IConfigStore _configStore;
    private readonly IAppLogger _logger;

    private AppState _state = AppState.Idle;
    private string _stateText = "待机";
    private string _detailText = "";
    private string _errorText = "";
    private double _level;
    private bool _isPreviewVisible;
    private string _previewText = "";
    private string _hintText = "";

    public HudViewModel(
        IVoiceInputController controller,
        IConfigStore configStore,
        IAppLogger logger,
        Action openSettings,
        Action hideRequested)
    {
        _controller = controller;
        _configStore = configStore;
        _logger = logger;

        OpenSettingsCommand = new RelayCommand(openSettings);
        HideCommand = new RelayCommand(hideRequested);
        CancelCommand = new RelayCommand(() => _controller.Cancel());
        StopCommand = new AsyncRelayCommand(StopAsync);
        InsertCommand = new AsyncRelayCommand(InsertAsync);
        CopyCommand = new RelayCommand(CopyPreview);
        DismissPreviewCommand = new RelayCommand(ClearPreview);
        DismissErrorCommand = new RelayCommand(() => ErrorText = "");
        UpdateHint();
        ThemeManager.ThemeChanged += OnThemeChanged;
    }

    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand HideCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand InsertCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand DismissPreviewCommand { get; }
    public RelayCommand DismissErrorCommand { get; }

    public AppState State => _state;

    public string StateText
    {
        get => _stateText;
        private set => SetProperty(ref _stateText, value);
    }

    public string DetailText
    {
        get => _detailText;
        private set => SetProperty(ref _detailText, value);
    }

    public string HintText
    {
        get => _hintText;
        private set => SetProperty(ref _hintText, value);
    }

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorText);

    public double Level
    {
        get => _level;
        set => SetProperty(ref _level, Math.Clamp(value, 0, 1));
    }

    public bool IsPreviewVisible
    {
        get => _isPreviewVisible;
        private set
        {
            if (SetProperty(ref _isPreviewVisible, value))
            {
                OnPropertyChanged(nameof(IsNormalMode));
            }
        }
    }

    public bool IsNormalMode => !_isPreviewVisible;

    public string PreviewText
    {
        get => _previewText;
        set => SetProperty(ref _previewText, value);
    }

    public string GlyphText => _state switch
    {
        AppState.Listening => "\uE720",   // 麦克风
        AppState.Recognizing => "\uE895", // 同步/处理中
        AppState.Speaking => "\uE767",    // 音量
        AppState.Error => "\uE783",       // 错误
        AppState.Disabled => "\uE769",    // 暂停
        _ => "\uE7C8"                     // 待机（圆形）
    };

    public Brush StatusBrush => _state switch
    {
        AppState.Listening => ListeningBrush,
        AppState.Recognizing => RecognizingBrush,
        AppState.Speaking => SpeakingBrush,
        AppState.Error => ErrorBrush,
        _ => IdleBrush
    };

    public bool IsListening => _state == AppState.Listening;
    public bool IsPreviewMode => _isPreviewVisible;
    public bool ShowStopButton => _state == AppState.Listening;
    public bool ShowCancelButton => _state is AppState.Listening or AppState.Recognizing or AppState.Speaking;
    public bool ShowLevelBar => _state == AppState.Listening;

    public void ApplyState(AppState state, string? statusText)
    {
        _state = state;
        StateText = state switch
        {
            AppState.Listening => "聆听中…",
            AppState.Recognizing => "识别中…",
            AppState.Speaking => "朗读中",
            AppState.Error => "出错了",
            AppState.Disabled => "已暂停",
            _ => "待机"
        };

        DetailText = string.IsNullOrWhiteSpace(statusText) ? "" : statusText!;
        if (state != AppState.Error)
        {
            ErrorText = "";
        }

        if (state != AppState.Listening)
        {
            Level = 0;
        }

        OnPropertiesChanged(nameof(GlyphText), nameof(StatusBrush), nameof(IsListening),
            nameof(IsPreviewMode), nameof(ShowStopButton), nameof(ShowCancelButton), nameof(ShowLevelBar));

        if (state != AppState.Listening)
        {
            UpdateHint();
        }
    }

    public void ShowPreview(string text)
    {
        PreviewText = text;
        IsPreviewVisible = true;
        OnPropertiesChanged(nameof(IsPreviewMode), nameof(IsNormalMode));
        UpdateHint();
    }

    public void ClearPreview()
    {
        IsPreviewVisible = false;
        PreviewText = "";
        OnPropertiesChanged(nameof(IsPreviewMode), nameof(IsNormalMode));
        UpdateHint();
    }

    public void ReportError(string message)
    {
        ErrorText = message;
        _state = AppState.Error;
        StateText = "出错了";
        OnPropertiesChanged(nameof(GlyphText), nameof(StatusBrush), nameof(ShowSuccessText));
        UpdateHint();
    }

    public bool ShowSuccessText { get; private set; }

    /// <summary>根据当前配置拼一句操作提示。</summary>
    public void UpdateHint()
    {
        try
        {
            var hotkeys = _configStore.Current.Hotkeys;
            var push = string.IsNullOrWhiteSpace(hotkeys.PushToTalk) ? "Ctrl+Alt" : hotkeys.PushToTalk;
            var toggle = string.IsNullOrWhiteSpace(hotkeys.Toggle) ? "Ctrl+Alt+M" : hotkeys.Toggle;
            var cancel = string.IsNullOrWhiteSpace(hotkeys.Cancel) ? "Esc" : hotkeys.Cancel;

            HintText = _state switch
            {
                AppState.Listening => hotkeys.DefaultMode == RecordingMode.PushToTalk
                    ? $"松开 {push} 结束 · {cancel} 取消"
                    : $"再次按 {toggle} 结束 · {cancel} 取消",
                AppState.Recognizing => "正在识别，请稍候…",
                AppState.Speaking => "正在朗读识别结果",
                AppState.Error => "",
                AppState.Disabled => "热键已暂停，可在托盘中恢复",
                _ => _isPreviewVisible ? "" : $"按住 {push} 说话，按 {toggle} 切换录音"
            };
        }
        catch (Exception ex)
        {
            _logger.Warn($"HUD 提示文案生成失败：{ex.Message}");
            HintText = "";
        }
    }

    private async Task StopAsync()
    {
        try
        {
            await _controller.StopAndTranscribeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.Warn($"HUD 停止录音失败：{ex.Message}");
            ReportError("停止录音失败：" + ex.Message);
        }
    }

    private async Task InsertAsync()
    {
        var text = PreviewText?.Trim() ?? "";
        if (text.Length == 0)
        {
            return;
        }

        try
        {
            var result = await _controller.InsertTextAsync(text).ConfigureAwait(true);
            if (result.Success)
            {
                ClearPreview();
            }
            else
            {
                ReportError(result.Error ?? "插入失败");
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"HUD 插入文本失败：{ex.Message}");
            ReportError("插入失败：" + ex.Message);
        }
    }

    private void CopyPreview()
    {
        var text = PreviewText ?? "";
        if (text.Length == 0)
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
            StateText = "已复制到剪贴板";
        }
        catch (Exception ex)
        {
            _logger.Warn($"复制失败：{ex.Message}");
            ReportError("复制失败，剪贴板可能被占用");
        }
    }

    /// <summary>由 HudWindow 释放时调用：解除主题订阅。</summary>
    public void Dispose()
    {
        ThemeManager.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, ResolvedTheme theme)
    {
        // 调色板已替换：状态色是自绘（非 DynamicResource）属性，需要重新通知绑定求值。
        OnPropertyChanged(nameof(StatusBrush));
    }
}
