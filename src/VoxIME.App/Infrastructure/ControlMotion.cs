using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace VoxIME.App.Infrastructure;

/// <summary>
/// 通用控件动效：把控件状态映射到模板里的标准部件，统一用 Motion 的曲线做缓动。
/// 模板只要提供 PART_HoverLayer / PART_PressedLayer / PART_SelectedLayer / PART_Indicator /
/// PART_Track / PART_TrackOn / PART_Thumb，就能自动获得动效；缺失的部件会被静默跳过。
/// 不针对单个按钮写动画，也不做缩放/旋转/布局位移。
/// </summary>
public static class ControlMotion
{
    private sealed class State
    {
        public UIElement? HoverLayer;
        public UIElement? PressedLayer;
        public UIElement? SelectedLayer;
        public UIElement? Indicator;
        public FrameworkElement? Track;
        public FrameworkElement? TrackOn;
        public FrameworkElement? Thumb;
        public TranslateTransform? ThumbTransform;
    }

    private static readonly ConditionalWeakTable<FrameworkElement, State> States = new();

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled",
        typeof(bool),
        typeof(ControlMotion),
        new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || e.NewValue is not true || States.TryGetValue(element, out _))
        {
            return;
        }

        var state = new State();
        States.Add(element, state);

        element.Loaded += (_, _) =>
        {
            ResolveParts(element, state);
            Update(element, state, initial: true);
        };

        switch (element)
        {
            case ToggleButton toggle:
                WireToggle(toggle, state);
                break;
            case ButtonBase button:
                WireButton(button, state);
                break;
            case ListBoxItem item:
                WireListBoxItem(item, state);
                break;
        }
    }

    private static void WireButton(ButtonBase button, State state)
    {
        button.MouseEnter += (_, _) => Update(button, state, initial: false);
        button.MouseLeave += (_, _) => Update(button, state, initial: false);

        // IsPressed 在 mouse down 之后才更新，统一挪到下一轮 Input 再读状态。
        button.PreviewMouseLeftButtonDown += (_, _) => ScheduleUpdate(button, state);
        button.PreviewMouseLeftButtonUp += (_, _) => ScheduleUpdate(button, state);
        button.LostMouseCapture += (_, _) => ScheduleUpdate(button, state);
        button.IsEnabledChanged += (_, _) => Update(button, state, initial: true);
    }

    private static void WireToggle(ToggleButton toggle, State state)
    {
        toggle.MouseEnter += (_, _) => Update(toggle, state, initial: false);
        toggle.MouseLeave += (_, _) => Update(toggle, state, initial: false);
        toggle.Checked += (_, _) => Update(toggle, state, initial: false);
        toggle.Unchecked += (_, _) => Update(toggle, state, initial: false);
        toggle.IsEnabledChanged += (_, _) => Update(toggle, state, initial: true);
    }

    private static void WireListBoxItem(ListBoxItem item, State state)
    {
        item.MouseEnter += (_, _) => Update(item, state, initial: false);
        item.MouseLeave += (_, _) => Update(item, state, initial: false);
        item.Selected += (_, _) => Update(item, state, initial: false);
        item.Unselected += (_, _) => Update(item, state, initial: false);
        item.IsEnabledChanged += (_, _) => Update(item, state, initial: true);
    }

    private static void ScheduleUpdate(FrameworkElement element, State state)
    {
        element.Dispatcher.BeginInvoke(
            new Action(() => Update(element, state, initial: false)),
            DispatcherPriority.Input);
    }

    private static void ResolveParts(FrameworkElement element, State state)
    {
        if (element is not Control control || control.Template is null)
        {
            return;
        }

        state.HoverLayer = control.Template.FindName("PART_HoverLayer", element) as UIElement;
        state.PressedLayer = control.Template.FindName("PART_PressedLayer", element) as UIElement;
        state.SelectedLayer = control.Template.FindName("PART_SelectedLayer", element) as UIElement;
        state.Indicator = control.Template.FindName("PART_Indicator", element) as UIElement;

        if (element is not ToggleButton)
        {
            return;
        }

        state.Track = control.Template.FindName("PART_Track", element) as FrameworkElement;
        state.TrackOn = control.Template.FindName("PART_TrackOn", element) as FrameworkElement;
        state.Thumb = control.Template.FindName("PART_Thumb", element) as FrameworkElement;

        if (state.Thumb is null)
        {
            state.ThumbTransform = null;
            return;
        }

        if (state.Thumb.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            state.Thumb.RenderTransform = transform;
        }

        state.ThumbTransform = transform;
    }

    private static void Update(FrameworkElement element, State state, bool initial)
    {
        switch (element)
        {
            case ToggleButton toggle:
                UpdateToggle(toggle, state, initial);
                break;
            case ButtonBase button:
                UpdateButton(button, state, initial);
                break;
            case ListBoxItem item:
                UpdateListBoxItem(item, state, initial);
                break;
        }
    }

    private static void UpdateButton(ButtonBase button, State state, bool initial)
    {
        var enabled = button.IsEnabled;
        Animation.SetOpacity(state.HoverLayer, enabled && button.IsMouseOver ? 1d : 0d, TimeSpan.FromMilliseconds(150), initial);
        Animation.SetOpacity(state.PressedLayer, enabled && button.IsPressed ? 1d : 0d, TimeSpan.FromMilliseconds(90), initial);
    }

    private static void UpdateToggle(ToggleButton toggle, State state, bool initial)
    {
        var enabled = toggle.IsEnabled;
        var isChecked = toggle.IsChecked == true;
        Animation.SetOpacity(state.TrackOn, enabled && isChecked ? 1d : 0d, TimeSpan.FromMilliseconds(180), initial);
        Animation.SetOpacity(state.Track, enabled && toggle.IsMouseOver ? 0.82d : 1d, TimeSpan.FromMilliseconds(90), initial);

        if (state.ThumbTransform is not null && state.Thumb is not null)
        {
            var trackWidth = state.TrackOn?.ActualWidth > 0
                ? state.TrackOn.ActualWidth
                : state.Track?.ActualWidth ?? 0;
            var travel = Math.Max(0, trackWidth - state.Thumb.ActualWidth - 6);
            Animation.SetTranslateX(state.ThumbTransform, isChecked ? travel : 0d, TimeSpan.FromMilliseconds(180), initial);
        }
    }

    private static void UpdateListBoxItem(ListBoxItem item, State state, bool initial)
    {
        var enabled = item.IsEnabled;
        var hovered = enabled && item.IsMouseOver;
        var selected = enabled && item.IsSelected;

        Animation.SetOpacity(state.HoverLayer, hovered ? 1d : 0d, TimeSpan.FromMilliseconds(150), initial);
        Animation.SetOpacity(state.SelectedLayer, selected ? 1d : 0d, TimeSpan.FromMilliseconds(180), initial);
        Animation.SetOpacity(state.Indicator, selected ? 1d : 0d, TimeSpan.FromMilliseconds(180), initial);
    }

    private static class Animation
    {
        public static void SetOpacity(UIElement? element, double target, TimeSpan expressiveDuration, bool snap)
        {
            if (element is null)
            {
                return;
            }

            if (snap || !Motion.Enabled)
            {
                element.BeginAnimation(UIElement.OpacityProperty, null);
                element.Opacity = target;
                return;
            }

            element.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(target, Motion.Duration(expressiveDuration))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
        }

        public static void SetTranslateX(TranslateTransform transform, double target, TimeSpan expressiveDuration, bool snap)
        {
            if (snap || !Motion.Enabled)
            {
                transform.BeginAnimation(TranslateTransform.XProperty, null);
                transform.X = target;
                return;
            }

            transform.BeginAnimation(
                TranslateTransform.XProperty,
                new DoubleAnimation(target, Motion.Duration(expressiveDuration))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
        }
    }
}
