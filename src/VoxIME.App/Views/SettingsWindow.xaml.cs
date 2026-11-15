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
    private bool _minimizing;
    private int _lastPageIndex;
    private WindowState _lastWindowState;

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
        _lastWindowState = WindowState;
    }

    /// <summary>退出流程中允许真正关闭。</summary>
    /// <summary>供设置页内的 Live2D 位置预览等子功能写日志。</summary>
    public IAppLogger Logger => _logger;

    public bool AllowClose { get; set; }

    /// <summary>托盘菜单切换点击穿透后，同步设置页草稿与界面，避免之后“保存”把托盘切换改回去。</summary>
    public void SyncClickThrough(bool enabled) => _viewModel.SyncClickThrough(enabled);

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

    /// <summary>
    /// 从最小化恢复（任务栏/托盘）：退场动画会把 RootBorder 留在透明、缩小的状态，
    /// 这里复位后再播入场动画；只在「最小化 → 非最小化」切换时触发。
    /// </summary>
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);

        var previous = _lastWindowState;
        _lastWindowState = WindowState;

        // 恢复目标可能是普通窗口，也可能是最大化窗口（WindowStyle=None 也允许被最大化）：
        // 两者都必须复位 RootBorder，否则内容会停留在退场动画留下的 Opacity=0。
        var restoring = previous == WindowState.Minimized && WindowState != WindowState.Minimized;
        if (!restoring)
        {
            return;
        }

        _minimizing = false;
        if (IsVisible)
        {
            PlayEnterMotion();
        }
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true)
        {
            return;
        }

        _hiding = false;
        _minimizing = false;

        // 隐藏期间被最小化时，AppHost 会先 Show 再把状态还原为 Normal：
        // 这里先不播，交给紧接着的 OnStateChanged，避免连续播放两次入场动画。
        if (WindowState == WindowState.Minimized)
        {
            return;
        }

        PlayEnterMotion();
    }

    /// <summary>窗口入场；先复位 RootBorder，清掉退场动画残留的 Opacity 与缩放。</summary>
    private void PlayEnterMotion()
    {
        Motion.Reset(RootBorder);
        Motion.PlayEnter(RootBorder, offsetY: 18, fromScale: 0.97, duration: TimeSpan.FromMilliseconds(280));
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

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        // _hiding 期间窗口正在播关闭退场动画：此时再起最小化动画会覆盖关闭动画的
        // Opacity 动画，导致关闭回调丢失、窗口停在半透明，直接忽略。
        if (_minimizing || _hiding || WindowState == WindowState.Minimized)
        {
            return;
        }

        // 动效关闭档：不做退场，立即最小化；恢复时 PlayEnterMotion 会复位 RootBorder，
        // 不会残留退场动画留下的 Opacity=0 / 缩小状态。
        if (!Motion.Enabled || !IsVisible)
        {
            WindowState = WindowState.Minimized;
            return;
        }

        _minimizing = true;
        Motion.PlayExit(RootBorder, offsetY: 6, toScale: 0.96, duration: TimeSpan.FromMilliseconds(160), completed: () =>
        {
            if (!_minimizing)
            {
                return;
            }

            _minimizing = false;

            // 退场期间窗口可能已被隐藏/关闭（Esc、关闭按钮或退出流程），此时不再最小化，
            // 避免下次打开时多触发一次状态切换与入场动画。
            if (!IsLoaded || !IsVisible || _hiding)
            {
                return;
            }

            WindowState = WindowState.Minimized;
        });
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => HideWithMotion();

    private void SaveButton_Click(object sender, RoutedEventArgs e) => _viewModel.SaveCommand.Execute(null);
}
