using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using VoxIME.Core.Abstractions;
using VoxIME.Core.Configuration;
using VoxIME.Core.Logging;
using VoxIME.Core.Services;

namespace VoxIME.Live2D;

/// <summary>
/// Live2D 桌宠控制器（<see cref="ILive2DController"/> 实现）。
///
/// 行为概要：
/// - 在 WPF Dispatcher 上创建透明、无边框、置顶、不占任务栏的桌宠窗口；
/// - 通过 WebView2CompositionControl 渲染 Cubism 3/4 模型（真正透明合成）；
/// - 无模型 / 无 WebView2 Runtime / 模型加载失败 / 握手超时 → 自动降级为 WPF 矢量兜底形象，
///   并通过 <see cref="ErrorOccurred"/> 与 <see cref="LastError"/> 报告原因；
/// - 任意线程调用安全：状态/口型写入字段后由 UI 线程 30Hz 合并下发；眼动由独立后台线程按
///   配置帧率（5–300Hz）采样光标，再在渲染帧下发给模型，避免高频定时器挤占 UI 线程；
/// - 环境变量 <c>VOXIME_LIVE2D_DISABLE_WEBVIEW=1</c> 可强制走兜底形象（开发/验收用）。
/// </summary>
public sealed class Live2DController : ILive2DController
{
    private const int FlushIntervalMilliseconds = 33;
    private static readonly TimeSpan PageHandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WebViewInitTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan ModelLoadTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan BubbleAutoHideDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ErrorDedupeWindow = TimeSpan.FromSeconds(2);

    private readonly IAppLogger _logger;
    private readonly object _sync = new();
    private readonly object _gazeSync = new();
    private readonly Dispatcher? _dispatcher;
    private readonly DispatcherTimer? _flushTimer;

    private Live2DConfig _config = new();
    private PetWindow? _window;
    private Live2DWebHost? _webHost;
    private CancellationTokenSource? _contentCts;
    private TaskCompletionSource<bool>? _pageReadySource;
    private TaskCompletionSource<string>? _modelLoadedSource;
    private double _lastModelWidth;
    private double _lastModelHeight;

    private Live2DState _state = Live2DState.Idle;
    private double _mouthValue;
    private double _gazeX;
    private double _gazeY;
    private string? _bubbleText;
    private DateTime _bubbleSetAt = DateTime.MinValue;
    private string? _lastRaisedError;
    private DateTime _lastRaisedErrorAt = DateTime.MinValue;

    // 眼动跟随：后台线程按配置帧率（5–300Hz）采样全局光标，UI 渲染帧再下发给 JS，避免高频定时器挤占 UI 线程。
    private Thread? _gazeThread;
    private volatile bool _gazeThreadStop;
    private volatile bool _gazeActive;
    private volatile int _gazeFps = 30;
    private long _gazeWindowHandle;
    private double _gazeSampleX;
    private double _gazeSampleY;
    private bool _gazeSampleDirty;
    private bool _renderingHooked;

    private Live2DState _lastSentState = (Live2DState)(-1);
    private double _lastSentMouth = double.NaN;
    private double _lastSentGazeX = double.NaN;
    private double _lastSentGazeY = double.NaN;
    private bool _lastSentGazeInvertY;
    private string? _lastSentBubble;
    private bool _lastSentBubbleVisible;

    private bool _disposed;
    private bool _initialized;
    private bool _visible;
    private volatile bool _isAvailable;
    private bool _readyRaised;
    private bool _contentInitialized;
    private bool _contentInitializing;
    private bool _fallbackActive;
    private string? _lastError;
    private double? _userLeft;
    private double? _userTop;
    private bool _systemParametersSubscribed;
    private bool _diagnosticSystemEventsSubscribed;
    private bool _physicalSnapRetryPending;

    public Live2DController(IAppLogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // 只跟随 WPF Application 的 Dispatcher。没有 Application 时（控制台/测试/被错误地在
        // 非 WPF 进程使用）安全降级：不创建任何 UI，也不抛异常，通过 LastError 明确说明原因。
        var application = Application.Current;
        if (application is not null)
        {
            _dispatcher = application.Dispatcher;
        }

        if (_dispatcher is not null)
        {
            _flushTimer = new DispatcherTimer(DispatcherPriority.Render, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(FlushIntervalMilliseconds)
            };
            _flushTimer.Tick += OnFlushTick;
        }
        else
        {
            _logger.Warn("[Live2D] 当前进程没有 WPF Application/Dispatcher，桌宠将保持不可用（安全降级）。");
        }
    }

    /// <inheritdoc />
    public bool IsAvailable => _isAvailable;

    /// <inheritdoc />
    public bool IsVisible => _visible;

    /// <inheritdoc />
    public Live2DState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    /// <inheritdoc />
    public string? LastError
    {
        get
        {
            lock (_sync)
            {
                return _lastError;
            }
        }
    }

    /// <summary>当前是否显示 WPF 兜底形象（WebView2/模型不可用或加载中）。</summary>
    public bool UsingFallbackAvatar
    {
        get
        {
            lock (_sync)
            {
                return _fallbackActive;
            }
        }
    }

    /// <summary>用户拖动窗口后触发（附加能力，宿主可据此持久化 Left/Top）。</summary>
    public event EventHandler<Live2DPositionChangedEventArgs>? PositionChanged;

    /// <summary>
    /// 读取桌宠窗口的屏幕物理像素矩形（开发期截图/自动化验证用）。
    /// 必须在 UI 线程调用；窗口尚未创建时返回 false。
    /// </summary>
    public bool TryGetWindowPixelBounds(out int left, out int top, out int width, out int height)
    {
        left = top = width = height = 0;
        var window = _window;
        var dispatcher = _dispatcher;
        if (window is null || dispatcher is null || !dispatcher.CheckAccess())
        {
            return false;
        }

        var handle = window.Handle;
        return handle != IntPtr.Zero && WindowInterop.TryGetWindowRect(handle, out left, out top, out width, out height);
    }

    /// <inheritdoc />
    public event EventHandler? Ready;

    /// <inheritdoc />
    public event EventHandler<string>? ErrorOccurred;

    /// <inheritdoc />
    public event EventHandler<Live2DModelSizeEventArgs>? ModelSizeAvailable;

    /// <inheritdoc />
    public void Initialize(Live2DConfig config)
    {
        if (_disposed)
        {
            return;
        }

        var snapshot = CloneConfig(config);
        lock (_sync)
        {
            _config = snapshot;
        }

        _logger.Info(
            $"[Live2D] Initialize：Enabled={snapshot.Enabled}, ModelDirectory={snapshot.ModelDirectory ?? "(未配置)"}, " +
            $"{snapshot.Width}x{snapshot.Height}, Opacity={snapshot.Opacity}, Scale={snapshot.Scale}, " +
            $"ClickThrough={snapshot.ClickThrough}, AlwaysOnTop={snapshot.AlwaysOnTop}");

        SubscribeDisplaySettings();
        RunOnUiThread(InitializeCore, "Initialize");
    }

    private void SubscribeDisplaySettings()
    {
        if (_systemParametersSubscribed)
        {
            return;
        }

        try
        {
            SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
            _systemParametersSubscribed = true;
        }
        catch (Exception ex)
        {
            _logger.Debug($"[Live2D] 订阅显示器配置变化失败：{ex.Message}");
        }

        try
        {
            // 临时诊断：确认 RDP/远控切换是否触发 WM_DISPLAYCHANGE。
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDiagnosticDisplaySettingsChanged;
            _diagnosticSystemEventsSubscribed = true;
        }
        catch (Exception ex)
        {
            _logger.Debug($"[Live2D][诊断] 订阅 SystemEvents 失败：{ex.Message}");
        }
    }

    private void UnsubscribeDisplaySettings()
    {
        if (_systemParametersSubscribed)
        {
            try
            {
                SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
            }
            catch (Exception)
            {
                // 进程退出阶段忽略。
            }
            finally
            {
                _systemParametersSubscribed = false;
            }
        }

        if (_diagnosticSystemEventsSubscribed)
        {
            try
            {
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDiagnosticDisplaySettingsChanged;
            }
            catch (Exception)
            {
                // 进程退出阶段忽略。
            }
            finally
            {
                _diagnosticSystemEventsSubscribed = false;
            }
        }
    }

    private void OnDiagnosticDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        RunOnUiThread(() =>
        {
            if (_window is not null)
            {
                _logger.Info($"[Live2D][诊断] SystemEvents.DisplaySettingsChanged：{_window.GetDisplayDiagnostics()}");
            }
        }, "DiagnosticDisplaySettingsChanged");
    }

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (e.PropertyName != nameof(SystemParameters.WorkArea)
            && e.PropertyName != nameof(SystemParameters.VirtualScreenLeft)
            && e.PropertyName != nameof(SystemParameters.VirtualScreenTop)
            && e.PropertyName != nameof(SystemParameters.VirtualScreenWidth)
            && e.PropertyName != nameof(SystemParameters.VirtualScreenHeight)
            && e.PropertyName != nameof(SystemParameters.PrimaryScreenWidth)
            && e.PropertyName != nameof(SystemParameters.PrimaryScreenHeight))
        {
            return;
        }

        RunOnUiThread(
            () => EnsureWindowVisibleOnDisplayChange("SystemParameters:" + e.PropertyName),
            "DisplaySettingsChanged");
    }

    private void EnsureWindowVisibleOnDisplayChange(string source)
    {
        if (_disposed || _window is null)
        {
            return;
        }

        var visible = _window.IsVisibleOnCurrentDesktop();
        var needsRepair = _window.NeedsDisplayGeometryRepair();
        _logger.Info(
            $"[Live2D][诊断] 显示变化({source}) visibleOnVirtualDesktop={visible} " +
            $"needsGeometryRepair={needsRepair} " + _window.GetDisplayDiagnostics());

        var config = GetConfig();
        if (config.SnapToEdge && (needsRepair || !visible)
            && _window.SnapToBottomEdgePhysical(
                config.Width,
                config.Height,
                config.KeepAboveTaskbar))
        {
            _userLeft = _window.Left;
            _userTop = _window.Top;
            _logger.Info(
                $"[Live2D] 显示器/DPI 变化，按物理像素重新吸附到底部：" +
                $"({_window.Left:0.#},{_window.Top:0.#})，实际={_window.ActualWidth:0.#}x{_window.ActualHeight:0.#}");
            PositionChanged?.Invoke(this, new Live2DPositionChangedEventArgs(_window.Left, _window.Top));
            SchedulePhysicalSnapRetry();
            return;
        }

        if (visible)
        {
            return;
        }

        _logger.Info("[Live2D] 显示器布局变化，桌宠窗口已不在可见区域，自动回位。");
        ResetWindowToVisiblePosition(config);
    }

    /// <summary>
    /// DPI 刚切换时 GetDpiForWindow 可能还是旧值；第一次物理吸附会按旧缩放算。
    /// 延迟到 WPF 完成 DPI 更新后，再按最终缩放补一次。
    /// </summary>
    private void SchedulePhysicalSnapRetry()
    {
        if (_physicalSnapRetryPending)
        {
            return;
        }

        _physicalSnapRetryPending = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(350).ConfigureAwait(false);
                RunOnUiThread(() =>
                {
                    _physicalSnapRetryPending = false;
                    if (_disposed || _window is null)
                    {
                        return;
                    }

                    var config = GetConfig();
                    if (!config.SnapToEdge
                        || !_window.SnapToBottomEdgePhysical(
                            config.Width,
                            config.Height,
                            config.KeepAboveTaskbar))
                    {
                        return;
                    }

                    _userLeft = _window.Left;
                    _userTop = _window.Top;
                    _logger.Info(
                        $"[Live2D] DPI 稳定后再次吸附到底部：" +
                        $"({_window.Left:0.#},{_window.Top:0.#})，实际={_window.ActualWidth:0.#}x{_window.ActualHeight:0.#}");
                    PositionChanged?.Invoke(
                        this,
                        new Live2DPositionChangedEventArgs(_window.Left, _window.Top));
                }, "RetryPhysicalSnap");
            }
            catch (Exception ex)
            {
                _logger.Debug($"[Live2D] 延迟物理吸附失败：{ex.Message}");
            }
        });
    }

    /// <summary>把窗口移到当前屏的可见位置，并通知上层持久化新坐标。</summary>
    private void ResetWindowToVisiblePosition(Live2DConfig config)
    {
        if (_window is null)
        {
            return;
        }

        _userLeft = null;
        _userTop = null;
        _window.ApplyGeometry(
            config.Width,
            config.Height,
            -1,
            -1,
            config.SnapToEdge,
            config.KeepAboveTaskbar,
            hasSavedPosition: false,
            config.Opacity,
            config.AlwaysOnTop,
            config.ClickThrough);

        PositionChanged?.Invoke(this, new Live2DPositionChangedEventArgs(_window.Left, _window.Top));
    }

    /// <inheritdoc />
    public void ApplyConfig(Live2DConfig config)
    {
        if (_disposed)
        {
            return;
        }

        var snapshot = CloneConfig(config);
        lock (_sync)
        {
            _config = snapshot;
        }

        RunOnUiThread(() => ApplyConfigCore(snapshot), "ApplyConfig");
    }

    /// <inheritdoc />
    public void Show() => RunOnUiThread(ShowCore, "Show");

    /// <inheritdoc />
    public void Hide() => RunOnUiThread(HideCore, "Hide");

    /// <inheritdoc />
    public void SetState(Live2DState state)
    {
        lock (_sync)
        {
            if (_state == state)
            {
                return;
            }

            _state = state;
        }

        _logger.Debug($"[Live2D] 状态 -> {state}");
    }

    /// <inheritdoc />
    public void PreviewMotion(string? motionGroup)
    {
        var group = (motionGroup ?? "").Trim();
        if (group.Length == 0)
        {
            return;
        }

        RunOnUiThread(() =>
        {
            if (_disposed || _webHost is null)
            {
                return;
            }

            try
            {
                if (!_visible)
                {
                    ShowCore();
                }

                _webHost.SendPreviewMotion(group);
                _logger.Info($"[Live2D] 预览动作：{group}");
            }
            catch (Exception ex)
            {
                HandleError($"预览动作失败：{group}", ex);
            }
        }, "PreviewMotion");
    }

    /// <inheritdoc />
    public void PreviewExpression(string? expression)
    {
        var name = (expression ?? "").Trim();
        if (name.Length == 0)
        {
            return;
        }

        RunOnUiThread(() =>
        {
            if (_disposed || _webHost is null)
            {
                return;
            }

            try
            {
                if (!_visible)
                {
                    ShowCore();
                }

                _webHost.SendPreviewExpression(name);
                _logger.Info($"[Live2D] 预览表情：{name}");
            }
            catch (Exception ex)
            {
                HandleError($"预览表情失败：{name}", ex);
            }
        }, "PreviewExpression");
    }

    /// <inheritdoc />
    public void SetMouthOpen(double value)
    {
        var clamped = double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);
        lock (_sync)
        {
            _mouthValue = clamped;
        }
    }

    /// <inheritdoc />
    public void SetGaze(double normalizedX, double normalizedY)
    {
        var x = double.IsNaN(normalizedX) ? 0 : Math.Clamp(normalizedX, -1, 1);
        var y = double.IsNaN(normalizedY) ? 0 : Math.Clamp(normalizedY, -1, 1);
        lock (_sync)
        {
            _gazeX = x;
            _gazeY = y;
        }
    }

    /// <inheritdoc />
    public void SetBubbleText(string? text)
    {
        lock (_sync)
        {
            _bubbleText = string.IsNullOrWhiteSpace(text) ? null : text!.Trim();
            _bubbleSetAt = DateTime.UtcNow;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _logger.Info("[Live2D] Dispose：释放桌宠资源。");
        UnsubscribeDisplaySettings();

        var dispatcher = _dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        try
        {
            if (dispatcher.CheckAccess())
            {
                CleanupCore();
            }
            else if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
            {
                dispatcher.Invoke(CleanupCore, DispatcherPriority.Normal);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Live2D] Dispose 时清理 UI 资源失败（进程可能正在退出）：{ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // UI 线程：初始化
    // ------------------------------------------------------------------
    private void InitializeCore()
    {
        if (_disposed)
        {
            return;
        }

        _initialized = true;
        try
        {
            EnsureWindow();
            ApplyWindowConfiguration();
            _flushTimer?.Start();
            HookRendering();
            StartGazeThread();

            var config = GetConfig();
            ApplyGazeConfig(config);
            if (config.Enabled)
            {
                _window!.Show();
                _visible = true;
                StartContentInitialization();
            }
            else
            {
                // 未启用：先创建窗口句柄但不显示；后续 Show() 时再初始化内容。
                _window!.Show();
                _window.Hide();
                _visible = false;
                _logger.Info("[Live2D] 配置 Enabled=false，桌宠窗口保持隐藏。");
            }

            UpdateGazeWindowHandle();
            UpdateGazeState();
        }
        catch (Exception ex)
        {
            HandleError("初始化桌宠窗口失败", ex);
        }
    }

    private void EnsureWindow()
    {
        if (_window is not null)
        {
            return;
        }

        _window = new PetWindow();
        _window.PositionChanged += OnWindowPositionChanged;
        _window.Closed += (_, _) => _logger.Debug("[Live2D] 桌宠窗口已关闭。");
        _logger.Info("[Live2D] 桌宠窗口已创建。");
    }

    private void ApplyWindowConfiguration()
    {
        if (_window is null)
        {
            return;
        }

        var config = GetConfig();
        var left = config.Left;
        var top = config.Top;
        // 默认哨兵是 (-1,-1)；负坐标在多显示器/贴顶场景是合法位置，不能因为 top<0 就当成“未保存”。
        var hasSavedPosition = !(Math.Abs(left + 1) < 0.01 && Math.Abs(top + 1) < 0.01);
        if (!hasSavedPosition && _userLeft.HasValue && _userTop.HasValue)
        {
            // 用户拖动过：在配置未显式给坐标时保留用户位置
            left = _userLeft.Value;
            top = _userTop.Value;
            hasSavedPosition = true;
        }

        _window.ApplyGeometry(
            config.Width,
            config.Height,
            left,
            top,
            config.SnapToEdge,
            config.KeepAboveTaskbar,
            hasSavedPosition,
            config.Opacity,
            config.AlwaysOnTop,
            config.ClickThrough);

        // 分辨率/多屏布局变化后，旧坐标可能完全落到当前桌面外（表现为“看不到桌宠”）。
        if (!_window.IsVisibleOnCurrentDesktop())
        {
            _logger.Info("[Live2D] 桌宠窗口不在当前桌面可见区域，自动重置到默认位置。");
            ResetWindowToVisiblePosition(config);
        }

        _window.SetAvatarTransform(config.Scale, config.ModelOffsetX, config.ModelOffsetY);
        _window.Avatar.Theme = string.IsNullOrWhiteSpace(config.FallbackAvatarTheme)
            ? "violet"
            : config.FallbackAvatarTheme;
        _logger.Info(
            $"[Live2D] 窗口几何：{_window.Width}x{_window.Height} @ ({_window.Left:0},{_window.Top:0}) " +
            $"Opacity={_window.Opacity:0.00} Topmost={_window.Topmost} ClickThrough={config.ClickThrough}");
    }

    private void ApplyConfigCore(Live2DConfig config)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            // 配置里是 (-1,-1) 表示位置已被重置：同时丢掉本进程内缓存的拖动位置，
            // 否则 ApplyWindowConfiguration 会继续使用旧坐标，重置按钮看起来无效。
            if (Math.Abs(config.Left + 1) < 0.01 && Math.Abs(config.Top + 1) < 0.01)
            {
                _userLeft = null;
                _userTop = null;
            }

            var previousDirectory = GetConfig().ModelDirectory;
            ApplyWindowConfiguration();
            ApplyGazeConfig(config);

            if (_window is not null)
            {
                _window.Bubble.Hide();
                _lastSentBubble = null;
                _lastSentBubbleVisible = false;
            }

            if (!_initialized)
            {
                _logger.Debug("[Live2D] ApplyConfig 早于 Initialize：保存配置，等待 Initialize。");
                return;
            }

            if (!config.Enabled)
            {
                HideCore();
                DisposeWebHost();
                _contentInitialized = false;
                CancelContentToken();
                return;
            }

            var directoryChanged = !string.Equals(
                previousDirectory?.Trim(),
                config.ModelDirectory?.Trim(),
                StringComparison.OrdinalIgnoreCase);

            if (directoryChanged)
            {
                _logger.Info("[Live2D] 模型目录变化，重新加载桌宠内容。");
                DisposeWebHost();
                _contentInitialized = false;
                CancelContentToken();
                EnsureWindow();
                _window!.Show();
                _window.SetFallbackVisible(true);
                _visible = true;
                StartContentInitialization();
            }
            else
            {
                SendConfigToPage();
                if (!_contentInitialized && _visible)
                {
                    StartContentInitialization();
                }
            }

            UpdateGazeWindowHandle();
            UpdateGazeState();
        }
        catch (Exception ex)
        {
            HandleError("应用桌宠配置失败", ex);
        }
    }

    // ------------------------------------------------------------------
    // UI 线程：显示/隐藏
    // ------------------------------------------------------------------
    private void ShowCore()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            EnsureWindow();
            ApplyWindowConfiguration();
            _window!.Show();
            _visible = true;
            _flushTimer?.Start();
            UpdateGazeWindowHandle();
            HookRendering();
            StartGazeThread();
            UpdateGazeState();
            if (!_contentInitialized)
            {
                // 注意：内容初始化（WebView2 EnsureCoreWebView2Async）期间绝对不能折叠 WebView 控件，
                // 否则初始化会永久挂起（WPF 分层窗口下没有有效渲染目标）。
                // 兜底形象位于 WebView 下层，直接显示即可，等模型加载成功后再隐藏。
                _window.SetAvatarVisible(true);
                StartContentInitialization();
            }
        }
        catch (Exception ex)
        {
            HandleError("显示桌宠失败", ex);
        }
    }

    private void HideCore()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _window?.Hide();
            _visible = false;
            _gazeActive = false;
            _flushTimer?.Stop();
            _logger.Info("[Live2D] 桌宠已隐藏。");
        }
        catch (Exception ex)
        {
            HandleError("隐藏桌宠失败", ex);
        }
    }

    // ------------------------------------------------------------------
    // 内容初始化：WebView2 + Live2D 模型，失败则降级 WPF 兜底形象
    // ------------------------------------------------------------------
    private async void StartContentInitialization()
    {
        if (_disposed || !_initialized || _contentInitializing)
        {
            return;
        }

        _contentInitializing = true;
        CancelContentToken();
        var cts = new CancellationTokenSource();
        _contentCts = cts;

        try
        {
            var config = GetConfig();
            var modelDirectory = config.ModelDirectory?.Trim();
            var modelFile = ResolveModelFile(modelDirectory, out var resolveError);
            if (!string.IsNullOrEmpty(resolveError))
            {
                RaiseError(resolveError!);
            }

            if (modelFile is null)
            {
                // 未配置模型：这是正常用法（使用内置兜底形象），不算错误。
                ShowFallbackAvatar(force: true);
                MarkReady(modelDirectory is null ? "内置兜底形象（未配置模型）" : "内置兜底形象（模型不可用）");
                return;
            }

            if (string.Equals(Environment.GetEnvironmentVariable("VOXIME_LIVE2D_DISABLE_WEBVIEW"), "1", StringComparison.Ordinal))
            {
                _logger.Warn("[Live2D] 环境变量 VOXIME_LIVE2D_DISABLE_WEBVIEW=1，跳过 WebView2 直接使用兜底形象。");
                RaiseError("已按环境变量禁用 WebView2，使用内置兜底形象。");
                ShowFallbackAvatar(force: true);
                MarkReady("兜底形象（WebView2 被禁用）");
                return;
            }

            var webRoot = Live2DWebHost.ResolveWebRoot();
            if (webRoot is null)
            {
                RaiseError("找不到 Live2D 网页资源目录（live2d/wwwroot），已降级为内置兜底形象。");
                ShowFallbackAvatar(force: true);
                MarkReady("兜底形象（缺少网页资源）");
                return;
            }

            // 模型加载前先显示兜底形象，避免窗口空白。
            ShowFallbackAvatar(force: true);

            var webHost = new Live2DWebHost(_logger);
            _webHost = webHost;
            webHost.PageReady += OnPageReady;
            webHost.ModelLoaded += OnModelLoaded;
            webHost.PageError += OnPageError;
            _window?.SetWebView(webHost.View);

            _logger.Info("[Live2D] 开始初始化 WebView2 宿主…");
            var initializeTask = webHost.InitializeAsync(webRoot, modelDirectory);
            var initCompleted = await Task.WhenAny(initializeTask, Task.Delay(WebViewInitTimeout, cts.Token))
                .ConfigureAwait(true);
            if (cts.IsCancellationRequested || _disposed)
            {
                return;
            }

            if (initCompleted != initializeTask)
            {
                RaiseError($"WebView2 初始化超时（{WebViewInitTimeout.TotalSeconds:0} 秒），已降级为内置兜底形象。");
                DisposeWebHost();
                ShowFallbackAvatar(force: true);
                MarkReady("兜底形象（WebView2 初始化超时）");
                return;
            }

            var initialized = await initializeTask.ConfigureAwait(true);
            if (cts.IsCancellationRequested || _disposed)
            {
                return;
            }

            if (!initialized)
            {
                RaiseError("WebView2 初始化失败（可能未安装 WebView2 Runtime），已降级为内置兜底形象。");
                DisposeWebHost();
                ShowFallbackAvatar(force: true);
                MarkReady("兜底形象（WebView2 不可用）");
                return;
            }

            _pageReadySource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pageReady = await WaitForTaskAsync(_pageReadySource.Task, PageHandshakeTimeout, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested || _disposed)
            {
                return;
            }

            if (!pageReady)
            {
                RaiseError($"Live2D 页面握手超时（{PageHandshakeTimeout.TotalSeconds:0} 秒），已降级为内置兜底形象。");
                DisposeWebHost();
                ShowFallbackAvatar(force: true);
                MarkReady("兜底形象（页面握手超时）");
                return;
            }

            SendConfigToPage();
            _modelLoadedSource = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var modelUrl = Live2DWebHost.BuildModelUrl(modelDirectory!, modelFile);
            _logger.Info($"[Live2D] 请求加载模型：{modelUrl}");
            _webHost.SendLoadModel(modelUrl);

            var loadedName = await WaitForTaskAsync(_modelLoadedSource.Task, ModelLoadTimeout, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested || _disposed)
            {
                return;
            }

            if (loadedName is null)
            {
                RaiseError($"Live2D 模型加载超时/失败（{Path.GetFileName(modelFile)}），已降级为内置兜底形象。");
                DisposeWebHost();
                ShowFallbackAvatar(force: true);
                MarkReady("兜底形象（模型加载失败）");
                return;
            }

            // 成功：显示 WebView2，隐藏兜底形象
            _window?.SetWebViewVisible(true);
            lock (_sync)
            {
                _fallbackActive = false;
                _lastError = null;
            }

            _lastSentState = (Live2DState)(-1);
            _lastSentMouth = double.NaN;
            _lastSentGazeX = double.NaN;
            _lastSentGazeY = double.NaN;
            SendConfigToPage();
            _logger.Info($"[Live2D] Live2D 模型加载成功：{loadedName}");
            MarkReady($"Live2D 模型：{loadedName}");
            RaiseModelSizeAvailable(loadedName);
        }
        catch (OperationCanceledException)
        {
            _logger.Debug("[Live2D] 内容初始化被取消。");
        }
        catch (Exception ex)
        {
            HandleError("初始化 Live2D 桌宠内容失败", ex);
            try
            {
                DisposeWebHost();
                ShowFallbackAvatar(force: true);
                MarkReady("兜底形象（初始化异常）");
            }
            catch (Exception inner)
            {
                _logger.Error("[Live2D] 降级为兜底形象时再次失败。", inner);
            }
        }
        finally
        {
            _contentInitializing = false;
            _contentInitialized = true;
        }
    }

    private static string? ResolveModelFile(string? modelDirectory, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(modelDirectory))
        {
            return null;
        }

        string directory;
        try
        {
            directory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(modelDirectory.Trim().Trim('"')));
        }
        catch (Exception)
        {
            error = $"模型目录路径无效：{modelDirectory}";
            return null;
        }

        if (!Directory.Exists(directory))
        {
            error = $"模型目录不存在：{directory}（已降级为内置兜底形象）";
            return null;
        }

        try
        {
            var candidates = Directory.EnumerateFiles(directory, "*.model3.json", SearchOption.AllDirectories)
                .OrderBy(path => path.Count(ch => ch == Path.DirectorySeparatorChar))
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (candidates.Length == 0)
            {
                error = $"模型目录中未找到 *.model3.json：{directory}（已降级为内置兜底形象）";
                return null;
            }

            return candidates[0];
        }
        catch (Exception ex)
        {
            error = $"扫描模型目录失败：{ex.Message}（已降级为内置兜底形象）";
            return null;
        }
    }

    private void SendConfigToPage()
    {
        var config = GetConfig();
        var expressionMap = config.ResolveExpressionMap(config.ModelDirectory);
        var expressions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["listening"] = expressionMap.Listening,
            ["thinking"] = expressionMap.Thinking,
            ["speaking"] = expressionMap.Speaking,
            ["error"] = expressionMap.Error
        };
        var motionMap = config.ResolveMotionMap(config.ModelDirectory);
        var motions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["listening"] = motionMap.Listening,
            ["thinking"] = motionMap.Thinking,
            ["speaking"] = motionMap.Speaking,
            ["error"] = motionMap.Error
        };
        var idleMotionGroup = string.IsNullOrWhiteSpace(motionMap.IdleGroup) ? "Idle" : motionMap.IdleGroup!.Trim();
        // 同步 Exp3/Animation 等未登记进 model3.json 的资源，页面加载模型后注入引擎。
        var modelAssets = Live2DModelAssetCatalog.Load(config.ModelDirectory);
        if (modelAssets.Expressions.Count > 0 || modelAssets.Motions.Count > 0)
        {
            _logger.Info(
                $"[Live2D] 模型资源扫描：{modelAssets.Expressions.Count} 个表情 / {modelAssets.Motions.Count} 个动作" +
                $"（{Path.GetFileName(modelAssets.ModelJsonPath) ?? "未找到 model3.json"}）");
        }

        _webHost?.SendConfig(
            config.EyeFollow,
            config.IdleMotion,
            config.MouthSync,
            config.Scale,
            config.ModelOffsetX,
            config.ModelOffsetY,
            idleMotionGroup,
            config.ShowBubble,
            expressions,
            motions,
            modelAssets);
        // 缩放不再改 WebView2 ZoomFactor：ZoomFactor 会让页面 viewport 重排，JS 重新适配后视觉尺寸
        // 几乎不变且会被拉伸发糊。统一交给页面在 canvas 内缩放，并随缩放提高渲染分辨率。
        _webHost?.SetZoom(1.0);

        if (_window is not null)
        {
            _window.SetAvatarTransform(config.Scale, config.ModelOffsetX, config.ModelOffsetY);
        }
    }

    // ------------------------------------------------------------------
    // WebView2 事件
    // ------------------------------------------------------------------
    private void OnPageReady(object? sender, EventArgs e) => _pageReadySource?.TrySetResult(true);

    private void OnModelLoaded(object? sender, Live2DModelSizeEventArgs e)
    {
        _lastModelWidth = e.Width;
        _lastModelHeight = e.Height;
        _modelLoadedSource?.TrySetResult(e.Name);
    }

    /// <summary>模型加载成功后上报尺寸（供按模型比例自适应窗口）。</summary>
    private void RaiseModelSizeAvailable(string name)
    {
        if (_disposed || !double.IsFinite(_lastModelWidth) || !double.IsFinite(_lastModelHeight)
            || _lastModelWidth <= 0 || _lastModelHeight <= 0)
        {
            return;
        }

        try
        {
            ModelSizeAvailable?.Invoke(this, new Live2DModelSizeEventArgs(name, _lastModelWidth, _lastModelHeight));
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Live2D] 模型尺寸事件处理失败：{ex.Message}");
        }
    }

    private void OnPageError(object? sender, string message)
    {
        var source = _modelLoadedSource;
        if (source is not null && !source.Task.IsCompleted)
        {
            source.TrySetException(new InvalidOperationException(message));
            return;
        }

        // 模型已加载后页面崩溃/报错：降级回兜底形象，避免窗口变成空白。
        RaiseError($"Live2D 渲染异常：{message}");
        try
        {
            DisposeWebHost();
            ShowFallbackAvatar(force: true);
        }
        catch (Exception ex)
        {
            _logger.Error("[Live2D] 页面异常后降级失败。", ex);
        }
    }

    private void ShowFallbackAvatar(bool force)
    {
        try
        {
            EnsureWindow();
            _window!.SetFallbackVisible(true);
            lock (_sync)
            {
                _fallbackActive = true;
            }

            if (force)
            {
                _window.Avatar.ResetAnimation();
            }
        }
        catch (Exception ex)
        {
            HandleError("显示内置兜底形象失败", ex);
        }
    }

    private void DisposeWebHost()
    {
        var host = _webHost;
        _webHost = null;
        if (host is null)
        {
            return;
        }

        try
        {
            host.PageReady -= OnPageReady;
            host.ModelLoaded -= OnModelLoaded;
            host.PageError -= OnPageError;
            host.Dispose();
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Live2D] 释放 WebView2 宿主失败：{ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // 30Hz 合帧下发：状态 / 口型 / 眼动 / 气泡
    // ------------------------------------------------------------------

    /// <summary>应用眼动配置：采样帧率（5–300Hz）与是否启用。</summary>
    private void ApplyGazeConfig(Live2DConfig config)
    {
        var fps = Math.Clamp(config.EyeFollowFps, 5, 300);
        if (_gazeFps != fps)
        {
            _gazeFps = fps;
            _logger.Debug($"[Live2D] 眼动采样帧率 = {fps}fps");
        }

        UpdateGazeState();
    }

    private void UpdateGazeState()
    {
        _gazeActive = _visible && GetConfig().EyeFollow && Interlocked.Read(ref _gazeWindowHandle) != 0;
        if (!_gazeActive)
        {
            lock (_gazeSync)
            {
                _gazeSampleDirty = false;
            }
        }
    }

    private void UpdateGazeWindowHandle()
    {
        var handle = _window?.Handle ?? IntPtr.Zero;
        Interlocked.Exchange(ref _gazeWindowHandle, handle.ToInt64());
    }

    private void HookRendering()
    {
        if (_renderingHooked)
        {
            return;
        }

        CompositionTarget.Rendering += OnRenderingFrame;
        _renderingHooked = true;
    }

    private void UnhookRendering()
    {
        if (!_renderingHooked)
        {
            return;
        }

        try
        {
            CompositionTarget.Rendering -= OnRenderingFrame;
        }
        catch (Exception)
        {
            // 进程退出时可能已无法访问 WPF 静态事件，忽略。
        }

        _renderingHooked = false;
    }

    private void StartGazeThread()
    {
        if (_gazeThread is not null)
        {
            return;
        }

        _gazeThreadStop = false;
        _gazeThread = new Thread(GazeThreadMain)
        {
            IsBackground = true,
            Name = "VoxIME.Live2DGaze"
        };
        _gazeThread.Start();
    }

    private void StopGazeThread()
    {
        _gazeThreadStop = true;
        var thread = _gazeThread;
        _gazeThread = null;
        if (thread is not null)
        {
            try
            {
                thread.Join(1000);
            }
            catch (Exception)
            {
                // 忽略退出阶段的线程等待异常。
            }
        }
    }

    /// <summary>
    /// 后台按配置帧率采样全局光标（5–300Hz，采样成本极低），把归一化视线缓存起来；
    /// 真正下发给 WebView2 由渲染帧（CompositionTarget.Rendering）完成，
    /// 这样高帧率不会用高频 DispatcherTimer 挤占 UI 线程（之前 60fps 抽搐的根因）。
    /// </summary>
    private void GazeThreadMain()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (!_gazeThreadStop)
        {
            var fps = Math.Clamp(_gazeFps, 5, 300);
            var intervalMs = 1000.0 / fps;
            var deadline = stopwatch.Elapsed.TotalMilliseconds + intervalMs;
            try
            {
                if (_gazeActive)
                {
                    var handle = new IntPtr(Interlocked.Read(ref _gazeWindowHandle));
                    if (handle != IntPtr.Zero
                        && WindowInterop.TryGetCursorPosition(out var cursorX, out var cursorY)
                        && WindowInterop.TryGetWindowRect(handle, out var left, out var top, out var width, out var height))
                    {
                        var centerX = left + width / 2.0;
                        var centerY = top + height / 2.0;
                        // 鼠标距窗口中心约 1.8 倍窗口尺寸时达到最大偏转。
                        var gazeX = Math.Clamp((cursorX - centerX) / Math.Max(1.0, width * 1.8), -1, 1);
                        // 上下方向修正：屏幕 Y 向下为正，而 Cubism 视线 Y 正方向与屏幕相反，这里直接按“光标在上→视线向上”换算。
                        var gazeY = Math.Clamp((cursorY - centerY) / Math.Max(1.0, height * 1.8), -1, 1);
                        lock (_gazeSync)
                        {
                            _gazeSampleX = gazeX;
                            _gazeSampleY = gazeY;
                            _gazeSampleDirty = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"[Live2D] 眼动采样异常：{ex.Message}");
            }

            while (!_gazeThreadStop && stopwatch.Elapsed.TotalMilliseconds < deadline)
            {
                var remaining = deadline - stopwatch.Elapsed.TotalMilliseconds;
                if (remaining > 2.0)
                {
                    Thread.Sleep(1);
                }
                else
                {
                    Thread.SpinWait(40);
                }
            }
        }
    }

    /// <summary>渲染帧回调：每帧把后台采样到的最新视线下发给模型（通常等于显示器刷新率），并驱动 WPF 兜底形象。</summary>
    private void OnRenderingFrame(object? sender, EventArgs e)
    {
        if (_disposed || _window is null)
        {
            return;
        }

        var config = GetConfig();
        double gazeX;
        double gazeY;
        bool dirty;
        lock (_gazeSync)
        {
            gazeX = _gazeSampleX;
            gazeY = _gazeSampleY;
            dirty = _gazeSampleDirty;
            _gazeSampleDirty = false;
        }

        double manualX;
        double manualY;
        lock (_sync)
        {
            manualX = _gazeX;
            manualY = _gazeY;
        }

        var effectiveX = config.EyeFollow ? gazeX : manualX;
        var effectiveY = config.EyeFollow ? gazeY : manualY;
        if (!dirty && config.EyeFollow)
        {
            return;
        }

        // 不同厂商/作者的模型可能把 ParamEyeBallY / ParamAngleY 的上下方向绑反（骨骼绑定约定不同），
        // 因此提供按模型记忆的“上下反转”开关；只影响 WebView2 模型，WPF 兜底形象始终保持正确方向。
        var invertY = config.EyeFollowInvertY;
        var webGazeY = invertY ? -effectiveY : effectiveY;
        var invertChanged = invertY != _lastSentGazeInvertY;
        if (!invertChanged
            && NearlyEqual(effectiveX, _lastSentGazeX, 0.005)
            && NearlyEqual(effectiveY, _lastSentGazeY, 0.005))
        {
            return;
        }

        _lastSentGazeX = effectiveX;
        _lastSentGazeY = effectiveY;
        _lastSentGazeInvertY = invertY;
        try
        {
            _window.Avatar.GazeX = effectiveX;
            _window.Avatar.GazeY = effectiveY;
            _webHost?.SendGaze(Math.Round(effectiveX, 3), Math.Round(webGazeY, 3));
            _logger.Debug($"[Live2D] 眼动下发 gaze=({effectiveX:0.00},{webGazeY:0.00}) invertY={invertY}");
        }
        catch (Exception ex)
        {
            _logger.Debug($"[Live2D] 眼动下发失败：{ex.Message}");
        }
    }

    private void OnFlushTick(object? sender, EventArgs e)
    {
        if (_disposed || _window is null)
        {
            return;
        }

        try
        {
            var config = GetConfig();
            Live2DState state;
            double mouth;
            string? bubble;
            DateTime bubbleSetAt;
            lock (_sync)
            {
                state = _state;
                mouth = _mouthValue;
                bubble = _bubbleText;
                bubbleSetAt = _bubbleSetAt;
            }

            if (bubble is not null && DateTime.UtcNow - bubbleSetAt > BubbleAutoHideDelay)
            {
                lock (_sync)
                {
                    _bubbleText = null;
                }

                bubble = null;
            }

            var avatar = _window.Avatar;

            if (state != _lastSentState)
            {
                _lastSentState = state;
                avatar.State = state;
                _webHost?.SendState(StateToJs(state));
            }

            var effectiveMouth = config.MouthSync
                ? Math.Clamp(mouth * Math.Clamp(config.MouthSensitivity, 0.1, 5.0), 0, 1)
                : 0;
            if (!NearlyEqual(effectiveMouth, _lastSentMouth, 0.004))
            {
                _lastSentMouth = effectiveMouth;
                avatar.MouthOpen = effectiveMouth;
                _webHost?.SendMouth(Math.Round(effectiveMouth, 3));
            }

            var bubbleVisible = config.ShowBubble && !string.IsNullOrEmpty(bubble);
            if (!string.Equals(bubble, _lastSentBubble, StringComparison.Ordinal) || bubbleVisible != _lastSentBubbleVisible)
            {
                _lastSentBubble = bubble;
                _lastSentBubbleVisible = bubbleVisible;
                _window.Bubble.ShowText(bubbleVisible ? bubble : null);
                _webHost?.SendBubble(bubbleVisible ? bubble : null);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Live2D] 合帧下发异常：{ex.Message}");
        }
    }

    private static bool NearlyEqual(double a, double b, double tolerance) =>
        !double.IsNaN(a) && !double.IsNaN(b) && Math.Abs(a - b) <= tolerance;

    private static string StateToJs(Live2DState state) => state switch
    {
        Live2DState.Listening => "listening",
        Live2DState.Thinking => "thinking",
        Live2DState.Speaking => "speaking",
        Live2DState.Error => "error",
        _ => "idle"
    };

    // ------------------------------------------------------------------
    // 错误 / 就绪 / 配置工具
    // ------------------------------------------------------------------
    private void MarkReady(string reason)
    {
        if (_readyRaised || _disposed)
        {
            return;
        }

        _readyRaised = true;
        _isAvailable = true;
        _logger.Info($"[Live2D] 桌宠就绪：{reason}");
        try
        {
            Ready?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.Error("[Live2D] Ready 事件处理器抛出异常。", ex);
        }
    }

    private void RaiseError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        lock (_sync)
        {
            if (_lastRaisedError == message && DateTime.UtcNow - _lastRaisedErrorAt < ErrorDedupeWindow)
            {
                return;
            }

            _lastRaisedError = message;
            _lastRaisedErrorAt = DateTime.UtcNow;
            _lastError = message;
        }

        _logger.Warn("[Live2D] " + message);
        try
        {
            ErrorOccurred?.Invoke(this, message);
        }
        catch (Exception ex)
        {
            _logger.Error("[Live2D] ErrorOccurred 事件处理器抛出异常。", ex);
        }
    }

    private void HandleError(string operation, Exception exception)
    {
        _logger.Error($"[Live2D] {operation} 失败。", exception);
        RaiseError($"{operation}失败：{exception.Message}（已降级为内置兜底形象）");
    }

    private Live2DConfig GetConfig()
    {
        lock (_sync)
        {
            return _config;
        }
    }

    private static Live2DConfig CloneConfig(Live2DConfig? config)
    {
        if (config is null)
        {
            return new Live2DConfig();
        }

        try
        {
            return ConfigJson.Clone(config);
        }
        catch (Exception)
        {
            return new Live2DConfig
            {
                Enabled = config.Enabled,
                ModelDirectory = config.ModelDirectory,
                Width = config.Width,
                Height = config.Height,
                Left = config.Left,
                Top = config.Top,
                Opacity = config.Opacity,
                Scale = config.Scale,
                ClickThrough = config.ClickThrough,
                AlwaysOnTop = config.AlwaysOnTop,
                EyeFollow = config.EyeFollow,
                IdleMotion = config.IdleMotion,
                MouthSync = config.MouthSync,
                MouthSensitivity = config.MouthSensitivity,
                ShowBubble = config.ShowBubble,
                IdleMotionGroup = config.IdleMotionGroup,
                ListeningExpression = config.ListeningExpression,
                ThinkingExpression = config.ThinkingExpression,
                SpeakingExpression = config.SpeakingExpression,
                ErrorExpression = config.ErrorExpression,
                FallbackAvatarTheme = config.FallbackAvatarTheme
            };
        }
    }

    private void RunOnUiThread(Action action, string operation)
    {
        if (_disposed)
        {
            return;
        }

        var dispatcher = _dispatcher;
        if (dispatcher is null)
        {
            RaiseError($"{operation} 失败：当前进程没有 WPF Dispatcher，请在有 WPF Application 的 UI 线程初始化桌宠。");
            return;
        }

        try
        {
            if (dispatcher.CheckAccess())
            {
                action();
            }
            else if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
            {
                dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
            }
        }
        catch (Exception ex)
        {
            HandleError(operation, ex);
        }
    }

    private void OnWindowPositionChanged(object? sender, Live2DPositionChangedEventArgs e)
    {
        var previousTop = _userTop;
        _userLeft = e.Left;
        _userTop = e.Top;
        try
        {
            var config = GetConfig();
            // 只做下边吸附；向上拖动时不再把窗口往下拽，用户可以自由移到上半屏。
            var movingUp = previousTop.HasValue && e.Top < previousTop.Value - 0.5;
            if (config.SnapToEdge && !movingUp && _window is not null
                && _window.TrySnapToBottomEdge(config.KeepAboveTaskbar))
            {
                _userLeft = _window.Left;
                _userTop = _window.Top;
                e = new Live2DPositionChangedEventArgs(_window.Left, _window.Top);
            }

            PositionChanged?.Invoke(this, e);
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Live2D] PositionChanged 事件处理器异常：{ex.Message}");
        }
    }

    private static async Task<bool> WaitForTaskAsync(Task task, TimeSpan timeout, CancellationToken token)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await task.WaitAsync(timeoutCts.Token).ConfigureAwait(true);
            return true;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return false;
        }
    }

    private static async Task<T?> WaitForTaskAsync<T>(Task<T> task, TimeSpan timeout, CancellationToken token)
        where T : class
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await task.WaitAsync(timeoutCts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void CancelContentToken()
    {
        try
        {
            _contentCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 忽略。
        }

        _contentCts?.Dispose();
        _contentCts = null;
    }

    private void CleanupCore()
    {
        _flushTimer?.Stop();
        UnhookRendering();
        StopGazeThread();
        _gazeActive = false;
        Interlocked.Exchange(ref _gazeWindowHandle, 0);
        CancelContentToken();
        DisposeWebHost();

        if (_window is not null)
        {
            _window.PositionChanged -= OnWindowPositionChanged;
            _window.CloseSafely();
            _window = null;
        }

        _visible = false;
        _isAvailable = false;
        _contentInitialized = false;
        _contentInitializing = false;
        lock (_sync)
        {
            _fallbackActive = false;
        }
    }
}
