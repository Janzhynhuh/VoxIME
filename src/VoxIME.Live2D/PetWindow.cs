using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Runtime.InteropServices;

namespace VoxIME.Live2D;

/// <summary>
/// 桌宠窗口：透明、无边框、置顶、不占任务栏、可点击穿透（可拖动）。
/// 内容区容纳 WebView2（Live2D）或 WPF 兜底形象，顶部叠加气泡。
/// 本类只负责窗口与视觉树，不包含业务逻辑（由 <see cref="Live2DController"/> 驱动）。
/// </summary>
internal sealed class PetWindow : Window
{
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;
    private const double AutoPositionMarginRight = 28;
    private const uint MonitorDefaultToNearest = 2;
    private const uint MonitorDefaultToPrimary = 1;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private readonly Grid _contentHost;
    private readonly FallbackAvatar _avatar;
    private readonly Border _dragLayer;
    private readonly BubbleView _bubble;
    private readonly DispatcherTimer _topmostTimer;
    private FrameworkElement? _webView;
    private IntPtr _hwnd;
    private bool _clickThrough = true;
    private double _avatarScale = 1.0;
    private double _avatarOffsetX;
    private double _avatarOffsetY;

    public PetWindow()
    {
        Title = "VoxIME 桌宠";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        SnapsToDevicePixels = false;
        Opacity = 1.0;
        Width = 380;
        Height = 560;

        _avatar = new FallbackAvatar
        {
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            RenderTransformOrigin = new Point(0.5, 0.92)
        };

        _contentHost = new Grid();
        _contentHost.Children.Add(_avatar);

        _bubble = new BubbleView();

        _dragLayer = new Border
        {
            Background = Brushes.Transparent,
            Visibility = Visibility.Collapsed,
            Cursor = Cursors.SizeAll,
            ToolTip = "拖动可移动桌宠"
        };
        _dragLayer.MouseLeftButtonDown += OnDragLayerMouseLeftButtonDown;

        _root = new Grid();
        _root.Children.Add(_contentHost);
        _root.Children.Add(_dragLayer);
        _root.Children.Add(_bubble);
        Content = _root;

        SizeChanged += (_, _) =>
        {
            _bubble.UpdateMaxWidth(Width);
            ApplyAvatarTransform();
        };
        SourceInitialized += OnSourceInitialized;

        _topmostTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _topmostTimer.Tick += (_, _) => ReassertTopmost();
        Loaded += (_, _) =>
        {
            if (Topmost)
            {
                _topmostTimer.Start();
            }
        };
        Closed += (_, _) => _topmostTimer.Stop();
    }

    private readonly Grid _root;

    /// <summary>用户拖动窗口结束后触发，供宿主持久化位置。</summary>
    public event EventHandler<Live2DPositionChangedEventArgs>? PositionChanged;

    public FallbackAvatar Avatar => _avatar;

    public BubbleView Bubble => _bubble;

    public bool ClickThrough
    {
        get => _clickThrough;
        set
        {
            if (_clickThrough == value)
            {
                return;
            }

            _clickThrough = value;
            ApplyInteropStyles();
        }
    }

    public IntPtr Handle => _hwnd;

    /// <summary>接入 WebView2 控件（放在兜底形象之上，默认为透明空白）。</summary>
    public void SetWebView(FrameworkElement webView)
    {
        _webView = webView;
        webView.Visibility = Visibility.Visible;
        _contentHost.Children.Add(webView);
    }

    public void SetWebViewVisible(bool visible)
    {
        if (_webView is null)
        {
            return;
        }

        _webView.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _avatar.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SetFallbackVisible(bool visible)
    {
        _avatar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible && _webView is not null)
        {
            _webView.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 只控制兜底形象显隐，不改变 WebView2 的可见性。
    /// 用于 WebView2 初始化过程中：此时折叠控件会让 EnsureCoreWebView2Async 永久挂起。
    /// </summary>
    public void SetAvatarVisible(bool visible)
    {
        _avatar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>应用兜底形象缩放与窗口内偏移（偏移按窗口宽/高比例，正值=右/下）。</summary>
    public void SetAvatarTransform(double scale, double offsetX, double offsetY)
    {
        _avatarScale = Math.Clamp(scale, 0.2, 3.0);
        _avatarOffsetX = Math.Clamp(offsetX, -1.0, 1.0);
        _avatarOffsetY = Math.Clamp(offsetY, -1.0, 1.0);
        ApplyAvatarTransform();
    }

    private void ApplyAvatarTransform()
    {
        var transform = new TransformGroup();
        transform.Children.Add(new ScaleTransform(_avatarScale, _avatarScale));
        transform.Children.Add(new TranslateTransform(
            _avatarOffsetX * ActualWidth,
            _avatarOffsetY * ActualHeight));
        _avatar.RenderTransform = transform;
    }

    /// <summary>应用尺寸/位置/透明度/置顶/穿透。snapToEdge=true 时只处理“下边吸附”。</summary>
    public void ApplyGeometry(
        double width,
        double height,
        double left,
        double top,
        bool snapToEdge,
        bool keepAboveTaskbar,
        bool hasSavedPosition,
        double opacity,
        bool topmost,
        bool clickThrough)
    {
        Width = Math.Clamp(width, 160, 1600);
        Height = Math.Clamp(height, 160, 1600);
        Opacity = Math.Clamp(opacity, 0.2, 1.0);
        Topmost = topmost;
        if (_topmostTimer is not null)
        {
            if (topmost)
            {
                _topmostTimer.Start();
            }
            else
            {
                _topmostTimer.Stop();
            }
        }

        ClickThrough = clickThrough;
        _bubble.UpdateMaxWidth(Width);

        if (snapToEdge)
        {
            if (hasSavedPosition && IsFinite(left) && IsFinite(top))
            {
                Left = left;
                Top = top;
                TrySnapToBottomEdge(keepAboveTaskbar);
            }
            else
            {
                MoveToAutoPosition(keepAboveTaskbar);
            }
        }
        else if (hasSavedPosition && IsFinite(left) && IsFinite(top))
        {
            // 保存的位置允许负坐标（多显示器/窗口顶部超出主屏）；只有默认哨兵 (-1,-1) 才居中。
            Left = left;
            Top = top;
            ClampToVirtualScreen();
        }
        else
        {
            MoveToCenter();
            ClampToVirtualScreen();
        }
    }

    private double GetWindowScale()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                var dpi = GetDpiForWindow(hwnd);
                if (dpi > 0)
                {
                    return dpi / 96.0;
                }
            }

            return VisualTreeHelper.GetDpi(this).PixelsPerDip;
        }
        catch (Exception)
        {
            return 1.0;
        }
    }

    /// <summary>取指定窗口矩形中心所在的显示器工作区/完整区域（WPF DIP）。</summary>
    private bool TryGetMonitorRects(double left, double top, double width, double height, out Rect workArea, out Rect monitorBounds)
    {
        workArea = SystemParameters.WorkArea;
        monitorBounds = new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        try
        {
            var scale = GetWindowScale();
            if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale))
            {
                scale = 1.0;
            }

            var point = new POINT
            {
                X = (int)Math.Round((left + width / 2) * scale),
                Y = (int)Math.Round((top + height / 2) * scale)
            };
            var monitor = MonitorFromPoint(point, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            }

            if (monitor == IntPtr.Zero)
            {
                return false;
            }

            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref info))
            {
                return false;
            }

            workArea = new Rect(
                info.rcWork.Left / scale,
                info.rcWork.Top / scale,
                (info.rcWork.Right - info.rcWork.Left) / scale,
                (info.rcWork.Bottom - info.rcWork.Top) / scale);
            monitorBounds = new Rect(
                info.rcMonitor.Left / scale,
                info.rcMonitor.Top / scale,
                (info.rcMonitor.Right - info.rcMonitor.Left) / scale,
                (info.rcMonitor.Bottom - info.rcMonitor.Top) / scale);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private Rect GetWorkAreaForRect(double left, double top, double width, double height)
    {
        return TryGetMonitorRects(left, top, width, height, out var workArea, out _)
            ? workArea
            : SystemParameters.WorkArea;
    }

    private Rect GetMonitorBoundsForRect(double left, double top, double width, double height)
    {
        return TryGetMonitorRects(left, top, width, height, out _, out var monitorBounds)
            ? monitorBounds
            : new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
    }

    public void MoveTo(double left, double top)
    {
        if (!IsFinite(left) || !IsFinite(top))
        {
            return;
        }

        Left = left;
        Top = top;
        ClampToVirtualScreen();
    }

    public void MoveToAutoPosition(bool keepAboveTaskbar = true)
    {
        // 使用当前窗口所在显示器（而不是固定主屏），否则副屏上的自动贴边会算错坐标。
        var workArea = GetWorkAreaForRect(Left, Top, Width, Height);
        var monitorBounds = GetMonitorBoundsForRect(Left, Top, Width, Height);
        var bottom = keepAboveTaskbar
            ? workArea.Bottom
            : monitorBounds.Bottom;

        var maxWidth = Math.Max(160, workArea.Width - AutoPositionMarginRight);
        var maxHeight = Math.Max(160, bottom - workArea.Top);
        Width = Math.Min(Width, maxWidth);
        Height = Math.Min(Height, maxHeight);

        Left = workArea.Right - Width - AutoPositionMarginRight;
        Top = bottom - Height;
    }

    /// <summary>
    /// 只做“下边吸附”：窗口底边距离当前显示器底部（任务栏上沿/屏幕底边）在阈值内时，
    /// 把底边贴到边界；上/左/右方向不吸附、不强制移动。返回是否发生吸附。
    /// </summary>
    public bool TrySnapToBottomEdge(bool keepAboveTaskbar = true, double threshold = 100)
    {
        var workArea = GetWorkAreaForRect(Left, Top, Width, Height);
        var monitorBounds = GetMonitorBoundsForRect(Left, Top, Width, Height);
        var bottom = keepAboveTaskbar
            ? workArea.Bottom
            : monitorBounds.Bottom;

        var distance = Math.Abs(bottom - (Top + Height));
        if (distance > threshold)
        {
            return false;
        }

        // 下边吸附只允许改 Y 和高度，完全不改 X：否则贴右边缘时会被水平钳制“回弹”。
        var maxHeight = Math.Max(160, bottom - workArea.Top);
        Height = Math.Min(Height, maxHeight);
        Top = bottom - Height;

        return true;
    }

    /// <summary>没有保存位置时，居中显示在当前所在显示器的工作区。</summary>
    public void MoveToCenter()
    {
        var workArea = GetWorkAreaForRect(Left, Top, Width, Height);
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Top + (workArea.Height - Height) / 2;
    }

    /// <summary>
    /// 临时诊断信息：显示切换时把窗口/显示器/DPI/工作区一次性写入日志。
    /// 定位远程桌面吸附问题后删除，不作为长期 API。
    /// </summary>
    public string GetDisplayDiagnostics()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var scale = GetWindowScale();
            var physical = default(RECT);
            var hasPhysical = hwnd != IntPtr.Zero && GetWindowRect(hwnd, out physical);
            var monitor = hwnd != IntPtr.Zero
                ? MonitorFromWindow(hwnd, MonitorDefaultToNearest)
                : IntPtr.Zero;
            var work = "n/a";
            var bounds = "n/a";
            if (monitor != IntPtr.Zero)
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    work = $"{info.rcWork.Left},{info.rcWork.Top},{info.rcWork.Right},{info.rcWork.Bottom}";
                    bounds = $"{info.rcMonitor.Left},{info.rcMonitor.Top},{info.rcMonitor.Right},{info.rcMonitor.Bottom}";
                }
            }

            var physicalText = hasPhysical
                ? $"{physical.Left},{physical.Top},{physical.Right},{physical.Bottom}"
                : "n/a";
            return $"win=({Left:0.#},{Top:0.#},{Width:0.#}x{Height:0.#}) actual={ActualWidth:0.#}x{ActualHeight:0.#} " +
                   $"scale={scale:0.###} physical={physicalText} monitorBounds={bounds} monitorWork={work} " +
                   $"virtual=({SystemParameters.VirtualScreenLeft:0.#},{SystemParameters.VirtualScreenTop:0.#}," +
                   $"{SystemParameters.VirtualScreenWidth:0.#}x{SystemParameters.VirtualScreenHeight:0.#}) " +
                   $"primaryWork=({SystemParameters.WorkArea.Left:0.#},{SystemParameters.WorkArea.Top:0.#}," +
                   $"{SystemParameters.WorkArea.Width:0.#}x{SystemParameters.WorkArea.Height:0.#})";
        }
        catch (Exception ex)
        {
            return "diagnostics-failed: " + ex.Message;
        }
    }

    private double GetPhysicalDisplayScale()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero && ActualWidth > 1 && GetWindowRect(hwnd, out var rect))
            {
                var physicalWidth = rect.Right - rect.Left;
                if (physicalWidth > 0)
                {
                    var scale = physicalWidth / ActualWidth;
                    if (scale is > 0.5 and < 4.0)
                    {
                        return scale;
                    }
                }
            }
        }
        catch (Exception)
        {
            // 回退到 DPI API。
        }

        return GetWindowScale();
    }

    /// <summary>
    /// 远控/DPI 切换后，WPF 可能保留旧物理尺寸（ActualWidth 与 Width 不一致）。
    /// 这种情况下必须按物理像素重新施加几何，否则吸附会按错误的 DIP 计算。
    /// </summary>
    public bool NeedsDisplayGeometryRepair()
    {
        try
        {
            return ActualWidth > 1 && ActualHeight > 1
                   && (Math.Abs(ActualWidth - Width) > 0.75
                       || Math.Abs(ActualHeight - Height) > 0.75);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 按物理像素重新贴到当前显示器任务栏上沿（或屏幕底边）。
    /// 用于远控/DPI 变化后 WPF 逻辑坐标与物理像素不一致的场景。
    /// </summary>
    public bool SnapToBottomEdgePhysical(double widthDip, double heightDip, bool keepAboveTaskbar)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var current))
            {
                return false;
            }

            var scale = GetPhysicalDisplayScale();
            if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale))
            {
                return false;
            }

            var visible = IsVisibleOnCurrentDesktop();
            var monitor = visible
                ? MonitorFromWindow(hwnd, MonitorDefaultToNearest)
                : MonitorFromPoint(default, MonitorDefaultToPrimary);
            if (monitor == IntPtr.Zero)
            {
                return false;
            }

            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref info))
            {
                return false;
            }

            var targetWidth = Math.Max(1, (int)Math.Round(widthDip * scale));
            var targetHeight = Math.Max(1, (int)Math.Round(heightDip * scale));
            targetWidth = Math.Min(targetWidth, Math.Max(1, info.rcWork.Right - info.rcWork.Left));
            targetHeight = Math.Min(targetHeight, Math.Max(1, info.rcWork.Bottom - info.rcWork.Top));

            var targetX = visible
                ? current.Left
                : info.rcWork.Right - targetWidth - (int)Math.Round(AutoPositionMarginRight * scale);
            targetX = Math.Clamp(
                targetX,
                info.rcWork.Left,
                Math.Max(info.rcWork.Left, info.rcWork.Right - targetWidth));

            var bottom = keepAboveTaskbar ? info.rcWork.Bottom : info.rcMonitor.Bottom;
            var targetY = bottom - targetHeight;
            targetY = Math.Clamp(
                targetY,
                info.rcWork.Top,
                Math.Max(info.rcWork.Top, bottom - targetHeight));

            if (!SetWindowPos(
                    hwnd,
                    IntPtr.Zero,
                    targetX,
                    targetY,
                    targetWidth,
                    targetHeight,
                    SwpNoZOrder | SwpNoActivate))
            {
                return false;
            }

            // SetWindowPos 后同步 WPF 坐标，确保 PositionChanged 持久化的是新位置。
            Left = targetX / scale;
            Top = targetY / scale;
            Width = widthDip;
            Height = heightDip;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 拖拽时的物理像素吸附：只有窗口物理底边已经靠近当前显示器任务栏上沿
    /// （阈值按当前 DPI 换算）才触发，避免每次拖拽都强制拉到底部。
    /// </summary>
    public bool TrySnapToBottomEdgePhysical(
        double widthDip,
        double heightDip,
        bool keepAboveTaskbar,
        double threshold = 100)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var current))
            {
                return false;
            }

            var scale = GetPhysicalDisplayScale();
            if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale))
            {
                return false;
            }

            var monitor = IsVisibleOnCurrentDesktop()
                ? MonitorFromWindow(hwnd, MonitorDefaultToNearest)
                : MonitorFromPoint(default, MonitorDefaultToPrimary);
            if (monitor == IntPtr.Zero)
            {
                return false;
            }

            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref info))
            {
                return false;
            }

            var bottom = keepAboveTaskbar ? info.rcWork.Bottom : info.rcMonitor.Bottom;
            if (Math.Abs(bottom - current.Bottom) > threshold * scale)
            {
                return false;
            }

            return SnapToBottomEdgePhysical(widthDip, heightDip, keepAboveTaskbar);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 判断窗口是否在当前虚拟桌面上留下了足够可见区域。
    /// 用于显示器分辨率/布局变化后，把落到屏幕外的桌宠自动找回来。
    /// </summary>
    public bool IsVisibleOnCurrentDesktop(double minVisibleSize = 120)
    {
        // 远程桌面/DPI 切换时 WPF 的 Left/Top/Width 与物理窗口可能不一致；
        // 可见性以 Win32 物理矩形和系统虚拟屏为准。
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var physical))
            {
                var scale = GetPhysicalDisplayScale();
                if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale))
                {
                    scale = 1.0;
                }

                var virtualLeft = GetSystemMetrics(SmXVirtualScreen);
                var virtualTop = GetSystemMetrics(SmYVirtualScreen);
                var virtualRight = virtualLeft + GetSystemMetrics(SmCxVirtualScreen);
                var virtualBottom = virtualTop + GetSystemMetrics(SmCyVirtualScreen);
                var visibleWidth = Math.Min(physical.Right, virtualRight) - Math.Max(physical.Left, virtualLeft);
                var visibleHeight = Math.Min(physical.Bottom, virtualBottom) - Math.Max(physical.Top, virtualTop);
                var threshold = minVisibleSize * scale;
                var physicalWidth = physical.Right - physical.Left;
                var physicalHeight = physical.Bottom - physical.Top;
                return visibleWidth >= Math.Min(physicalWidth, threshold)
                       && visibleHeight >= Math.Min(physicalHeight, threshold);
            }
        }
        catch (Exception)
        {
            // 回退到 WPF 逻辑坐标。
        }

        if (!IsFinite(Left) || !IsFinite(Top) || !IsFinite(Width) || !IsFinite(Height))
        {
            return false;
        }

        var logicalVirtualLeft = SystemParameters.VirtualScreenLeft;
        var logicalVirtualTop = SystemParameters.VirtualScreenTop;
        var logicalVirtualRight = logicalVirtualLeft + SystemParameters.VirtualScreenWidth;
        var logicalVirtualBottom = logicalVirtualTop + SystemParameters.VirtualScreenHeight;

        var logicalVisibleWidth = Math.Min(Left + Width, logicalVirtualRight) - Math.Max(Left, logicalVirtualLeft);
        var logicalVisibleHeight = Math.Min(Top + Height, logicalVirtualBottom) - Math.Max(Top, logicalVirtualTop);
        return logicalVisibleWidth >= Math.Min(Width, minVisibleSize)
               && logicalVisibleHeight >= Math.Min(Height, minVisibleSize);
    }

    /// <summary>把窗口限制在虚拟桌面范围内（避免拖到屏幕外再也找不回来）。</summary>
    public void ClampToVirtualScreen()
    {
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        var visibleMargin = 60.0;
        var left = Math.Min(Math.Max(Left, virtualLeft - Width + visibleMargin), virtualRight - visibleMargin);
        var top = Math.Min(Math.Max(Top, virtualTop - 10), virtualBottom - visibleMargin);

        if (Math.Abs(left - Left) > 0.5)
        {
            Left = left;
        }

        if (Math.Abs(top - Top) > 0.5)
        {
            Top = top;
        }
    }

    public void CloseSafely()
    {
        try
        {
            Close();
        }
        catch (Exception)
        {
            // 窗口可能已被关闭/进程正在退出，忽略。
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        WindowInterop.ApplyToolWindowStyle(_hwnd);
        ReassertTopmost();
        var source = HwndSource.FromHwnd(_hwnd);
        source?.AddHook(WndProc);
        ApplyInteropStyles();
    }

    /// <summary>用 Win32 重新断言置顶（WPF Topmost 在远程桌面/其它顶层窗口出现时偶发被覆盖）。</summary>
    public void ReassertTopmost()
    {
        if (_hwnd != IntPtr.Zero)
        {
            WindowInterop.SetTopmost(_hwnd, Topmost);
        }
    }

    private void ApplyInteropStyles()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        WindowInterop.SetClickThrough(_hwnd, _clickThrough);
        // 非穿透模式下提供整窗拖动层（不拦截 WebView2 时也能拖动窗口）。
        _dragLayer.Visibility = _clickThrough ? Visibility.Collapsed : Visibility.Visible;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmNcHitTest && _clickThrough)
        {
            handled = true;
            return new IntPtr(HtTransparent);
        }

        return IntPtr.Zero;
    }

    private void OnDragLayerMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
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
            // 鼠标已松开等边界情况，忽略。
        }
        finally
        {
            PositionChanged?.Invoke(this, new Live2DPositionChangedEventArgs(Left, Top));
        }

        e.Handled = true;
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
