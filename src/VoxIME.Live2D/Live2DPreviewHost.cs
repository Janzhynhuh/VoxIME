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

/// <summary>设置页位置预览里拖动模型时上报的归一化偏移（-1..1，按窗口宽/高比例）。</summary>
public sealed class Live2DPreviewOffsetEventArgs : EventArgs
{
    public Live2DPreviewOffsetEventArgs(double offsetX, double offsetY)
    {
        OffsetX = offsetX;
        OffsetY = offsetY;
    }

    public double OffsetX { get; }
    public double OffsetY { get; }
}

/// <summary>
/// 设置页「模型位置预览」的独立 WebView2 宿主：加载 preview.html 的 embed 模式，
/// 提供 60fps 的模型渲染与拖动偏移上报。
///
/// 与桌宠主宿主相互独立：使用单独的 UserData 目录，设置页关闭/切页时整体 Dispose，
/// 避免长期占用第二份模型内存。必须在 UI 线程创建与调用。
/// </summary>
public sealed class Live2DPreviewHost : IDisposable
{
    private readonly IAppLogger _logger;
    private readonly WebView2CompositionControl _view;
    private readonly Queue<string> _pendingMessages = new();
    private CoreWebView2Environment? _environment;
    private bool _coreReady;
    private bool _pageReady;
    private bool _disposed;
    private string? _modelDirectory;
    private double _scale = 1.0;
    private double _offsetX;
    private double _offsetY;

    public Live2DPreviewHost(IAppLogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _view = new WebView2CompositionControl
        {
            DefaultBackgroundColor = System.Drawing.Color.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            // 与主宿主相同的硬性约束：初始化期间不能 Collapsed，否则 EnsureCoreWebView2Async 会挂起。
            Visibility = Visibility.Visible
        };
        _view.NavigationCompleted += OnNavigationCompleted;
    }

    /// <summary>解析随程序发布的 Live2D 网页根目录（供设置页创建预览前做可用性判断）。</summary>
    public static string? ResolveWebRoot() => Live2DWebHost.ResolveWebRoot();

    /// <summary>供 WPF 插入页面的预览控件。</summary>
    public FrameworkElement View => _view;

    public bool IsReady => _pageReady;

    public string? LastError { get; private set; }

    /// <summary>页面脚本就绪（收到 ready）。</summary>
    public event EventHandler? Ready;

    /// <summary>预览模型加载完成，参数为原始尺寸（用于 AutoFitWindow 估算）。</summary>
    public event EventHandler<Live2DModelSizeEventArgs>? ModelLoaded;

    /// <summary>页面拖动模型后上报的偏移。</summary>
    public event EventHandler<Live2DPreviewOffsetEventArgs>? OffsetChanged;

    /// <summary>初始化/加载失败，参数为可读原因。</summary>
    public event EventHandler<string>? Error;

    /// <summary>
    /// 初始化预览：映射资源/模型虚拟主机并导航到 embed 预览页。
    /// 返回 false 表示预览不可用（调用方应显示占位/兜底滑块），不影响桌宠。
    /// </summary>
    public async Task<bool> InitializeAsync(string webRoot, string modelDirectory, string modelJsonPath)
    {
        if (_disposed)
        {
            return false;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(webRoot) || !Directory.Exists(webRoot))
            {
                throw new InvalidOperationException("找不到 Live2D 网页资源目录");
            }

            var webRootParent = Directory.GetParent(webRoot)?.FullName;
            if (string.IsNullOrWhiteSpace(webRootParent) || !Directory.Exists(webRootParent))
            {
                throw new InvalidOperationException("无法解析 Live2D 网页父目录");
            }

            if (string.IsNullOrWhiteSpace(modelDirectory) || !Directory.Exists(modelDirectory))
            {
                throw new InvalidOperationException("模型目录不存在");
            }

            var userDataFolder = Path.Combine(AppInfo.AppDataDirectory, "webview2-preview");
            Directory.CreateDirectory(userDataFolder);

            _environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder).ConfigureAwait(true);

            await _view.EnsureCoreWebView2Async(_environment).ConfigureAwait(true);
            var core = _view.CoreWebView2
                       ?? throw new InvalidOperationException("预览 WebView2 初始化后 CoreWebView2 为 null。");

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
                Live2DWebHost.AppHostName,
                webRootParent,
                CoreWebView2HostResourceAccessKind.DenyCors);
            core.SetVirtualHostNameToFolderMapping(
                Live2DWebHost.ModelHostName,
                modelDirectory,
                CoreWebView2HostResourceAccessKind.Allow);

            _modelDirectory = modelDirectory;

            try
            {
                await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.Warn($"[Live2D] 预览 WebView2 清理缓存失败（忽略）：{ex.Message}");
            }

            var modelUrl = Live2DWebHost.BuildModelUrl(modelDirectory, modelJsonPath);
            var pageUrl = "https://" + Live2DWebHost.AppHostName + "/wwwroot/preview.html"
                + "?embed=1&status=0&bg=solid&model=" + Uri.EscapeDataString(modelUrl)
                + "&v=" + DateTime.UtcNow.Ticks;
            _logger.Info($"[Live2D] 预览 WebView2 加载：{pageUrl}");
            core.Navigate(pageUrl);
            _coreReady = true;
            FlushPendingMessages();
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.Warn($"[Live2D] 位置预览初始化失败：{ex.Message}");
            Error?.Invoke(this, ex.Message);
            return false;
        }
    }

    /// <summary>更新预览模型缩放与偏移；页面未就绪时先入队。</summary>
    public void ApplyGeometry(double scale, double offsetX, double offsetY)
    {
        _scale = Math.Clamp(scale, 0.2, 3.0);
        _offsetX = Math.Clamp(offsetX, -Live2DConfig.MaxModelOffset, Live2DConfig.MaxModelOffset);
        _offsetY = Math.Clamp(offsetY, -Live2DConfig.MaxModelOffset, Live2DConfig.MaxModelOffset);
        Send(new { type = "setConfig", scale = _scale, offsetX = _offsetX, offsetY = _offsetY });
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            var message = "预览页导航失败：" + e.WebErrorStatus;
            LastError = message;
            _logger.Warn("[Live2D] " + message);
            Error?.Invoke(this, message);
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            switch (type)
            {
                case "ready":
                    _pageReady = true;
                    _logger.Info("[Live2D] 位置预览页面就绪。");
                    SendInitialConfig();
                    Ready?.Invoke(this, EventArgs.Empty);
                    break;
                case "modelLoaded":
                {
                    var name = root.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "";
                    var width = root.TryGetProperty("width", out var widthElement) && widthElement.TryGetDouble(out var w) ? w : 0;
                    var height = root.TryGetProperty("height", out var heightElement) && heightElement.TryGetDouble(out var h) ? h : 0;
                    _logger.Info($"[Live2D] 位置预览模型加载完成：{name} {width:0}x{height:0}");
                    ModelLoaded?.Invoke(this, new Live2DModelSizeEventArgs(name, width, height));
                    break;
                }
                case "previewOffset":
                {
                    var offsetX = root.TryGetProperty("offsetX", out var xElement) && xElement.TryGetDouble(out var x) ? x : 0;
                    var offsetY = root.TryGetProperty("offsetY", out var yElement) && yElement.TryGetDouble(out var y) ? y : 0;
                    OffsetChanged?.Invoke(this, new Live2DPreviewOffsetEventArgs(
                        Math.Clamp(offsetX, -Live2DConfig.MaxModelOffset, Live2DConfig.MaxModelOffset),
                        Math.Clamp(offsetY, -Live2DConfig.MaxModelOffset, Live2DConfig.MaxModelOffset)));
                    break;
                }
                case "debug":
                {
                    var debugMessage = root.TryGetProperty("message", out var debugElement)
                        ? debugElement.GetString()
                        : null;
                    if (!string.IsNullOrWhiteSpace(debugMessage))
                    {
                        _logger.Debug("[Live2D] 位置预览页面：" + debugMessage);
                    }

                    break;
                }
                case "error":
                {
                    var message = root.TryGetProperty("message", out var messageElement)
                        ? messageElement.GetString()
                        : "未知错误";
                    LastError = message;
                    _logger.Warn($"[Live2D] 位置预览页面报错：{message}");
                    Error?.Invoke(this, message ?? "未知错误");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"[Live2D] 解析预览页面消息失败（忽略）：{ex.Message}");
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        var message = "预览 WebView2 进程失败：" + e.ProcessFailedKind;
        LastError = message;
        _logger.Warn("[Live2D] " + message);
        Error?.Invoke(this, message);
    }

    private void SendInitialConfig()
    {
        var catalog = Live2DModelAssetCatalog.Load(_modelDirectory);
        Send(new
        {
            type = "setConfig",
            eyeFollow = false,
            idleMotion = true,
            mouthSync = false,
            showBubble = false,
            scale = _scale,
            offsetX = _offsetX,
            offsetY = _offsetY,
            supersampling = 2,
            idleMotionGroup = "Idle",
            idleMotionGroups = Array.Empty<object>(),
            expressions = new Dictionary<string, string?>(),
            motions = new Dictionary<string, object[]>(),
            modelAssets = new
            {
                expressions = catalog.Expressions.Select(item => new { name = item.Name, file = item.File }).ToArray(),
                motions = catalog.Motions.Select(item => new { group = item.Group, file = item.File }).ToArray()
            }
        });
    }

    private void Send(object message)
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
            _logger.Warn($"[Live2D] 预览消息序列化失败：{ex.Message}");
            return;
        }

        if (!_coreReady || _view.CoreWebView2 is null)
        {
            _pendingMessages.Enqueue(json);
            return;
        }

        Post(json);
    }

    private void Post(string json)
    {
        try
        {
            _view.CoreWebView2?.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Live2D] 发送预览消息失败：{ex.Message}");
        }
    }

    private void FlushPendingMessages()
    {
        while (_pendingMessages.Count > 0 && _view.CoreWebView2 is not null)
        {
            Post(_pendingMessages.Dequeue());
        }
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
            // 忽略关闭阶段的异常。
        }

        try
        {
            _view.Dispose();
        }
        catch (Exception ex)
        {
            _logger.Debug($"[Live2D] 释放预览 WebView2 失败（忽略）：{ex.Message}");
        }
    }
}
