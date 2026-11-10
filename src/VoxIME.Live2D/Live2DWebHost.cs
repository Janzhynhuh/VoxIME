using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using VoxIME.Core;
using VoxIME.Core.Abstractions;
using VoxIME.Core.Configuration;
using VoxIME.Core.Logging;
using VoxIME.Core.Services;

namespace VoxIME.Live2D;

/// <summary>
/// WebView2 宿主：承载 Live2D 网页（透明背景），负责虚拟主机映射、JS ↔ C# 消息桥与握手。
///
/// 使用 <see cref="WebView2CompositionControl"/>（WPF 合成控件），才能在
/// <c>AllowsTransparency=True</c> 的 WPF 分层窗口里实现真正的 per-pixel 透明合成
/// （普通 WebView2 控件是 HWND 子窗口，在分层窗口里只能得到不透明色块）。
/// </summary>
internal sealed class Live2DWebHost : IDisposable
{
    /// <summary>应用资源虚拟主机名：映射到包含 wwwroot 的 live2d 输出目录。</summary>
    public const string AppHostName = "voxime.live2d";

    /// <summary>用户模型虚拟主机名：映射到配置里的模型目录。</summary>
    public const string ModelHostName = "voxime.models";

    private const string PageRelativePath = "wwwroot/index.html";

    private readonly IAppLogger _logger;
    private readonly WebView2CompositionControl _view;
    private readonly Queue<string> _pendingMessages = new();
    private CoreWebView2Environment? _environment;
    private bool _coreReady;
    private bool _disposed;
    private bool _pageReadyReceived;
    private string? _mappedModelDirectory;

    public Live2DWebHost(IAppLogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _view = new WebView2CompositionControl
        {
            DefaultBackgroundColor = System.Drawing.Color.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            // 注意：不能以 Collapsed 状态初始化——Collapsed 的控件没有有效渲染目标，
            // EnsureCoreWebView2Async 会一直挂起。始终 Visible，空白时本身是透明的。
            Visibility = Visibility.Visible
        };
        _view.NavigationCompleted += OnNavigationCompleted;
    }

    public FrameworkElement View => _view;

    public bool IsCoreReady => _coreReady;

    public bool IsPageReady => _pageReadyReceived;

    /// <summary>页面脚本就绪（收到 {type:"ready"}）。</summary>
    public event EventHandler? PageReady;

    /// <summary>模型加载完成（收到 {type:"modelLoaded", name, width, height}）。</summary>
    public event EventHandler<Live2DModelSizeEventArgs>? ModelLoaded;

    /// <summary>页面/加载错误（收到 {type:"error", message} 或导航失败）。</summary>
    public event EventHandler<string>? PageError;

    /// <summary>解析随程序发布的 Live2D 网页根目录（输出目录 或 开发期源码 assets）。</summary>
    public static string? ResolveWebRoot()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var direct = Path.Combine(baseDirectory, "live2d", "wwwroot");
        if (Directory.Exists(direct) && File.Exists(Path.Combine(direct, "index.html")))
        {
            return direct;
        }

        // 开发期：从输出目录向上找仓库里的 assets/live2d/wwwroot（不写死绝对路径）。
        var directory = new DirectoryInfo(baseDirectory);
        for (var i = 0; i < 10 && directory is not null; i++)
        {
            var candidate = Path.Combine(directory.FullName, "assets", "live2d", "wwwroot");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "index.html")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// 初始化 WebView2 并加载桌宠页面。返回 false 表示 WebView2 不可用（调用方应降级到 WPF 兜底形象）。
    /// 必须在 UI 线程调用。
    /// </summary>
    public async Task<bool> InitializeAsync(string webRoot, string? modelDirectory)
    {
        if (_disposed)
        {
            return false;
        }

        try
        {
            var webRootParent = Directory.GetParent(webRoot)?.FullName
                                ?? throw new InvalidOperationException("无法解析 Live2D 网页根目录的父目录。");
            // 允许开发期（多实例/验证工具）用独立 UserData 目录，避免 WebView2 用户数据目录被占用。
            var userDataOverride = Environment.GetEnvironmentVariable("VOXIME_LIVE2D_USER_DATA_DIR");
            var userDataFolder = string.IsNullOrWhiteSpace(userDataOverride)
                ? Path.Combine(AppInfo.AppDataDirectory, "webview2")
                : userDataOverride!;
            Directory.CreateDirectory(userDataFolder);

            _logger.Info($"[Live2D] 初始化 WebView2：webRoot={webRoot}, userData={userDataFolder}");
            _environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder).ConfigureAwait(true);
            _logger.Info("[Live2D] WebView2 环境创建完成。");

            var ownerWindow = Window.GetWindow(_view);
            _logger.Info(
                $"[Live2D] Ensure 前状态：IsLoaded={_view.IsLoaded}, IsVisible={_view.IsVisible}, " +
                $"ActualWidth={_view.ActualWidth:0}, ActualHeight={_view.ActualHeight:0}, " +
                $"PresentationSource={(PresentationSource.FromVisual(_view) is not null)}, " +
                $"OwnerWindow={(ownerWindow is null ? "(null)" : $"visible={ownerWindow.IsVisible}, loaded={ownerWindow.IsLoaded}")}");
            _view.Loaded += OnViewLoadedFirstTime;
            await _view.EnsureCoreWebView2Async(_environment).ConfigureAwait(true);
            _view.Loaded -= OnViewLoadedFirstTime;
            _logger.Info("[Live2D] WebView2 控件初始化完成。");
            var core = _view.CoreWebView2
                       ?? throw new InvalidOperationException("WebView2 初始化后 CoreWebView2 为 null。");

            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsSwipeNavigationEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.WebMessageReceived += OnWebMessageReceived;
            core.ProcessFailed += OnProcessFailed;
            core.SetVirtualHostNameToFolderMapping(
                AppHostName,
                webRootParent,
                CoreWebView2HostResourceAccessKind.DenyCors);

            if (!string.IsNullOrWhiteSpace(modelDirectory) && Directory.Exists(modelDirectory))
            {
                MapModelDirectory(modelDirectory!);
            }

            // 升级后 WebView2 可能继续使用旧的 app.js；本地页面不依赖网络缓存，
            // 每次初始化清掉磁盘缓存，保证待机/眨眼等 JS 修复立即生效。
            try
            {
                await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.Warn($"[Live2D] 清理 WebView2 缓存失败（忽略）：{ex.Message}");
            }

            var assetVersion = GetWebAssetVersion(webRoot);
            var url = $"https://{AppHostName}/{PageRelativePath}?embedded=1&host=1&v={assetVersion}";
            _logger.Info($"[Live2D] 导航到桌宠页：{url}");
            core.Navigate(url);
            _coreReady = true;
            FlushPendingMessages();
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("[Live2D] WebView2 初始化失败，将降级为 WPF 兜底形象。", ex);
            return false;
        }
    }

    /// <summary>把用户模型目录映射到虚拟主机（跨域读取 moc3/贴图需要 Allow CORS）。</summary>
    public void MapModelDirectory(string modelDirectory)
    {
        if (_view.CoreWebView2 is null)
        {
            return;
        }

        _view.CoreWebView2.SetVirtualHostNameToFolderMapping(
            ModelHostName,
            modelDirectory,
            CoreWebView2HostResourceAccessKind.Allow);
        _mappedModelDirectory = modelDirectory;
        _logger.Info($"[Live2D] 模型目录映射：https://{ModelHostName}/ -> {modelDirectory}");
    }

    /// <summary>由模型目录 + .model3.json 绝对路径构造页面可访问的 URL。</summary>
    public static string BuildModelUrl(string modelDirectory, string model3FullPath)
    {
        var relative = Path.GetRelativePath(modelDirectory, model3FullPath).Replace('\\', '/');
        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString);
        return $"https://{ModelHostName}/{string.Join('/', segments)}";
    }

    public void SetZoom(double zoomFactor)
    {
        try
        {
            _view.ZoomFactor = Math.Clamp(zoomFactor, 0.2, 3.0);
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Live2D] 设置 WebView 缩放失败：{ex.Message}");
        }
    }

    /// <summary>发送消息到页面（CoreWebView2 就绪前先入队）。</summary>
    public void Send(object message)
    {
        if (_disposed)
        {
            return;
        }

        string json;
        try
        {
            json = JsonSerializer.Serialize(message);
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Live2D] 消息序列化失败：{ex.Message}");
            return;
        }

        if (!_coreReady || _view.CoreWebView2 is null)
        {
            _pendingMessages.Enqueue(json);
            return;
        }

        try
        {
            _view.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Live2D] 发送消息到页面失败：{ex.Message}");
        }
    }

    public void SendLoadModel(string modelUrl) => Send(new { type = "loadModel", url = modelUrl });

    public void SendState(string state) => Send(new { type = "setState", state });

    public void SendPreviewMotion(string motionGroup) =>
        Send(new { type = "previewMotion", group = motionGroup });

    /// <summary>同时预览多个动作组（每个动作组占一条并行层）。</summary>
    public void SendPreviewMotions(IReadOnlyList<string> motionGroups) =>
        Send(new { type = "previewMotion", groups = motionGroups });

    public void SendPreviewExpression(string expression) =>
        Send(new { type = "previewExpression", name = expression });

    public void SendMouth(double value) => Send(new { type = "setMouth", value });

    public void SendGaze(double x, double y) => Send(new { type = "setGaze", x, y });

    public void SendBubble(string? text) => Send(new { type = "setBubble", text });

    public void SendConfig(
        bool eyeFollow,
        bool idleMotion,
        bool mouthSync,
        double scale,
        double offsetX,
        double offsetY,
        string idleMotionGroup,
        IReadOnlyList<Live2DMotionEntry> idleMotionGroups,
        bool showBubble,
        IReadOnlyDictionary<string, string?> expressions,
        IReadOnlyDictionary<string, IReadOnlyList<Live2DMotionEntry>> motions,
        Live2DModelAssetCatalog? modelAssets)
    {
        Send(new
        {
            type = "setConfig",
            eyeFollow,
            idleMotion,
            mouthSync,
            scale,
            offsetX,
            offsetY,
            idleMotionGroup,
            idleMotionGroups = (idleMotionGroups ?? Array.Empty<Live2DMotionEntry>())
                .Select(item => new { group = item.Group, loop = item.EffectiveLoop })
                .ToArray(),
            showBubble,
            expressions,
            motions = (motions ?? new Dictionary<string, IReadOnlyList<Live2DMotionEntry>>())
                .ToDictionary(
                    pair => pair.Key,
                    pair => (pair.Value ?? Array.Empty<Live2DMotionEntry>())
                        .Select(item => new { group = item.Group, loop = item.EffectiveLoop })
                        .ToArray(),
                    StringComparer.OrdinalIgnoreCase),
            modelAssets = modelAssets is null
                ? null
                : new
                {
                    expressions = modelAssets.Expressions.Select(item => new { name = item.Name, file = item.File }).ToArray(),
                    motions = modelAssets.Motions.Select(item => new { group = item.Group, file = item.File }).ToArray()
                }
        });
    }

    /// <summary>
    /// 用网页资源时间戳做缓存版本号。WebView2 会缓存同一虚拟主机 URL；
    /// 只改 app.js 而不改查询参数时，升级后可能仍加载旧脚本，导致状态/待机逻辑看起来没生效。
    /// </summary>
    private static string GetWebAssetVersion(string webRoot)
    {
        try
        {
            var appJs = Path.Combine(webRoot, "app.js");
            if (File.Exists(appJs))
            {
                return File.GetLastWriteTimeUtc(appJs).Ticks.ToString();
            }
        }
        catch (Exception)
        {
            // 版本号失败时退回程序版本，不阻塞页面加载。
        }

        return AppInfo.Version;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_view.CoreWebView2 is not null)
            {
                _view.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                _view.CoreWebView2.ProcessFailed -= OnProcessFailed;
            }
        }
        catch (Exception)
        {
            // 进程退出阶段可能已释放，忽略。
        }

        try
        {
            _view.Dispose();
        }
        catch (Exception)
        {
            // 忽略释放异常。
        }
    }

    private void FlushPendingMessages()
    {
        while (_pendingMessages.Count > 0 && _view.CoreWebView2 is not null)
        {
            var json = _pendingMessages.Dequeue();
            try
            {
                _view.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch (Exception ex)
            {
                _logger.Warn($"[Live2D] 补发消息失败：{ex.Message}");
            }
        }
    }

    private void OnViewLoadedFirstTime(object? sender, RoutedEventArgs e)
    {
        _logger.Info($"[Live2D] WebView2 控件首次 Loaded（IsLoaded={_view.IsLoaded}）。");
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            var message = $"Live2D 页面加载失败（{e.WebErrorStatus}）。";
            _logger.Warn("[Live2D] " + message);
            PageError?.Invoke(this, message);
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        var message = $"WebView2 进程异常（{e.ProcessFailedKind}），桌宠已降级。";
        _logger.Warn("[Live2D] " + message);
        PageError?.Invoke(this, message);
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string json;
        try
        {
            json = e.WebMessageAsJson;
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Live2D] 读取页面消息失败：{ex.Message}");
            return;
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            switch (type)
            {
                case "ready":
                    if (!_pageReadyReceived)
                    {
                        _pageReadyReceived = true;
                        _logger.Info("[Live2D] 页面脚本握手完成（ready）。");
                        PageReady?.Invoke(this, EventArgs.Empty);
                    }

                    break;
                case "modelLoaded":
                {
                    var name = root.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                    var width = root.TryGetProperty("width", out var widthElement) && widthElement.TryGetDouble(out var w)
                        ? w
                        : 0;
                    var height = root.TryGetProperty("height", out var heightElement) && heightElement.TryGetDouble(out var h)
                        ? h
                        : 0;
                    _logger.Info($"[Live2D] 模型加载完成：{name ?? "(未命名)"}，原始尺寸 {width:0}×{height:0}");
                    ModelLoaded?.Invoke(this, new Live2DModelSizeEventArgs(name ?? "model", width, height));
                    break;
                }
                case "motionPreviewResult":
                {
                    var ok = root.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True;
                    var group = root.TryGetProperty("group", out var groupElement) ? groupElement.GetString() : "?";
                    if (ok)
                    {
                        _logger.Info($"[Live2D] 已下发动作预览：{group}");
                    }
                    else
                    {
                        _logger.Warn($"[Live2D] 动作预览失败：{group}");
                    }
                    break;
                }
                case "expressionPreviewResult":
                {
                    var ok = root.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True;
                    var name = root.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : "?";
                    if (ok)
                    {
                        _logger.Info($"[Live2D] 已下发表情预览：{name}");
                    }
                    else
                    {
                        var message = root.TryGetProperty("message", out var messageElement)
                            ? messageElement.GetString()
                            : "未找到或加载失败";
                        _logger.Warn($"[Live2D] 表情预览失败：{name}（{message}）");
                    }
                    break;
                }
                case "debug":
                {
                    var message = root.TryGetProperty("message", out var debugElement)
                        ? debugElement.GetString()
                        : "";
                    _logger.Info($"[Live2D] 页面调试：{message}");
                    break;
                }
                case "expressionResult":
                {
                    var ok = root.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True;
                    var state = root.TryGetProperty("state", out var stateElement) ? stateElement.GetString() : "?";
                    var name = root.TryGetProperty("name", out var exprElement) ? exprElement.GetString() : "?";
                    if (ok)
                    {
                        _logger.Info($"[Live2D] 表情已应用：{state} -> {name}");
                    }
                    else
                    {
                        var message = root.TryGetProperty("message", out var messageElement)
                            ? messageElement.GetString()
                            : "未找到或加载失败";
                        _logger.Warn($"[Live2D] 表情应用失败：{state} -> {name}（{message}）");
                    }
                    break;
                }
                case "error":
                {
                    var message = root.TryGetProperty("message", out var messageElement)
                        ? messageElement.GetString()
                        : "页面报告未知错误";
                    _logger.Warn($"[Live2D] 页面错误：{message}");
                    PageError?.Invoke(this, message ?? "页面报告未知错误");
                    break;
                }
                default:
                    _logger.Debug($"[Live2D] 未处理的页面消息类型：{type}");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Live2D] 解析页面消息失败：{ex.Message}；原始内容：{json}");
        }
    }
}
