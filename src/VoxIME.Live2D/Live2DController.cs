using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using VoxIME.Core.Abstractions;
using VoxIME.Core.Configuration;
using VoxIME.Core.Logging;

namespace VoxIME.Live2D;

/// <summary>
/// Live2D 桌宠控制器（<see cref="ILive2DController"/> 实现）。
///
/// 行为概要：
/// - 在 WPF Dispatcher 上创建透明、无边框、置顶、不占任务栏的桌宠窗口；
/// - 通过 WebView2CompositionControl 渲染 Cubism 3/4 模型（真正透明合成）；
/// - 无模型 / 无 WebView2 Runtime / 模型加载失败 / 握手超时 → 自动降级为 WPF 矢量兜底形象，
///   并通过 <see cref="ErrorOccurred"/> 与 <see cref="LastError"/> 报告原因；
/// - 任意线程调用安全：高频接口（口型/眼动/状态）写入字段，由 UI 线程 30Hz 合并后下发，
///   避免跨线程 UI 访问与消息洪泛；
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
    private readonly Dispatcher? _dispatcher;
    private readonly DispatcherTimer? _flushTimer;

    private Live2DConfig _config = new();
    private PetWindow? _window;
    private Live2DWebHost? _webHost;
    private CancellationTokenSource? _contentCts;
    private TaskCompletionSource<bool>? _pageReadySource;
    private TaskCompletionSource<string>? _modelLoadedSource;

    private Live2DState _state = Live2DState.Idle;
    private double _mouthValue;
    private double _gazeX;
    private double _gazeY;
    private string? _bubbleText;
    private DateTime _bubbleSetAt = DateTime.MinValue;
    private DateTime _lastGazePollAt = DateTime.MinValue;
    private string? _lastRaisedError;
    private DateTime _lastRaisedErrorAt = DateTime.MinValue;

    private Live2DState _lastSentState = (Live2DState)(-1);
    private double _lastSentMouth = double.NaN;
    private double _lastSentGazeX = double.NaN;
    private double _lastSentGazeY = double.NaN;
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

        RunOnUiThread(InitializeCore, "Initialize");
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
            ApplyFlushInterval();

            var config = GetConfig();
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
        if ((left < 0 || top < 0) && _userLeft.HasValue && _userTop.HasValue)
        {
            // 用户拖动过：在配置未显式给坐标时保留用户位置
            left = _userLeft.Value;
            top = _userTop.Value;
        }

        _window.ApplyGeometry(
            config.Width,
            config.Height,
            left,
            top,
            config.Opacity,
            config.AlwaysOnTop,
            config.ClickThrough);
        _window.SetAvatarScale(config.Scale);
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
            var previousDirectory = GetConfig().ModelDirectory;
            ApplyWindowConfiguration();
            ApplyFlushInterval();

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
            ApplyFlushInterval();
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
        var expressions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["listening"] = config.ListeningExpression,
            ["thinking"] = config.ThinkingExpression,
            ["speaking"] = config.SpeakingExpression,
            ["error"] = config.ErrorExpression
        };

        _webHost?.SendConfig(
            config.EyeFollow,
            config.IdleMotion,
            config.MouthSync,
            1.0,
            string.IsNullOrWhiteSpace(config.IdleMotionGroup) ? "Idle" : config.IdleMotionGroup,
            config.ShowBubble,
            expressions);
        _webHost?.SetZoom(config.Scale);

        if (_window is not null)
        {
            _window.SetAvatarScale(config.Scale);
        }
    }

    // ------------------------------------------------------------------
    // WebView2 事件
    // ------------------------------------------------------------------
    private void OnPageReady(object? sender, EventArgs e) => _pageReadySource?.TrySetResult(true);

    private void OnModelLoaded(object? sender, string name) => _modelLoadedSource?.TrySetResult(name);

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

    /// <summary>
    /// 按 <see cref="Live2DConfig.EyeFollowFps"/> 调整合帧定时器频率：
    /// 眼动可配 5–60Hz，定时器最快提到约 8ms/帧；其它状态/口型消息仍是"变化才下发"，提升帧率开销很小。
    /// </summary>
    private void ApplyFlushInterval()
    {
        var timer = _flushTimer;
        if (timer is null)
        {
            return;
        }

        var config = GetConfig();
        var eyeFollowFps = config.EyeFollow ? Math.Clamp(config.EyeFollowFps, 5, 60) : 30;
        var intervalMs = Math.Max(8.0, Math.Min(FlushIntervalMilliseconds, 1000.0 / eyeFollowFps));
        var interval = TimeSpan.FromMilliseconds(intervalMs);
        if (timer.Interval != interval)
        {
            timer.Interval = interval;
        }

        _lastGazePollAt = DateTime.MinValue;
        _logger.Debug($"[Live2D] 合帧间隔={intervalMs:0.0}ms（眼动轮询目标 {eyeFollowFps}fps）");
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
            double gazeX;
            double gazeY;
            string? bubble;
            DateTime bubbleSetAt;
            lock (_sync)
            {
                state = _state;
                mouth = _mouthValue;
                gazeX = _gazeX;
                gazeY = _gazeY;
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

            // 眼动跟随：EyeFollow 开启时按配置的 EyeFollowFps 轮询全局光标位置并换算成相对于桌宠窗口中心的
            // 归一化视线，覆盖显式 SetGaze 的默认值（桌面宠物没有焦点，无法靠 WPF 鼠标事件拿到全局坐标）。
            // 合帧定时器会按该帧率提速（最高 60fps），其它状态/口型消息仍是变化才下发，开销很小。
            var eyeFollowFps = Math.Clamp(config.EyeFollowFps, 5, 60);
            var gazePollIntervalMs = 1000.0 / eyeFollowFps;
            var nowUtc = DateTime.UtcNow;
            var gazePollDue = (nowUtc - _lastGazePollAt).TotalMilliseconds >= gazePollIntervalMs - 1.0;
            if (config.EyeFollow && _visible && _window.IsVisible && gazePollDue
                && WindowInterop.TryGetCursorPosition(out var cursorX, out var cursorY)
                && WindowInterop.TryGetWindowRect(_window.Handle, out var windowLeft, out var windowTop, out var windowWidth, out var windowHeight))
            {
                _lastGazePollAt = nowUtc;
                var centerX = windowLeft + windowWidth / 2.0;
                var centerY = windowTop + windowHeight / 2.0;
                // 鼠标距窗口中心约 1.8 倍窗口尺寸时达到最大偏转，全屏范围内不会过于夸张。
                gazeX = Math.Clamp((cursorX - centerX) / Math.Max(1.0, windowWidth * 1.8), -1, 1);
                gazeY = Math.Clamp((centerY - cursorY) / Math.Max(1.0, windowHeight * 1.8), -1, 1);
            }

            var effectiveGazeX = config.EyeFollow ? gazeX : 0;
            var effectiveGazeY = config.EyeFollow ? gazeY : 0;
            if (!NearlyEqual(effectiveGazeX, _lastSentGazeX, 0.01) || !NearlyEqual(effectiveGazeY, _lastSentGazeY, 0.01))
            {
                _lastSentGazeX = effectiveGazeX;
                _lastSentGazeY = effectiveGazeY;
                avatar.GazeX = effectiveGazeX;
                avatar.GazeY = effectiveGazeY;
                _webHost?.SendGaze(Math.Round(effectiveGazeX, 3), Math.Round(effectiveGazeY, 3));
                _logger.Debug($"[Live2D] 眼动下发 gaze=({effectiveGazeX:0.00},{effectiveGazeY:0.00})");
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
        _userLeft = e.Left;
        _userTop = e.Top;
        try
        {
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
