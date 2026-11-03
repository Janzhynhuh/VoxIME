using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using VoxIME.Core.Services;

namespace VoxIME.App.Infrastructure;

/// <summary>
/// 滚轮平滑滚动：和拖动滚动条使用同一条路径——逐帧调用
/// <c>ScrollViewer.ScrollToVerticalOffset</c>，滚动条/内容/命中测试天然每帧同步。
/// 动画曲线由 <see cref="ScrollMotion"/> 提供（滚轮累加目标、视觉偏移按指数曲线
/// 连续跟随），因此既没有"每格重新起步"的脉冲感，也不会出现滚动条停住、最后跳一下。
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

    /// <summary>在指定 ScrollViewer 上滚动指定像素（负值向上）。滚轮处理器与自动化测试共用此入口。</summary>
    public static void ScrollBy(ScrollViewer viewer, double deltaPixels)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        if (viewer.ScrollableHeight <= 0.5 || Math.Abs(deltaPixels) < 0.01)
        {
            return;
        }

        var state = GetState(viewer);
        var now = Environment.TickCount64;
        if (!state.Animating)
        {
            // 从当前真实偏移起滚；连续滚轮时目标持续领先，视觉偏移连续跟随。
            state.Offset = viewer.VerticalOffset;
            state.Target = state.Offset;
        }

        state.Target = Math.Clamp(state.Target + deltaPixels, 0, viewer.ScrollableHeight);
        state.LastTick = now;
        state.Animating = true;
        HookRendering();
    }

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
        viewer.ScrollChanged -= OnScrollChanged;
        viewer.ScrollChanged += OnScrollChanged;
        viewer.Unloaded -= OnViewerUnloaded;
        viewer.Unloaded += OnViewerUnloaded;
    }

    private static void Disable(ScrollViewer viewer)
    {
        viewer.PreviewMouseWheel -= OnPreviewMouseWheel;
        viewer.PreviewMouseDown -= OnPreviewMouseDown;
        viewer.ScrollChanged -= OnScrollChanged;
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
        ScrollBy(viewer, -e.Delta / 120d * PixelsPerNotch);
    }

    /// <summary>逐帧把真实偏移推向目标——与拖动滚动条走同一路径，滚动条实时跟随。</summary>
    private static void OnRendering(object? sender, EventArgs e)
    {
        var hasActive = false;
        var now = Environment.TickCount64;

        foreach (var (viewer, state) in States)
        {
            if (!state.Animating)
            {
                continue;
            }

            hasActive = true;
            var elapsed = (now - state.LastTick) / 1000d;
            state.LastTick = now;
            var step = ScrollMotion.Next(state.Offset, state.Target, elapsed);
            state.Offset = step.Offset;
            viewer.ScrollToVerticalOffset(state.Offset);

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

    /// <summary>外部滚动（拖动滚动条/键盘等）时放弃当前动画，跟随真实偏移。</summary>
    private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer viewer || !States.TryGetValue(viewer, out var state) || state.Animating)
        {
            return;
        }

        state.Offset = viewer.VerticalOffset;
        state.Target = state.Offset;
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
            state = new ScrollState { Offset = viewer.VerticalOffset };
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

        /// <summary>当前滚动偏移（已提交给 ScrollViewer）。</summary>
        public double Offset { get; set; }

        /// <summary>滚轮累加出来的目标偏移。</summary>
        public double Target { get; set; }

        public long LastTick { get; set; }
    }
}
