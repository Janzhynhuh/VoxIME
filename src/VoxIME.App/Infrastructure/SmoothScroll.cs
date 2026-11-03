using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using VoxIME.Core.Services;

namespace VoxIME.App.Infrastructure;

/// <summary>
/// 高性能平滑滚动（视觉层 / 逻辑层分离，参考 FluentWpfCore 的 v3 方案）：
/// - 视觉层：每帧只改 <see cref="ScrollContentPresenter"/> 的 TranslateTransform，不触发布局，
///   跟随显示器刷新率更新；
/// - 逻辑层：约 24Hz 低频调用 <c>ScrollToVerticalOffset</c> 同步真实偏移，
///   保证滚动条、虚拟化与命中测试正确。
/// 这样就避免了「每一帧都 ScrollToVerticalOffset → 每帧布局」导致的卡顿。
/// 动画曲线来自 <see cref="ScrollMotion"/>（300ms Fluent ease-out）。
/// </summary>
public static class SmoothScroll
{
    /// <summary>一格滚轮（120 单位）对应的目标滚动距离（像素）。</summary>
    private const double PixelsPerNotch = 100;

    /// <summary>逻辑偏移同步间隔（秒）：约 24Hz，避免高频布局。</summary>
    private const double LogicalSyncIntervalSeconds = 1d / 24d;

    private static readonly Dictionary<ScrollViewer, ScrollState> States = new();
    private static bool _renderingHooked;

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    /// <summary>
    /// 在指定 ScrollViewer 上滚动指定像素（负值向上）。滚轮处理器与自动化测试共用此入口。
    /// </summary>
    public static void ScrollBy(ScrollViewer viewer, double deltaPixels)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        if (viewer.ScrollableHeight <= 0.5 || Math.Abs(deltaPixels) < 0.01)
        {
            return;
        }

        var state = GetState(viewer);
        EnsurePresenter(viewer, state);

        if (state.Presenter is null)
        {
            // 模板尚未应用（极少数情况）：退化为一次性滚动，避免吞掉滚轮。
            viewer.ScrollToVerticalOffset(Math.Clamp(
                viewer.VerticalOffset + deltaPixels, 0, viewer.ScrollableHeight));
            return;
        }

        var now = Environment.TickCount64;
        if (!state.Animating)
        {
            state.VisualOffset = viewer.VerticalOffset;
            state.TargetOffset = state.VisualOffset;
        }

        state.TargetOffset = Math.Clamp(state.TargetOffset + deltaPixels, 0, viewer.ScrollableHeight);
        state.StartOffset = state.VisualOffset;
        state.StartTick = now;
        state.LastLogicalSyncTick = now;
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

    /// <summary>每帧只更新视觉位移；逻辑偏移按 24Hz 低频同步。</summary>
    private static void OnRendering(object? sender, EventArgs e)
    {
        var hasActive = false;
        var now = Environment.TickCount64;

        foreach (var (viewer, state) in States)
        {
            if (!state.Animating || state.Presenter is null)
            {
                continue;
            }

            hasActive = true;
            var elapsed = (now - state.StartTick) / 1000d;
            var step = ScrollMotion.Next(state.StartOffset, state.TargetOffset, elapsed);
            state.VisualOffset = Math.Clamp(step.Offset, 0, viewer.ScrollableHeight);
            ApplyVisualDelta(viewer, state);

            if (step.Completed)
            {
                // 落位时保留视觉补偿，等 ScrollChanged（布局完成）确认逻辑偏移后再清除，
                // 避免最后一步出现视觉回跳。
                state.VisualOffset = state.TargetOffset;
                SyncLogicalOffset(viewer, state);
                state.Animating = false;
            }
            else if ((now - state.LastLogicalSyncTick) / 1000d >= LogicalSyncIntervalSeconds)
            {
                SyncLogicalOffset(viewer, state);
                state.LastLogicalSyncTick = now;
            }
        }

        if (!hasActive)
        {
            UnhookRendering();
        }
    }

    /// <summary>
    /// 外部滚动（拖动滚动条、键盘、代码调用 ScrollToVerticalOffset 等）：
    /// 立即放弃视觉补偿并回到逻辑偏移，避免视觉与逻辑错位。
    /// </summary>
    private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer viewer || !States.TryGetValue(viewer, out var state))
        {
            return;
        }

        if (state.Syncing || state.Animating)
        {
            return;
        }

        state.VisualOffset = viewer.VerticalOffset;
        state.TargetOffset = state.VisualOffset;
        ResetVisualOffset(viewer, state);
    }

    private static void ApplyVisualDelta(ScrollViewer viewer, ScrollState state)
    {
        if (state.Presenter is null)
        {
            return;
        }

        var delta = state.VisualOffset - viewer.VerticalOffset;
        if (Math.Abs(delta) < 0.01)
        {
            if (state.Transform is not null)
            {
                state.Transform.Y = 0;
            }

            return;
        }

        state.Transform ??= new TranslateTransform();
        if (!ReferenceEquals(state.Presenter.RenderTransform, state.Transform))
        {
            state.OriginalTransform = state.Presenter.RenderTransform;
            state.Presenter.RenderTransform = state.Transform;
        }

        state.Transform.Y = -delta;
    }

    private static void SyncLogicalOffset(ScrollViewer viewer, ScrollState state)
    {
        state.Syncing = true;
        try
        {
            viewer.ScrollToVerticalOffset(state.VisualOffset);

            // ScrollToVerticalOffset 会立刻更新 VerticalOffset，但内容要等下一次布局才真正移动。
            // 如果此时就把视觉补偿清零，会先闪一帧旧位置；这里强制完成布局，保证
            // “逻辑位置、实际呈现、视觉补偿”三者在同一帧内一致，避免组件抽搐。
            viewer.UpdateLayout();
        }
        finally
        {
            state.Syncing = false;
        }

        // 布局完成后重算视觉补偿量，保证画面不跳。
        ApplyVisualDelta(viewer, state);
    }

    private static void ResetVisualOffset(ScrollViewer viewer, ScrollState state)
    {
        if (state.Presenter is null)
        {
            return;
        }

        if (state.Transform is not null && ReferenceEquals(state.Presenter.RenderTransform, state.Transform))
        {
            state.Presenter.RenderTransform = state.OriginalTransform;
        }

        state.Transform = null;
        state.OriginalTransform = null;
    }

    private static void EnsurePresenter(ScrollViewer viewer, ScrollState state)
    {
        if (state.Presenter is not null && state.Presenter.IsLoaded)
        {
            return;
        }

        state.Presenter = FindDescendant<ScrollContentPresenter>(viewer);
        if (state.Presenter is null)
        {
            return;
        }

        // 位移后必须裁剪在视口内，避免内容画到 ScrollViewer 外面。
        state.Presenter.ClipToBounds = true;
        state.OriginalTransform = state.Presenter.RenderTransform;
        state.Transform = null;
    }

    private static void Stop(ScrollViewer viewer)
    {
        if (States.TryGetValue(viewer, out var state))
        {
            state.Animating = false;
            ResetVisualOffset(viewer, state);
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

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match)
        {
            return match;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (FindDescendant<T>(VisualTreeHelper.GetChild(root, i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private sealed class ScrollState
    {
        public bool Animating { get; set; }

        /// <summary>当前视觉偏移（用户看到的位置）。</summary>
        public double VisualOffset { get; set; }

        /// <summary>动画目标偏移。</summary>
        public double TargetOffset { get; set; }

        /// <summary>动画起点偏移。</summary>
        public double StartOffset { get; set; }

        /// <summary>动画起点 Environment.TickCount64。</summary>
        public long StartTick { get; set; }

        /// <summary>上次同步逻辑偏移的时间戳。</summary>
        public long LastLogicalSyncTick { get; set; }

        /// <summary>正在同步逻辑偏移时置位，避免被 ScrollChanged 误判为外部滚动。</summary>
        public bool Syncing { get; set; }

        public ScrollContentPresenter? Presenter { get; set; }

        public TranslateTransform? Transform { get; set; }

        public Transform? OriginalTransform { get; set; }
    }
}
