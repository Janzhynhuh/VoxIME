using System.IO;
using System.Net.Http;
using System.Windows;
using VoxIME.App.Infrastructure;
using VoxIME.App.Services;
using VoxIME.App.ViewModels;
using VoxIME.App.Views;
using VoxIME.Core;
using VoxIME.Core.Abstractions;
using VoxIME.Core.Asr;
using VoxIME.Core.Audio;
using VoxIME.Core.Configuration;
using VoxIME.Core.History;
using VoxIME.Core.Hotkeys;
using VoxIME.Core.Logging;
using VoxIME.Core.Services;
using VoxIME.Core.TextInsertion;
using VoxIME.Core.Tts;
using VoxIME.Live2D;

namespace VoxIME.App;

/// <summary>
/// 应用装配根：集中创建 / 持有 / 释放全部服务（顺序与 docs/CONTRACTS.md 第 5 节一致）。
/// 必须由 UI 线程创建（Live2D / WPF 窗口 / 托盘都要求 UI 线程）。
/// </summary>
public sealed class AppHost : IDisposable, IAsyncDisposable
{
    private readonly FileLogger _logger;
    private readonly List<IDisposable> _disposables = new();
    private readonly List<IAsyncDisposable> _asyncDisposables = new();

    private bool _started;
    private bool _disposed;

    private AppHost(
        FileLogger logger,
        JsonConfigStore configStore,
        HttpClient httpClient,
        IAsrProviderRegistry asrRegistry,
        IHistoryStore historyStore,
        IVoiceInputController controller,
        ILive2DController live2D)
    {
        _logger = logger;
        ConfigStore = configStore;
        HttpClient = httpClient;
        AsrRegistry = asrRegistry;
        History = historyStore;
        Controller = controller;
        Live2D = live2D;
    }

    // ==================== 对外暴露 ====================

    public IAppLogger Logger => _logger;
    public IConfigStore ConfigStore { get; }
    public IHistoryStore History { get; }
    public IAsrProviderRegistry AsrRegistry { get; }
    public IVoiceInputController Controller { get; }
    public ILive2DController Live2D { get; }
    public HttpClient HttpClient { get; }

    public TrayIconService? Tray { get; private set; }
    public HudWindow? Hud { get; private set; }
    public SettingsWindow? Settings { get; private set; }
    public HistoryWindow? HistoryWindow { get; private set; }

    public AppConfig Config => ConfigStore.Current;

    /// <summary>启动：控制器初始化 + HUD + 托盘。</summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _logger.Info($"=== {AppInfo.GetVersionLabel()} 启动 ===");
        _logger.Info($"配置：{ConfigStore.ConfigFilePath}");
        _logger.Info($"日志：{_logger.LogFilePath}");

        // 先套用用户主题/配色，再创建 HUD / 托盘等界面，避免窗口先以默认色出现再跳变。
        ThemeManager.Initialize(Config);

        if (Live2D is Live2DController live2DController)
        {
            live2DController.PositionChanged += OnLive2DPositionChanged;
        }

        Controller.Initialize();
        CreateHud();
        CreateTray();

        _logger.Info("应用初始化完成（托盘已就绪）");
    }

    /// <summary>首次启动是否显示欢迎引导（尚未配置真实服务时）。</summary>
    public bool ShouldShowWelcome()
    {
        try
        {
            var config = ConfigStore.Current;
            if (!config.Ui.ShowWelcomeOnStartup)
            {
                return false;
            }

            return config.ProviderProfiles.All(p =>
                string.Equals(p.TypeId, AsrProviderTypeIds.Mock, StringComparison.OrdinalIgnoreCase)
                || !p.Enabled);
        }
        catch (Exception ex)
        {
            _logger.Warn($"判断欢迎引导失败：{ex.Message}");
            return false;
        }
    }

    public void ShowWelcome()
    {
        try
        {
            var window = new WelcomeWindow(ConfigStore, Controller, AsrRegistry, Logger)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            var result = window.ShowDialog();
            _ = result;
            if (!string.IsNullOrEmpty(window.AddedProviderMessage))
            {
                Ui.Success(window.AddedProviderMessage, "已添加本地离线服务");
            }

            if (window.RequestedSettings)
            {
                OpenSettings();
            }
        }
        catch (Exception ex)
        {
            _logger.Error("显示欢迎引导失败", ex);
        }
    }

    // ==================== 窗口 ====================

    public void OpenSettings()
    {
        WindowHelper.RunOnUi(() =>
        {
            try
            {
                if (Settings is null)
                {
                    var viewModel = new SettingsViewModel(ConfigStore, Controller, AsrRegistry, History, Logger);
                    viewModel.Saved += OnSettingsSaved;
                    viewModel.HistoryRequested += (_, _) => OpenHistory();
                    viewModel.AboutRequested += (_, _) => OpenAbout();
                    viewModel.OverlayPositionResetRequested += (_, _) => Hud?.ResetPosition();
                    Settings = new SettingsWindow(viewModel, Logger);
                }

                ShowAndActivate(Settings);
            }
            catch (Exception ex)
            {
                _logger.Error("打开设置窗口失败", ex);
                Ui.Error("打开设置窗口失败：" + ex.Message);
            }
        });
    }

    public void OpenHistory()
    {
        WindowHelper.RunOnUi(() =>
        {
            try
            {
                if (HistoryWindow is null)
                {
                    var viewModel = new HistoryViewModel(History, Logger, Config.Ui.HistoryLimit);
                    HistoryWindow = new HistoryWindow(viewModel, Logger);
                }

                HistoryWindow.RefreshFromStore();
                ShowAndActivate(HistoryWindow);
            }
            catch (Exception ex)
            {
                _logger.Error("打开历史窗口失败", ex);
                Ui.Error("打开历史窗口失败：" + ex.Message);
            }
        });
    }

    public void OpenAbout()
    {
        WindowHelper.RunOnUi(() =>
        {
            try
            {
                var window = new AboutWindow();
                var owner = Application.Current?.Windows
                    .OfType<Window>()
                    .FirstOrDefault(w => w.IsActive);
                if (owner is not null)
                {
                    window.Owner = owner;
                    window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                }

                window.ShowDialog();
            }
            catch (Exception ex)
            {
                _logger.Error("打开关于窗口失败", ex);
            }
        });
    }

    public void OpenLogDirectory()
    {
        var directory = Path.GetDirectoryName(_logger.LogFilePath);
        WindowHelper.OpenFolder(string.IsNullOrEmpty(directory) ? AppInfo.DefaultLogDirectory : directory);
    }

    /// <summary>退出应用（托盘“退出”）。</summary>
    public void RequestExit()
    {
        WindowHelper.RunOnUi(() =>
        {
            _logger.Info("用户请求退出应用");
            Application.Current?.Shutdown();
        });
    }

    /// <summary>托盘气泡提示（后台线程安全）。</summary>
    public void ShowBalloon(string title, string text, bool isError = false) =>
        Tray?.ShowBalloon(title, text, isError);

    // ==================== 内部装配 ====================

    public static AppHost Create()
    {
        AppInfo.EnsureDirectories();

        // 先用静默日志读取一次配置，确定日志目录与等级（配置损坏时回退默认）。
        var bootstrapStore = new JsonConfigStore(logger: NullLogger.Instance);
        var bootstrapConfig = bootstrapStore.Load();

        var logDirectory = string.IsNullOrWhiteSpace(bootstrapConfig.Logging.Directory)
            ? AppInfo.DefaultLogDirectory
            : bootstrapConfig.Logging.Directory!;
        var maxLogBytes = Math.Clamp(bootstrapConfig.Logging.MaxFileSizeKb, 64, 10240) * 1024L;
        var logger = new FileLogger(
            logDirectory,
            FileLogger.ParseLevel(bootstrapConfig.Logging.Level),
            maxLogBytes,
            retainFiles: 5);

        var configStore = new JsonConfigStore(null, null, logger);
        var config = configStore.Load();
        logger.Info($"配置加载完成：{config.ProviderProfiles.Count} 个服务，默认服务 {config.ActiveProviderProfileId}");

        // 超时由每个 ProviderProfile.TimeoutSeconds 通过 CancellationTokenSource 控制
        // （ASR 适配器上报要求：HttpClient 不设全局超时，避免长录音/本地慢服务被提前取消）。
        var httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppInfo.EnglishName}/{AppInfo.Version}");

        var asrRegistry = new AsrProviderRegistry(httpClient, logger);
        var audio = new WasapiAudioCaptureService(logger);
        var beep = new SystemBeepPlayer();
        var clipboard = new Win32ClipboardService();
        var injector = new ClipboardTextInjector(clipboard, logger);
        var postProcessor = new DefaultTextPostProcessor();
        var hotkeys = new GlobalHotkeyService(logger);
        var tts = new SapiTtsService(logger);
        var live2D = new Live2DController(logger);
        var history = new JsonlHistoryStore(null, logger, Math.Clamp(config.Ui.HistoryLimit, 50, 2000));

        var controller = new VoiceInputController(
            configStore,
            audio,
            asrRegistry,
            injector,
            postProcessor,
            tts,
            hotkeys,
            live2D,
            beep,
            history,
            logger);

        var host = new AppHost(logger, configStore, httpClient, asrRegistry, history, controller, live2D);
        host._disposables.Add(tts);
        host._disposables.Add(hotkeys);
        host._disposables.Add(live2D);
        host._asyncDisposables.Add(audio);
        return host;
    }

    private void CreateHud()
    {
        try
        {
            var viewModel = new HudViewModel(
                Controller,
                ConfigStore,
                Logger,
                OpenSettings,
                () => Hud?.HideHud());

            Hud = new HudWindow(viewModel, Controller, ConfigStore, Logger)
            {
                SuppressShowing = !Config.Overlay.ShowHud
            };

            _logger.Debug("HUD 悬浮窗已创建");
        }
        catch (Exception ex)
        {
            _logger.Error("创建 HUD 失败", ex);
        }
    }

    private void CreateTray()
    {
        try
        {
            Tray = new TrayIconService(
                Controller,
                Logger,
                OpenSettings,
                OpenHistory,
                OpenAbout,
                OpenLogDirectory,
                RequestExit)
            {
                IsVisible = Config.Ui.ShowTrayIcon
            };
        }
        catch (Exception ex)
        {
            _logger.Error("创建托盘图标失败", ex);
        }
    }

    private void OnSettingsSaved(object? sender, AppConfig config)
    {
        try
        {
            // 主题可能随保存/导入/恢复默认变化；其它界面已经通过 DynamicResource 自动重绘。
            ThemeManager.ApplyConfig(config);

            Hud?.RefreshFromConfig();
            if (Hud is not null)
            {
                Hud.SuppressShowing = !config.Overlay.ShowHud;
            }

            if (Tray is not null)
            {
                Tray.IsVisible = config.Ui.ShowTrayIcon;
            }

            // 保存反馈由设置窗口在“保存”按钮旁内联显示，不再弹托盘气泡。
            _logger.Info("设置已保存并应用");
        }
        catch (Exception ex)
        {
            _logger.Warn($"应用设置到界面失败：{ex.Message}");
        }
    }

    private static void ShowAndActivate(Window window)
    {
        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
        window.Focus();
    }

    // ==================== 释放 ====================

    /// <summary>记住用户拖动后的桌宠位置；关闭自动贴边后下次启动恢复到该位置。</summary>
    private void OnLive2DPositionChanged(object? sender, Live2DPositionChangedEventArgs e)
    {
        try
        {
            var config = ConfigStore.Current;
            if (Math.Abs(config.Live2D.Left - e.Left) < 0.5 && Math.Abs(config.Live2D.Top - e.Top) < 0.5)
            {
                return;
            }

            config.Live2D.Left = e.Left;
            config.Live2D.Top = e.Top;
            ConfigStore.Save(config);
            _logger.Debug($"[Live2D] 保存桌宠位置：({e.Left:0},{e.Top:0})");
        }
        catch (Exception ex)
        {
            _logger.Warn($"保存桌宠位置失败：{ex.Message}");
        }
    }

    private void DisposeUi()
    {
        try
        {
            Tray?.Dispose();
            Tray = null;
        }
        catch (Exception ex)
        {
            _logger.Debug($"释放托盘失败：{ex.Message}");
        }

        try
        {
            Hud?.Dispose();
            Hud = null;
        }
        catch (Exception ex)
        {
            _logger.Debug($"释放 HUD 失败：{ex.Message}");
        }

        try
        {
            if (Settings is not null)
            {
                Settings.AllowClose = true;
                Settings.Close();
                Settings = null;
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"关闭设置窗口失败：{ex.Message}");
        }

        try
        {
            if (HistoryWindow is not null)
            {
                HistoryWindow.AllowClose = true;
                HistoryWindow.Close();
                HistoryWindow = null;
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"关闭历史窗口失败：{ex.Message}");
        }
    }

    private void DisposeServices()
    {
        foreach (var disposable in _disposables)
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception ex)
            {
                _logger.Debug($"释放服务失败：{ex.Message}");
            }
        }

        _disposables.Clear();

        foreach (var asyncDisposable in _asyncDisposables)
        {
            try
            {
                asyncDisposable.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                _logger.Debug($"释放异步服务失败：{ex.Message}");
            }
        }

        _asyncDisposables.Clear();

        try
        {
            HttpClient.Dispose();
        }
        catch (Exception)
        {
            // 忽略。
        }

        _logger.Info("=== VoxIME 退出 ===");
        try
        {
            _logger.Dispose();
        }
        catch (Exception)
        {
            // 忽略。
        }
    }

    private async Task DisposeCoreAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeUi();

        if (Live2D is Live2DController live2DController)
        {
            live2DController.PositionChanged -= OnLive2DPositionChanged;
        }

        try
        {
            await Controller.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warn($"释放语音控制器失败：{ex.Message}");
        }

        DisposeServices();
    }

    public async ValueTask DisposeAsync() => await DisposeCoreAsync().ConfigureAwait(false);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            DisposeCoreAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.Error("释放 AppHost 失败", ex);
        }
    }
}

