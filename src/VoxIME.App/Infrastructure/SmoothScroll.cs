using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using VoxIME.Core.Services;

namespace VoxIME.App.Infrastructure;

/// <summary>
/// 高性能平滑滚动（视觉层 / 逻辑层分离，参考 FluentWpfCore 的 SmoothScrollViewer）：
/// - 视觉层：每帧只改 ScrollViewer.Content 的 TranslateTransform（不触发布局，跟随刷新率）；
///   位移加在内容上而不是 ScrollContentPresenter 上，这样 presenter 的视口裁剪保持不动，
///   内容不会滑出滚动区域；
/// - 逻辑层：动画结束后调用 <c>ScrollToVerticalOffset</c> 并强制 UpdateLayout 同步真实偏移，
///   滚动过程中完全不触发布局（滚动条在停止后跟上）。
/// 运动模型来自 <see cref="ScrollMotion"/>：滚轮累加目标偏移，视觉偏移按指数曲线连续跟随目标，
/// 消除"每格重新起步再停下"的脉冲感。
/// </summary>
public static class SmoothScroll
{
    /// <summary>一格滚轮（120 单位）对应的目标滚动距离（像素）。</summary>
    private const double PixelsPerNotch = 100;

    private static readonly Dictionary<ScrollViewer, ScrollState> States = new();
    private static bool _renderingHooked;

    /// <summary>滚动动画的实时帧率（约每 250ms 触发一次，供设置窗口显示/诊断）。</summary>
    public static event EventHandler<double>? FrameRateChanged;

    private static long _fpsWindowStart;
    private static int _fpsFrames;
    private static bool _fpsWindowActive;

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
        EnsureContent(viewer, state);

        if (state.Content is null)
        {
            // 模板尚未应用（极少数情况）：退化为一次性滚动，避免吞掉滚轮。
            viewer.ScrollToVerticalOffset(Math.Clamp(
                viewer.VerticalOffset + deltaPixels, 0, viewer.ScrollableHeight));
            return;
        }

        var now = Environment.TickCount64;
        if (!state.Animating)
        {
            // 从当前真实位置起滚；连续滚轮时目标持续领先，视觉偏移连续跟随。
            state.VisualOffset = viewer.VerticalOffset;
            state.TargetOffset = state.VisualOffset;
        }

        state.TargetOffset = Math.Clamp(state.TargetOffset + deltaPixels, 0, viewer.ScrollableHeight);
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

    /// <summary>每帧只更新视觉位移；逻辑偏移按 24Hz 低频同步。</summary>
    private static void OnRendering(object? sender, EventArgs e)
    {
        var hasActive = false;
        var now = Environment.TickCount64;

        foreach (var (viewer, state) in States)
        {
            if (!state.Animating || state.Content is null)
            {
                continue;
            }

            hasActive = true;
            var elapsed = (now - state.LastTick) / 1000d;
            state.LastTick = now;
            var step = ScrollMotion.Next(state.VisualOffset, state.TargetOffset, elapsed);
            state.VisualOffset = step.Offset;
            ApplyVisualDelta(viewer, state);

            if (step.Completed)
            {
                // 滚动过程中不触发布局：只在动画结束时同步一次逻辑偏移，避免布局抖动。
                SyncLogicalOffset(viewer, state);
                state.Animating = false;
            }
        }

        if (hasActive)
        {
            if (!_fpsWindowActive)
            {
                _fpsWindowActive = true;
                _fpsWindowStart = now;
                _fpsFrames = 0;
            }

            _fpsFrames++;
            var elapsedMs = now - _fpsWindowStart;
            if (elapsedMs >= 250)
            {
                EmitFrameRate(_fpsFrames * 1000d / elapsedMs);
                _fpsWindowStart = now;
                _fpsFrames = 0;
            }
        }
        else if (_fpsWindowActive)
        {
            var elapsedMs = now - _fpsWindowStart;
            if (elapsedMs > 0)
            {
                EmitFrameRate(_fpsFrames * 1000d / elapsedMs);
            }

            _fpsWindowActive = false;
            _fpsFrames = 0;
        }

        if (!hasActive)
        {
            UnhookRendering();
        }
    }

    private static void EmitFrameRate(double fps) => FrameRateChanged?.Invoke(null, Math.Round(fps));

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
        if (state.Content is null)
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
        if (!ReferenceEquals(state.Content.RenderTransform, state.Transform))
        {
            state.OriginalTransform = state.Content.RenderTransform;
            state.OriginalTransformOrigin = state.Content.RenderTransformOrigin;
            state.Content.RenderTransformOrigin = new Point(0, 0);
            state.Content.RenderTransform = state.Transform;
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
            // 强制在本帧内完成布局，再按实际呈现重算视觉补偿，保证三者同帧一致。
            viewer.UpdateLayout();
        }
        finally
        {
            state.Syncing = false;
        }

        ApplyVisualDelta(viewer, state);
    }

    private static void ResetVisualOffset(ScrollViewer viewer, ScrollState state)
    {
        _ = viewer;
        if (state.Content is null)
        {
            return;
        }

        if (state.Transform is not null && ReferenceEquals(state.Content.RenderTransform, state.Transform))
        {
            state.Content.RenderTransform = state.OriginalTransform;
            state.Content.RenderTransformOrigin = state.OriginalTransformOrigin;
        }

        state.Transform = null;
        state.OriginalTransform = null;
    }

    private static void EnsureContent(ScrollViewer viewer, ScrollState state)
    {
        if (state.Content is not null && state.Content.IsLoaded)
        {
            return;
        }

        state.Content = viewer.Content as FrameworkElement;
        if (state.Content is null)
        {
            // 少数模板会把内容包一层，取 presenter 的第一个可视子元素作为位移目标。
            var presenter = FindDescendant<ScrollContentPresenter>(viewer);
            state.Content = presenter is null
                ? null
                : VisualTreeHelper.GetChild(presenter, 0) as FrameworkElement;
        }

        if (state.Content is null)
        {
            return;
        }

        // 视口裁剪由 ScrollContentPresenter 负责；内容自身再有 transform 不会把裁剪区域带走。
        if (FindDescendant<ScrollContentPresenter>(viewer) is { } clipHost)
        {
            clipHost.ClipToBounds = true;
        }

        state.OriginalTransform = state.Content.RenderTransform;
        state.OriginalTransformOrigin = state.Content.RenderTransformOrigin;
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

        /// <summary>滚轮累加出来的目标偏移；视觉偏移连续跟随它。</summary>
        public double TargetOffset { get; set; }

        /// <summary>上一帧时间戳（Environment.TickCount64）。</summary>
        public long LastTick { get; set; }

        /// <summary>正在同步逻辑偏移时置位，避免被 ScrollChanged 误判为外部滚动。</summary>
        public bool Syncing { get; set; }

        /// <summary>被位移的 ScrollViewer.Content。</summary>
        public FrameworkElement? Content { get; set; }

        public TranslateTransform? Transform { get; set; }

        public Transform? OriginalTransform { get; set; }

        public Point OriginalTransformOrigin { get; set; }
    }
}
