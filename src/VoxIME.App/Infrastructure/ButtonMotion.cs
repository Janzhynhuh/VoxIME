using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace VoxIME.App.Infrastructure;

/// <summary>
/// 全局按钮微动效：hover 轻微放大、按压缩小、松开回弹，跟随设置里的动效档位。
/// 通过全局按钮样式设置附加属性，所有基于 BaseButton / IconButton 的按钮自动生效。
/// </summary>
public static class ButtonMotion
{
    private sealed class ButtonState
    {
        public bool Hovered;
        public bool Pressed;
    }

    private static readonly ConditionalWeakTable<Button, ButtonState> States = new();

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled",
        typeof(bool),
        typeof(ButtonMotion),
        new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Button button || e.NewValue is not true || States.TryGetValue(button, out _))
        {
            return;
        }

        var state = new ButtonState();
        States.Add(button, state);

        button.MouseEnter += (_, _) =>
        {
            state.Hovered = true;
            if (!state.Pressed)
            {
                Motion.PlayButtonHover(button, hovered: true);
            }
        };

        button.MouseLeave += (_, _) =>
        {
            state.Hovered = false;
            if (!state.Pressed)
            {
                Motion.PlayButtonHover(button, hovered: false);
            }
        };

        button.PreviewMouseLeftButtonDown += (_, _) =>
        {
            if (!button.IsEnabled)
            {
                return;
            }

            state.Pressed = true;
            Motion.PlayButtonPress(button);
        };

        button.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (!state.Pressed)
            {
                return;
            }

            state.Pressed = false;
            Motion.PlayButtonRelease(button, state.Hovered);
        };

        button.LostMouseCapture += (_, _) =>
        {
            if (!state.Pressed)
            {
                return;
            }

            state.Pressed = false;
            Motion.PlayButtonRelease(button, state.Hovered);
        };

        button.IsEnabledChanged += (_, _) =>
        {
            if (!button.IsEnabled)
            {
                state.Pressed = false;
                state.Hovered = false;
                Motion.ResetButtonScale(button);
            }
        };

        button.Unloaded += (_, _) =>
        {
            state.Pressed = false;
            state.Hovered = false;
            Motion.ResetButtonScale(button);
        };
    }
}