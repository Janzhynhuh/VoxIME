using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using VoxIME.Core.Services;

namespace VoxIME.App.Infrastructure;

/// <summary>
/// 滚动惯性：把鼠标滚轮滚动改成与显示器刷新同步、按时间衰减的平滑滚动。
/// 用法：XAML 给 ScrollViewer 挂 <c>inf:SmoothScroll.IsEnabled="True"</c>；
/// 挂在 ListBox 等容器上时会自动找到内部 ScrollViewer。只处理纵向滚动，
/// ScrollBar 拖拽与文本框滚动保持系统原生行为。
/// 窗口阴影已改为静态层（<see cref="WindowShadowLayer"/>），滚动不再触碰任何 Effect。
/// </summary>
public static class SmoothScroll
{
    /// <summary>一格滚轮（120 单位）对应的目标滚动距离（像素）。</summary>
    private const double PixelsPerNotch = 100;

    /// <summary>指数衰减的时间常数（秒）：越小越跟手，越大惯性越长。</summary>
    private const double TimeConstantSeconds = 0.09;

    private static readonly Dictionary<ScrollViewer, ScrollState> States = new();
    private static bool _renderingHooked;

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var enabled = e.NewValue is true;
        if (d is ScrollViewer viewer)
        {
            if (enabled)
            {
                Enable(viewer);
            }
            else
            {
                Disable(viewer);
            }

            return;
        }

        if (d is FrameworkElement element)
        {
            if (enabled)
            {
                element.Loaded += OnElementLoaded;
            }
            else
            {
                element.Loaded -= OnElementLoaded;
            }
        }
    }

    private static void OnElementLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && GetIsEnabled(element)
            && FindDescendantScrollViewer(element) is { } viewer)
        {
            Enable(viewer);
        }
    }

    private static void Enable(ScrollViewer viewer)
    {
        viewer.PreviewMouseWheel -= OnPreviewMouseWheel;
        viewer.PreviewMouseWheel += OnPreviewMouseWheel;
        viewer.PreviewMouseDown -= OnPreviewMouseDown;
        viewer.PreviewMouseDown += OnPreviewMouseDown;
        viewer.Unloaded -= OnViewerUnloaded;
        viewer.Unloaded += OnViewerUnloaded;
    }

    private static void Disable(ScrollViewer viewer)
    {
        viewer.PreviewMouseWheel -= OnPreviewMouseWheel;
        viewer.PreviewMouseDown -= OnPreviewMouseDown;
        viewer.Unloaded -= OnViewerUnloaded;
        Stop(viewer);
    }

    private static void OnViewerUnloaded(object sender, RoutedEventArgs e) => Stop((ScrollViewer)sender);

    private static void OnPreviewMouseDown(object sender, MouseButtonEventArgs e) => Stop((ScrollViewer)sender);

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer viewer || viewer.ScrollableHeight <= 0.5)
        {
            return;
        }

        // 文本输入控件自己的滚轮行为优先（例如多行提示词输入框）。
        if (IsInsideTextInput(e.OriginalSource as DependencyObject))
        {
            return;
        }

        e.Handled = true;
        var state = GetState(viewer);
        var from = state.Animating ? state.Target : viewer.VerticalOffset;
        state.Target = Math.Clamp(from - e.Delta / 120d * PixelsPerNotch, 0, viewer.ScrollableHeight);
        state.Direction = ScrollMotion.DirectionFor(viewer.VerticalOffset, state.Target);
        if (state.Direction == 0)
        {
            // 目标与当前一致（例如已在顶部/底部继续滚）：不用启动动画。
            state.Animating = false;
            return;
        }

        if (!state.Animating)
        {
            state.Animating = true;
            state.LastTimestamp = null;
            HookRendering();
        }
    }

    /// <summary>按显示器刷新节奏推进动画，使用时间增量做指数衰减（帧率无关，避免定时器抖动）。</summary>
    private static void OnRendering(object? sender, EventArgs e)
    {
        var now = e is RenderingEventArgs args ? args.RenderingTime.TotalSeconds : 0;
        var hasActive = false;

        foreach (var (viewer, state) in States)
        {
            if (!state.Animating)
            {
                continue;
            }

            hasActive = true;
            var dt = state.LastTimestamp is double last
                ? Math.Clamp(now - last, 1d / 240d, 0.12d)
                : 1d / 60d;
            state.LastTimestamp = now;

            var alpha = 1d - Math.Exp(-dt / TimeConstantSeconds);
            var step = ScrollMotion.Next(viewer.VerticalOffset, state.Target, state.Direction, alpha);
            viewer.ScrollToVerticalOffset(step.Offset);

            if (step.Completed)
            {
                state.Animating = false;
                state.Direction = 0;
                state.LastTimestamp = null;
            }
        }

        if (!hasActive)
        {
            UnhookRendering();
        }
    }

    private static void Stop(ScrollViewer viewer)
    {
        if (States.TryGetValue(viewer, out var state))
        {
            state.Animating = false;
            state.Direction = 0;
            States.Remove(viewer);
        }

        if (!States.Values.Any(s => s.Animating))
        {
            UnhookRendering();
        }
    }

    private static ScrollState GetState(ScrollViewer viewer)
    {
        if (!States.TryGetValue(viewer, out var state))
        {
            state = new ScrollState();
            States[viewer] = state;
        }

        return state;
    }

    private static void HookRendering()
    {
        if (_renderingHooked)
        {
            return;
        }

        _renderingHooked = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private static void UnhookRendering()
    {
        if (!_renderingHooked)
        {
            return;
        }

        _renderingHooked = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private static bool IsInsideTextInput(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is TextBoxBase)
            {
                return true;
            }

            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return false;
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer)
        {
            return viewer;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (FindDescendantScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private sealed class ScrollState
    {
        public bool Animating { get; set; }

        public double Target { get; set; }

        public double? LastTimestamp { get; set; }

        /// <summary>期望移动方向：+1 向下、-1 向上、0 未定。</summary>
        public int Direction { get; set; }
    }
}
