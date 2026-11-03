using System.ComponentModel;
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
    private bool _hiding;
    private int _lastPageIndex;

    public SettingsWindow(SettingsViewModel viewModel, IAppLogger logger)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _logger = logger;
        DataContext = viewModel;
        WindowHelper.EnableDrag(this, TitleBar);
        Closing += OnClosing;
        IsVisibleChanged += OnIsVisibleChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _lastPageIndex = _viewModel.SelectedPageIndex;
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

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            _hiding = false;
            Motion.PlayEnter(RootBorder, offsetY: 18, fromScale: 0.97, duration: TimeSpan.FromMilliseconds(280));
        }
    }

    /// <summary>切换设置页时做一次「滑入 + 淡入」，方向跟随导航顺序。</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingsViewModel.CurrentPage))
        {
            return;
        }

        var direction = _viewModel.SelectedPageIndex >= _lastPageIndex ? 1 : -1;
        _lastPageIndex = _viewModel.SelectedPageIndex;
        Motion.PlaySlideIn(PageHost, fromX: 12 * direction, duration: TimeSpan.FromMilliseconds(200));
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            HideWithMotion();
            return;
        }

        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Dispose();
    }

    private void HideWithMotion()
    {
        if (_hiding || !IsVisible)
        {
            return;
        }

        _hiding = true;
        Motion.PlayExit(RootBorder, offsetY: 10, toScale: 0.98, duration: TimeSpan.FromMilliseconds(160), completed: () =>
        {
            Hide();
            _hiding = false;
        });
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
            HideWithMotion();
            e.Handled = true;
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => HideWithMotion();

    private void SaveButton_Click(object sender, RoutedEventArgs e) => _viewModel.SaveCommand.Execute(null);
}
