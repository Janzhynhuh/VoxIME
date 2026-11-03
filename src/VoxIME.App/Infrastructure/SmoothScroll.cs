using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using VoxIME.Core.Services;

namespace VoxIME.App.Infrastructure;

/// <summary>
/// 平滑滚动：PCL 风格的 300ms Fluent ease-out（1-(1-t)^6）步进。
/// 用法：XAML 给 ScrollViewer 挂 <c>inf:SmoothScroll.IsEnabled="True"</c>；
/// 挂在 ListBox 等容器上时会自动找到内部 ScrollViewer。只处理纵向滚动，
/// ScrollBar 拖拽与文本框滚动保持系统原生行为。
///
/// 与旧实现的区别：不再按"当前偏移 → 目标"做逐帧指数逼近，而是滚轮事件累积目标、
/// 重锚起点与计时，然后按固定时长缓出。这样既没有反向补帧（不会回弹），
/// 也接近 PCL 的动画手感；绘制帧率仍受 WPF 透明窗口软件渲染上限约束。
/// </summary>
public static class SmoothScroll
{
    /// <summary>一格滚轮（120 单位）对应的目标滚动距离（像素）。</summary>
    private const double PixelsPerNotch = 100;

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
        var current = viewer.VerticalOffset;
        var delta = -e.Delta / 120d * PixelsPerNotch;

        // 动画中继续滚轮：在已累积的目标上继续加，起点重锚到当前实际偏移与当前时间，
        // 这样连续滚动会保持"每段 300ms 缓出"的跟手感（PCL 的动画组叠加思路）。
        var from = state.Animating ? state.Target : current;
        state.Target = Math.Clamp(from + delta, 0, viewer.ScrollableHeight);
        state.StartOffset = current;
        state.StartTick = Environment.TickCount64;

        if (Math.Abs(state.Target - current) <= 0.01)
        {
            state.Animating = false;
            return;
        }

        state.Animating = true;
        HookRendering();
    }

    /// <summary>按渲染帧推进：固定 300ms、Fluent ease-out，单调逼近目标，时间到直接落位。</summary>
    private static void OnRendering(object? sender, EventArgs e)
    {
        var hasActive = false;

        foreach (var (viewer, state) in States)
        {
            if (!state.Animating)
            {
                continue;
            }

            hasActive = true;
            var elapsed = (Environment.TickCount64 - state.StartTick) / 1000d;
            var step = ScrollMotion.Next(state.StartOffset, state.Target, elapsed);
            viewer.ScrollToVerticalOffset(step.Offset);

            if (step.Completed)
            {
                state.Animating = false;
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

        /// <summary>本轮动画的目标偏移。</summary>
        public double Target { get; set; }

        /// <summary>本轮动画的起点偏移。</summary>
        public double StartOffset { get; set; }

        /// <summary>本轮动画起点的 Environment.TickCount64。</summary>
        public long StartTick { get; set; }
    }
}
