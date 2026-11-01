using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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
    private const double AutoPositionMarginBottom = 18;
    private const uint MonitorDefaultToNearest = 2;

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

    private readonly Grid _contentHost;
    private readonly FallbackAvatar _avatar;
    private readonly Border _dragLayer;
    private readonly BubbleView _bubble;
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

    /// <summary>应用尺寸/位置/透明度/置顶/穿透。snapToEdge=true 时吸附主屏工作区右下角。</summary>
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
        ClickThrough = clickThrough;
        _bubble.UpdateMaxWidth(Width);

        if (snapToEdge)
        {
            if (hasSavedPosition && IsFinite(left) && IsFinite(top))
            {
                // 记住用户上次贴近的边（顶部/左侧等），而不是每次都强制回右下角。
                Left = left;
                Top = top;
                SnapToNearestEdge(keepAboveTaskbar);
            }
            else
            {
                MoveToAutoPosition(keepAboveTaskbar);
                // 自动贴边时位置由下边界反推，不能再做通用“把顶部拉回屏幕”的纵向钳制；
                // 否则窗口越高越会被推到底部以下（表现为模型只剩半个头、穿到屏幕外）。
                ClampToVirtualScreen(horizontalOnly: true);
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

        // 贴边时窗口必须完整落在目标边界内：否则窗口比屏幕还高，
        // 顶部会被裁掉一截，用户看到的就是“只剩半个头”。
        var maxWidth = Math.Max(160, workArea.Width - AutoPositionMarginRight);
        var maxHeight = Math.Max(160, bottom - workArea.Top - AutoPositionMarginBottom);
        Width = Math.Min(Width, maxWidth);
        Height = Math.Min(Height, maxHeight);

        Left = workArea.Right - Width - AutoPositionMarginRight;
        Top = bottom - Height - AutoPositionMarginBottom;
    }

    /// <summary>
    /// 拖到所在显示器边缘附近（默认 60 DIP 内）时吸附到该显示器最近的边缘；
    /// 不在边缘附近则保持用户放下时的位置，避免“随便拖也被强制拉回边上”。
    /// keepAboveTaskbar 只影响底边。返回是否发生了吸附。
    /// </summary>
    public bool SnapToNearestEdge(bool keepAboveTaskbar = true, double threshold = 60)
    {
        var workArea = GetWorkAreaForRect(Left, Top, Width, Height);
        var monitorBounds = GetMonitorBoundsForRect(Left, Top, Width, Height);
        var bottom = keepAboveTaskbar
            ? workArea.Bottom
            : monitorBounds.Bottom;

        var right = workArea.Right;
        var leftDistance = Math.Abs(Left - workArea.Left);
        var rightDistance = Math.Abs(right - (Left + Width));
        var topDistance = Math.Abs(Top - workArea.Top);
        var bottomDistance = Math.Abs(bottom - (Top + Height));

        var min = Math.Min(Math.Min(leftDistance, rightDistance), Math.Min(topDistance, bottomDistance));
        if (min > threshold)
        {
            return false;
        }

        // 只在真正吸附时压缩到可用区域，避免“窗口比屏幕高/宽”时无论贴哪边都会被裁掉。
        var maxWidth = Math.Max(160, workArea.Width - AutoPositionMarginRight);
        var maxHeight = Math.Max(160, bottom - workArea.Top - AutoPositionMarginBottom);
        Width = Math.Min(Width, maxWidth);
        Height = Math.Min(Height, maxHeight);

        if (min == bottomDistance)
        {
            Top = bottom - Height - AutoPositionMarginBottom;
            Left = Math.Min(Math.Max(Left, workArea.Left), Math.Max(workArea.Left, right - Width));
        }
        else if (min == rightDistance)
        {
            Left = right - Width - AutoPositionMarginRight;
            Top = Math.Min(Math.Max(Top, workArea.Top), Math.Max(workArea.Top, bottom - Height));
        }
        else if (min == topDistance)
        {
            Top = workArea.Top;
            Left = Math.Min(Math.Max(Left, workArea.Left), Math.Max(workArea.Left, right - Width));
        }
        else
        {
            Left = workArea.Left;
            Top = Math.Min(Math.Max(Top, workArea.Top), Math.Max(workArea.Top, bottom - Height));
        }

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
    /// 把窗口限制在虚拟桌面范围内（避免拖到屏幕外再也找不回来）。
    /// preserveBottomAboveTaskbar=true 时优先保证窗口底边在任务栏上方，允许顶部超出屏幕。
    /// horizontalOnly=true 时只做水平钳制（自动贴边由下边界决定纵向位置）。
    /// </summary>
    public void ClampToVirtualScreen(bool preserveBottomAboveTaskbar = false, bool horizontalOnly = false)
    {
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        var visibleMargin = 60.0;
        var left = Math.Min(Math.Max(Left, virtualLeft - Width + visibleMargin), virtualRight - visibleMargin);
        if (Math.Abs(left - Left) > 0.5)
        {
            Left = left;
        }

        if (horizontalOnly)
        {
            return;
        }

        double top;
        if (preserveBottomAboveTaskbar)
        {
            // 窗口可能比工作区还高：此时只限制“底边不超过任务栏顶部”，
            // 不再把顶部强行拉回屏幕内，否则会把底边重新推回任务栏区域。
            var workArea = SystemParameters.WorkArea;
            var maxTop = workArea.Bottom - Height - AutoPositionMarginBottom;
            top = Math.Min(Top, maxTop);
        }
        else
        {
            top = Math.Min(Math.Max(Top, virtualTop - 10), virtualBottom - visibleMargin);
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
        var source = HwndSource.FromHwnd(_hwnd);
        source?.AddHook(WndProc);
        ApplyInteropStyles();
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
