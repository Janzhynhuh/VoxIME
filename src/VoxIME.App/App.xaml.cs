using System.IO;
using System.Windows;
using System.Windows.Threading;
using VoxIME.App.Infrastructure;
using VoxIME.App.ViewModels;
using VoxIME.App.Views;
using VoxIME.Core;
using VoxIME.Core.Configuration;

namespace VoxIME.App;

/// <summary>
/// WPF 应用入口：单实例守卫 → 全局异常处理 → AppHost 装配 → 托盘/HUD/欢迎引导。
/// 无 StartupUri，所有窗口由 AppHost 管理；ShutdownMode=OnExplicitShutdown（关闭窗口不退出）。
/// </summary>
public partial class App : System.Windows.Application
{
    private AppHost? _host;
    private SingleInstanceGuard? _guard;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 尽早注入默认调色板：单实例提示框、欢迎引导等窗口都依赖 DynamicResource 主题键，
        // 不能等到配置加载完成才注入（AppHost.Create 里会再套用用户配置）。
        ThemeManager.ApplyConfig(AppConfig.CreateDefault());

        // 下拉框打开时会捕获鼠标滚轮，Window 层补一条兜底路径（见 SmoothScroll）。
        SmoothScroll.InitializeWindowWheelFallback();

        var arguments = e.Args ?? Array.Empty<string>();
        var settingsMode = HasArgument(arguments, "--settings");
        var trayMode = HasArgument(arguments, "--tray");

        _guard = new SingleInstanceGuard();
        if (!_guard.TryAcquire())
        {
            // 已有实例在运行：--settings 时直接唤起它的设置窗口，否则提示后退出。
            if (settingsMode)
            {
                SingleInstanceGuard.SignalExistingInstance();
            }
            else
            {
                MessageDialog.ShowMessage(
                    "VoxIME 已经在运行了，请在系统托盘中找到它。",
                    "VoxIME 声语",
                    MessageKind.Info);
            }

            Shutdown();
            return;
        }

        _guard.ActivateSettingsRequested += OnActivateSettingsRequested;
        RegisterGlobalExceptionHandlers();

        try
        {
            _host = AppHost.Create();
            _host.Start();
        }
        catch (Exception ex)
        {
            ShowStartupFailure(ex);
            return;
        }

        if (settingsMode)
        {
            _host.OpenSettings();
        }
        else if (HasArgument(arguments, "--e2e"))
        {
            RunE2EMode();
        }
        else if (HasArgument(arguments, "--selftest"))
        {
            RunSelfTest();
        }
        else if (!trayMode && _host.ShouldShowWelcome())
        {
            _host.ShowWelcome();
        }
    }

    /// <summary>隐藏的端到端自检入口（--e2e），结果写入 %TEMP%\voxime-e2e-result.json。</summary>
    private async void RunE2EMode()
    {
        var host = _host;
        if (host is null)
        {
            Shutdown(1);
            return;
        }

        int exitCode;
        try
        {
            exitCode = await E2ERunner.RunAsync(host).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            host.Logger.Error("[E2E] 运行失败", ex);
            exitCode = 3;
        }

        Shutdown(exitCode);
    }

    /// <summary>
    /// 隐藏的自检开关（--selftest）：依次构造并显示每个窗口后退出，用于开发期验证 XAML 资源与绑定。
    /// 结果写入日志（SELFTEST 行）。
    /// </summary>
    private void RunSelfTest()
    {
        var host = _host;
        var logger = host?.Logger;
        var failures = 0;

        void Check(string name, Action action)
        {
            try
            {
                action();
                logger?.Info($"SELFTEST {name}: OK");
            }
            catch (Exception ex)
            {
                failures++;
                logger?.Error($"SELFTEST {name}: FAIL", ex);
            }
        }

        if (host is null)
        {
            Shutdown(1);
            return;
        }

        Check("WelcomeWindow", () =>
        {
            var window = new WelcomeWindow(host.ConfigStore, host.Controller, host.AsrRegistry, host.Logger);
            window.Show();
            window.UpdateLayout();
            window.Close();
        });

        Check("SettingsWindow", () =>
        {
            var viewModel = new SettingsViewModel(
                host.ConfigStore, host.Controller, host.AsrRegistry, host.History, host.Logger);
            foreach (var index in Enumerable.Range(0, (int)SettingsPage.Appearance + 1))
            {
                viewModel.SelectedPageIndex = index;
            }

            var window = new SettingsWindow(viewModel, host.Logger);
            window.Show();
            window.UpdateLayout();
            window.AllowClose = true;
            window.Close();
        });

        Check("HistoryWindow", () =>
        {
            var viewModel = new HistoryViewModel(host.History, host.Logger, host.Config.Ui.HistoryLimit);
            var window = new HistoryWindow(viewModel, host.Logger);
            window.Show();
            window.UpdateLayout();
            window.AllowClose = true;
            window.Close();
        });

        Check("AboutWindow", () =>
        {
            var window = new AboutWindow();
            window.Show();
            window.UpdateLayout();
            window.Close();
        });

        Check("HudWindow", () =>
        {
            if (host.Hud is null)
            {
                throw new InvalidOperationException("HUD 未创建");
            }

            host.Hud.ShowHud();
            host.Hud.UpdateLayout();
            host.Hud.HideHud(immediate: true);
        });

        logger?.Info($"SELFTEST 完成：{failures} 个失败");
        Shutdown(failures == 0 ? 0 : 2);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _host?.Dispose();
        }
        catch (Exception ex)
        {
            LogFatal(ex);
        }

        _host = null;

        if (_guard is not null)
        {
            _guard.ActivateSettingsRequested -= OnActivateSettingsRequested;
            try
            {
                _guard.Dispose();
            }
            catch (Exception ex)
            {
                LogFatal(ex);
            }

            _guard = null;
        }

        base.OnExit(e);
    }

    // ==================== 单实例 ====================

    private void OnActivateSettingsRequested(object? sender, EventArgs e)
    {
        WindowHelper.RunOnUi(() =>
        {
            try
            {
                _host?.OpenSettings();
            }
            catch (Exception ex)
            {
                LogFatal(ex);
            }
        });
    }

    // ==================== 全局异常 ====================

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        try
        {
            _host?.Logger.Error("UI 线程未处理异常", e.Exception);
            _host?.ShowBalloon("VoxIME 出现异常（已记录日志）", e.Exception.Message, isError: true);
        }
        catch (Exception)
        {
            LogFatal(e.Exception);
        }
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            LogFatal(exception);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        try
        {
            _host?.Logger.Error("后台任务未观察异常", e.Exception);
        }
        catch (Exception)
        {
            LogFatal(e.Exception);
        }
    }

    private void ShowStartupFailure(Exception exception)
    {
        LogFatal(exception);
        try
        {
            _host?.Logger.Error("应用启动失败", exception);
        }
        catch (Exception)
        {
            // 忽略。
        }

        try
        {
            MessageDialog.ShowMessage(
                "VoxIME 启动失败：\n" + exception.Message +
                "\n\n错误详情已写入日志目录：\n" + AppInfo.DefaultLogDirectory,
                "启动失败",
                MessageKind.Error);
        }
        catch (Exception)
        {
            // 连对话框都失败时直接退出。
        }

        Shutdown();
    }

    /// <summary>兜底日志：即使 FileLogger 未创建成功也能留下记录。</summary>
    private static void LogFatal(Exception exception)
    {
        try
        {
            AppInfo.EnsureDirectories();
            var path = Path.Combine(AppInfo.DefaultLogDirectory, "startup-error.log");
            File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // 最后兜底失败则忽略（可能连目录都不可写）。
        }
    }

    private static bool HasArgument(string[] arguments, string name) =>
        arguments.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
}

