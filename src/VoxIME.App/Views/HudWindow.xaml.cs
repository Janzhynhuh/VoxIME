using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VoxIME.App.Infrastructure;
using VoxIME.App.ViewModels;
using VoxIME.Core.Abstractions;
using VoxIME.Core.Configuration;
using VoxIME.Core.Logging;

namespace VoxIME.App.Views;

/// <summary>
/// HUD 悬浮窗：透明置顶、底部居中、非交互区域鼠标穿透、可拖动、自动隐藏。
/// 所有 Core 事件都可能来自后台线程，统一 Dispatcher 封送。
/// </summary>
public partial class HudWindow : Window
{
    private readonly HudViewModel _viewModel;
    private readonly IVoiceInputController _controller;
    private readonly IConfigStore _configStore;
    private readonly IAppLogger _logger;
    private readonly DispatcherTimer _hideTimer;
    private readonly DispatcherTimer _levelTimer;

    private volatile float _pendingLevel;
    private bool _hasPosition;
    private bool _allowClose;
    private bool _isSourceHooked;

    public HudWindow(
        HudViewModel viewModel,
        IVoiceInputController controller,
        IConfigStore configStore,
        IAppLogger logger)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _controller = controller;
        _configStore = configStore;
        _logger = logger;

        DataContext = viewModel;

        _hideTimer = new DispatcherTimer();
        _hideTimer.Tick += OnHideTimerTick;

        _levelTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _levelTimer.Tick += OnLevelTimerTick;
        _levelTimer.Start();

        _controller.StateChanged += OnControllerStateChanged;
        _controller.MicrophoneLevelChanged += OnMicrophoneLevelChanged;
        _controller.TranscriptionReceived += OnTranscriptionReceived;
        _controller.ErrorOccurred += OnControllerError;
        _configStore.ConfigChanged += OnConfigChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    /// <summary>由 AppHost 在退出时调用（正常不允许用户关闭 HUD 窗口）。</summary>
    public void Dispose()
    {
        _allowClose = true;
        _controller.StateChanged -= OnControllerStateChanged;
        _controller.MicrophoneLevelChanged -= OnMicrophoneLevelChanged;
        _controller.TranscriptionReceived -= OnTranscriptionReceived;
        _controller.ErrorOccurred -= OnControllerError;
        _configStore.ConfigChanged -= OnConfigChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Dispose();

        _hideTimer.Stop();
        _levelTimer.Stop();

        if (_isSourceHooked && PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.RemoveHook(WndProc);
            _isSourceHooked = false;
        }

        try
        {
            Close();
        }
        catch (Exception ex)
        {
            _logger.Debug($"关闭 HUD 窗口失败：{ex.Message}");
        }
    }

    /// <summary>显示（不抢占焦点），并重置自动隐藏计时。</summary>
    public void ShowHud(bool activate = false)
    {
        try
        {
            if (SuppressShowing && !_viewModel.IsPreviewVisible)
            {
                return;
            }

            _hideTimer.Stop();
            if (!IsVisible)
            {
                Show();
                UpdateLayout();
            }

            if (!_hasPosition)
            {
                PositionWindow();
                _hasPosition = true;
            }

            if (activate)
            {
                Activate();
            }

            BeginAnimation(OpacityProperty,
                new DoubleAnimation(Opacity, 1, TimeSpan.FromMilliseconds(140)));
        }
        catch (Exception ex)
        {
            _logger.Debug($"显示 HUD 失败：{ex.Message}");
        }
    }

    /// <summary>是否抑制 HUD 自动显示（配置“显示悬浮提示窗”关闭时）。</summary>
    public bool SuppressShowing { get; set; }

    /// <summary>重置 HUD 到屏幕底部居中（设置页“重置位置”）。</summary>
    public void ResetPosition()
    {
        try
        {
            var config = _configStore.Current;
            config.Overlay.Left = -1;
            config.Overlay.Top = -1;
            _configStore.Save(config);
            _hasPosition = false;
            if (IsVisible)
            {
                UpdateLayout();
                PositionWindow();
                _hasPosition = true;
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"重置 HUD 位置失败：{ex.Message}");
        }
    }

    public void HideHud(bool immediate = false)
    {
        try
        {
            _hideTimer.Stop();
            if (!IsVisible)
            {
                return;
            }

            if (immediate)
            {
                BeginAnimation(OpacityProperty, null);
                Opacity = 0;
                Hide();
                return;
            }

            var animation = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(160));
            animation.Completed += (_, _) =>
            {
                if (Opacity <= 0.05 && !_viewModel.IsPreviewVisible)
                {
                    Hide();
                }
            };
            BeginAnimation(OpacityProperty, animation);
        }
        catch (Exception ex)
        {
            _logger.Debug($"隐藏 HUD 失败：{ex.Message}");
        }
    }

    public void ScheduleAutoHide(int? delayMs = null)
    {
        if (_viewModel.IsPreviewVisible)
        {
            _hideTimer.Stop();
            return;
        }

        _hideTimer.Stop();
        _hideTimer.Interval = TimeSpan.FromMilliseconds(
            Math.Clamp(delayMs ?? _configStore.Current.Overlay.AutoHideMs, 600, 30000));
        _hideTimer.Start();
    }

    /// <summary>设置页保存后刷新 HUD 提示文案。</summary>
    public void RefreshFromConfig() => _viewModel.UpdateHint();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // 注意：首次 Show 时才触发 Loaded，这里不能 Hide，否则 HUD 会被立刻隐藏。
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.TryEnableRoundedCorners(hwnd);
        }
        catch (Exception ex)
        {
            _logger.Debug($"HUD 初始化失败：{ex.Message}");
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.TrySetToolWindow(hwnd);
            if (PresentationSource.FromVisual(this) is HwndSource source)
            {
                source.AddHook(WndProc);
                _isSourceHooked = true;
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"HUD 窗口样式设置失败：{ex.Message}");
        }
    }

    private void PositionWindow()
    {
        try
        {
            var overlay = _configStore.Current.Overlay;
            var workArea = WindowHelper.GetPrimaryWorkArea();
            var width = ActualWidth > 10 ? ActualWidth : 640;
            var height = ActualHeight > 10 ? ActualHeight : 160;

            var virtualLeft = SystemParameters.VirtualScreenLeft;
            var virtualTop = SystemParameters.VirtualScreenTop;
            var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
            var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

            var left = overlay.Left;
            var top = overlay.Top;
            var valid = left >= virtualLeft && top >= virtualTop
                        && left + width <= virtualRight && top + height <= virtualBottom;

            if (!valid)
            {
                left = workArea.Left + (workArea.Width - width) / 2;
                top = workArea.Bottom - height - 42;
            }

            Left = Math.Clamp(left, virtualLeft, Math.Max(virtualLeft, virtualRight - width));
            Top = Math.Clamp(top, virtualTop, Math.Max(virtualTop, virtualBottom - height));
        }
        catch (Exception ex)
        {
            _logger.Warn($"HUD 定位失败：{ex.Message}");
        }
    }

    private void PersistPosition()
    {
        try
        {
            var config = _configStore.Current;
            if (Math.Abs(config.Overlay.Left - Left) < 0.5 && Math.Abs(config.Overlay.Top - Top) < 0.5)
            {
                return;
            }

            config.Overlay.Left = Left;
            config.Overlay.Top = Top;
            _configStore.Save(config);
        }
        catch (Exception ex)
        {
            _logger.Warn($"保存 HUD 位置失败：{ex.Message}");
        }
    }

    private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 拖动被取消。
        }

        PersistPosition();
    }

    private void OnHideTimerTick(object? sender, EventArgs e)
    {
        _hideTimer.Stop();
        if (!_viewModel.IsPreviewVisible)
        {
            HideHud();
        }
    }

    private void OnLevelTimerTick(object? sender, EventArgs e)
    {
        if (_viewModel.IsListening)
        {
            _viewModel.Level = _pendingLevel;
        }
        else if (_viewModel.Level > 0.001)
        {
            _viewModel.Level *= 0.72;
        }
    }

    private void OnMicrophoneLevelChanged(object? sender, AudioLevelEventArgs e) => _pendingLevel = e.Level;

    private void OnControllerStateChanged(object? sender, AppStateChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _viewModel.ApplyState(e.State, e.StatusText);
            switch (e.State)
            {
                case AppState.Listening:
                    ShowHud();
                    break;
                case AppState.Recognizing:
                    ShowHud();
                    _hideTimer.Stop();
                    break;
                case AppState.Speaking:
                    ShowHud();
                    ScheduleAutoHide();
                    break;
                case AppState.Error:
                    ShowHud();
                    ScheduleAutoHide(6000);
                    break;
                default:
                    if (!_viewModel.IsPreviewVisible)
                    {
                        ScheduleAutoHide();
                    }

                    break;
            }
        }));
    }

    private void OnControllerError(object? sender, string message)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _viewModel.ReportError(message);
            ShowHud();
            ScheduleAutoHide(6000);
        }));
    }

    private void OnTranscriptionReceived(object? sender, TranscriptionRecord record)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var text = record.Text?.Trim() ?? "";
            var insertionConfig = _configStore.Current.Insertion;
            if (insertionConfig.PreviewBeforeInsert && text.Length > 0)
            {
                _viewModel.ShowPreview(text);
                ShowHud(activate: true);
                _hideTimer.Stop();
                return;
            }

            if (text.Length > 0)
            {
                _viewModel.ApplyState(AppState.Idle,
                    text.Length > 28 ? "已插入：" + text[..28] + "…" : "已插入：" + text);
            }

            ShowHud();
            ScheduleAutoHide();
        }));
    }

    private void OnConfigChanged(object? sender, AppConfig config)
    {
        Dispatcher.BeginInvoke(new Action(_viewModel.UpdateHint));
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HudViewModel.IsPreviewVisible) && !_viewModel.IsPreviewVisible)
        {
            ScheduleAutoHide();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            HideHud();
        }
    }

    /// <summary>
    /// 命中测试：除按钮 / 文本框 / 拖动把手外，其余区域鼠标穿透到下层窗口。
    /// 预览模式同样按元素命中，避免透明边距挡住下方窗口点击。
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WmNcHitTest)
        {
            return IntPtr.Zero;
        }

        try
        {
            if (!NativeMethods.GetWindowRect(hwnd, out var rect))
            {
                return IntPtr.Zero;
            }

            var screenX = (short)(lParam.ToInt64() & 0xFFFF);
            var screenY = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
            var dpi = VisualTreeHelper.GetDpi(this);
            var point = new Point(
                (screenX - rect.Left) / dpi.DpiScaleX,
                (screenY - rect.Top) / dpi.DpiScaleY);

            var hit = VisualTreeHelper.HitTest(this, point);
            if (!IsInteractiveElement(hit?.VisualHit))
            {
                handled = true;
                return new IntPtr(NativeMethods.HtTransparent);
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"HUD 命中测试失败：{ex.Message}");
        }

        return IntPtr.Zero;
    }

    private static bool IsInteractiveElement(DependencyObject? element)
    {
        var current = element;
        var guard = 0;
        while (current is not null && current is not Window && guard++ < 64)
        {
            if (current is ButtonBase or TextBox or ScrollBar)
            {
                return true;
            }

            if (current is FrameworkElement { Tag: "hud-hit" })
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }
}
