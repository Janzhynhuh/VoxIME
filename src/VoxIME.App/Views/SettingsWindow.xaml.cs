using System.Windows;
using System.Windows.Input;
using VoxIME.App.Infrastructure;
using VoxIME.App.ViewModels;
using VoxIME.Core.Logging;

namespace VoxIME.App.Views;

/// <summary>设置窗口（单例，关闭时隐藏；App 退出时真正关闭）。</summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private readonly IAppLogger _logger;

    public SettingsWindow(SettingsViewModel viewModel, IAppLogger logger)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _logger = logger;
        DataContext = viewModel;
        WindowHelper.EnableDrag(this, TitleBar);
        Closing += OnClosing;
    }

    /// <summary>退出流程中允许真正关闭。</summary>
    public bool AllowClose { get; set; }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        // 每次打开都刷新一次麦克风/语音包等环境信息。
        try
        {
            _viewModel.RefreshDevices();
        }
        catch (Exception ex)
        {
            _logger.Debug($"刷新设置页设备信息失败：{ex.Message}");
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _viewModel.Dispose();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void SaveButton_Click(object sender, RoutedEventArgs e) => _viewModel.SaveCommand.Execute(null);
}
