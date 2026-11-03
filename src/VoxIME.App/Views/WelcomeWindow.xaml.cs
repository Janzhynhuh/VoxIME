using System.Windows;
using System.Windows.Input;
using VoxIME.App.Infrastructure;
using VoxIME.Core;
using VoxIME.Core.Abstractions;
using VoxIME.Core.Configuration;
using VoxIME.Core.Logging;

namespace VoxIME.App.Views;

/// <summary>
/// 首次启动欢迎窗口：说明用法，并可一键添加本地离线服务或前往设置。
/// </summary>
public partial class WelcomeWindow : Window
{
    private readonly IConfigStore _configStore;
    private readonly IVoiceInputController _controller;
    private readonly IAsrProviderRegistry _registry;
    private readonly IAppLogger _logger;
    private bool _closing;
    private bool _allowClose;

    public WelcomeWindow(
        IConfigStore configStore,
        IVoiceInputController controller,
        IAsrProviderRegistry registry,
        IAppLogger logger)
    {
        InitializeComponent();
        _configStore = configStore;
        _controller = controller;
        _registry = registry;
        _logger = logger;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        CloseWithMotion();
    }

    private void CloseWithMotion()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        Motion.PlayExit(RootBorder, offsetY: 10, toScale: 0.98, duration: TimeSpan.FromMilliseconds(150), completed: () =>
        {
            _allowClose = true;
            Close();
        });
    }

    /// <summary>用户是否希望打开设置窗口。</summary>
    public bool RequestedSettings { get; private set; }

    /// <summary>添加本地服务成功后的提示文案（由 AppHost 在欢迎窗关闭后弹出，避免嵌套模态）。</summary>
    public string? AddedProviderMessage { get; private set; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Motion.PlayEnter(RootBorder, offsetY: 18, fromScale: 0.97, duration: TimeSpan.FromMilliseconds(280));
        try
        {
            var config = _configStore.Current;
            var push = string.IsNullOrWhiteSpace(config.Hotkeys.PushToTalk)
                ? AppInfo.DefaultHotkeyPushToTalk
                : config.Hotkeys.PushToTalk;
            var toggle = string.IsNullOrWhiteSpace(config.Hotkeys.Toggle)
                ? AppInfo.DefaultHotkeyToggle
                : config.Hotkeys.Toggle;

            HotkeyHint.Text = config.Hotkeys.DefaultMode == RecordingMode.PushToTalk
                ? $"默认按住 {push} 说话，松开即识别；也可以按 {toggle} 开始/结束。所有热键都能在设置中修改。"
                : $"默认按 {toggle} 开始、再按一次结束；按住 {push} 也可说话。所有热键都能在设置中修改。";
        }
        catch (Exception ex)
        {
            _logger.Warn($"欢迎窗初始化提示失败：{ex.Message}");
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        RequestedSettings = true;
        MarkWelcomeSeen();
        CloseWithMotion();
    }

    private void AddLocalButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var config = _configStore.Current;
            var descriptor = _registry?.FindDescriptor(AsrProviderTypeIds.LocalCli);
            var preset = descriptor?.Presets.FirstOrDefault();

            var profile = preset?.Template.Clone() ?? new ProviderProfile
            {
                TypeId = AsrProviderTypeIds.LocalCli,
                Name = "本地离线识别",
                BaseUrl = "",
                Model = "",
                Language = "zh",
                Command = "whisper-cli",
                CommandArguments = "-m \"{model}\" -f \"{wav}\" -l {lang} -otxt -of \"{out}\"",
                OutputMode = "file",
                OutputFileTemplate = "{out}.txt",
                TimeoutSeconds = 120
            };

            profile.Id = Guid.NewGuid().ToString("N")[..8];
            profile.Name = string.IsNullOrWhiteSpace(profile.Name) ? "本地离线识别" : profile.Name;
            profile.Enabled = true;

            config.ProviderProfiles.Add(profile);
            config.ActiveProviderProfileId = profile.Id;
            config.Ui.ShowWelcomeOnStartup = false;
            _configStore.Save(config);
            _controller.ApplyConfig(config);

            AddedProviderMessage =
                $"已添加“{profile.Name}”并设为默认服务。\n请在设置的服务页中填写本地识别程序的命令行路径。";
            RequestedSettings = true;
            CloseWithMotion();
        }
        catch (Exception ex)
        {
            _logger.Error("添加本地离线服务失败", ex);
            Ui.Error("添加本地离线服务失败：" + ex.Message);
        }
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        MarkWelcomeSeen();
        CloseWithMotion();
    }

    private void MarkWelcomeSeen(bool save = true)
    {
        try
        {
            if (!save)
            {
                return;
            }

            var config = _configStore.Current;
            if (!config.Ui.ShowWelcomeOnStartup)
            {
                return;
            }

            config.Ui.ShowWelcomeOnStartup = false;
            _configStore.Save(config);
        }
        catch (Exception ex)
        {
            _logger.Warn($"保存欢迎窗状态失败：{ex.Message}");
        }
    }
}
