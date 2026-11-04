using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VoxIME.Core.Appearance;
using VoxIME.Core.Configuration;

namespace VoxIME.App.Infrastructure;

/// <summary>动效档位（由配置里的 <see cref="MotionModes"/> 解析而来）。</summary>
public enum MotionStyle
{
    /// <summary>明显：默认档，带位移与轻微缩放。</summary>
    Expressive,

    /// <summary>克制：时长更短、幅度减半。</summary>
    Subtle,

    /// <summary>关闭：瞬时完成。</summary>
    Off
}

/// <summary>
/// 界面动效辅助：统一时长/位移/缓动，并提供窗口入场、退场、页面滑入、卡片抬起、按钮按压等
/// 常用动画。只动画 <c>Opacity</c> 与 <c>RenderTransform</c>（不触发布局重算）。
/// </summary>
public static class Motion
{
    private static readonly IEasingFunction EaseOutEasing = FreezeEasing(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction EaseInEasing = FreezeEasing(new CubicEase { EasingMode = EasingMode.EaseIn });
    private static readonly IEasingFunction OvershootEasing = FreezeEasing(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 });

    /// <summary>当前动效档位。</summary>
    public static MotionStyle Style { get; private set; } = MotionStyle.Expressive;

    /// <summary>是否有动效（关闭时全部瞬时完成）。</summary>
    public static bool Enabled => Style != MotionStyle.Off;

    /// <summary>启动/保存后调用。</summary>
    public static void Initialize(AppConfig? config) => ApplyConfig(config);

    /// <summary>按配置更新档位。</summary>
    public static void ApplyConfig(AppConfig? config) => Apply(config?.Ui?.Motion);

    /// <summary>直接按档位字符串更新（设置页实时预览用）。</summary>
    public static void Apply(string? motionMode)
    {
        Style = MotionModes.Normalize(motionMode) switch
        {
            MotionModes.Subtle => MotionStyle.Subtle,
            MotionModes.Off => MotionStyle.Off,
            _ => MotionStyle.Expressive
        };
    }

    /// <summary>按当前档位缩放时长（关闭→零）。</summary>
    public static TimeSpan Duration(TimeSpan expressive) => Style switch
    {
        MotionStyle.Off => TimeSpan.Zero,
        MotionStyle.Subtle => TimeSpan.FromMilliseconds(expressive.TotalMilliseconds * 0.62),
        _ => expressive
    };

    /// <summary>按当前档位缩放位移/缩放幅度（克制档减半）。</summary>
    public static double Distance(double expressive) => Style switch
    {
        MotionStyle.Off => 0,
        MotionStyle.Subtle => expressive * 0.5,
        _ => expressive
    };

    /// <summary>入场：淡入 + 上浮 + 轻微放大（窗口/对话框用）。</summary>
    public static void PlayEnter(
        FrameworkElement element,
        double offsetY = 16,
        double fromScale = 0.96,
        TimeSpan? duration = null,
        Action? completed = null)
    {
        if (!Enabled)
        {
            Reset(element);
            completed?.Invoke();
            return;
        }

        var span = Duration(duration ?? TimeSpan.FromMilliseconds(260));
        var (translate, scale) = EnsureTransforms(element);
        element.Opacity = 1;

        var fade = new DoubleAnimation(0, 1, span) { EasingFunction = EaseOutEasing };
        if (completed is not null)
        {
            fade.Completed += (_, _) => completed();
        }

        element.BeginAnimation(UIElement.OpacityProperty, fade);
        translate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(Distance(offsetY), 0, span) { EasingFunction = EaseOutEasing });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(fromScale, 1, span) { EasingFunction = EaseOutEasing });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(fromScale, 1, span) { EasingFunction = EaseOutEasing });
    }

    /// <summary>退场：淡出 + 下移 + 轻微缩小；完成后回调（调用方在那里 Hide/Close）。</summary>
    public static void PlayExit(
        FrameworkElement element,
        double offsetY = 12,
        double toScale = 0.98,
        TimeSpan? duration = null,
        Action? completed = null)
    {
        if (!Enabled)
        {
            element.Opacity = 0;
            completed?.Invoke();
            return;
        }

        var span = Duration(duration ?? TimeSpan.FromMilliseconds(180));
        var (translate, scale) = EnsureTransforms(element);

        var fade = new DoubleAnimation(element.Opacity, 0, span) { EasingFunction = EaseInEasing };
        if (completed is not null)
        {
            fade.Completed += (_, _) => completed();
        }

        element.BeginAnimation(UIElement.OpacityProperty, fade);
        translate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, Distance(offsetY), span) { EasingFunction = EaseInEasing });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, toScale, span) { EasingFunction = EaseInEasing });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, toScale, span) { EasingFunction = EaseInEasing });
    }

    /// <summary>页面切换：水平滑入 + 淡入。</summary>
    public static void PlaySlideIn(
        FrameworkElement element,
        double fromX,
        TimeSpan? duration = null,
        Action? completed = null)
    {
        if (!Enabled)
        {
            Reset(element);
            completed?.Invoke();
            return;
        }

        var span = Duration(duration ?? TimeSpan.FromMilliseconds(200));
        var (translate, _) = EnsureTransforms(element);
        element.Opacity = 1;

        var fade = new DoubleAnimation(0, 1, span) { EasingFunction = EaseOutEasing };
        if (completed is not null)
        {
            fade.Completed += (_, _) => completed();
        }

        element.BeginAnimation(UIElement.OpacityProperty, fade);
        translate.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(Distance(fromX), 0, span) { EasingFunction = EaseOutEasing });
    }

    /// <summary>对话框弹入：淡入 + 从更小尺寸回弹（带轻微过冲）。</summary>
    public static void PlayDialogEnter(FrameworkElement element, double fromScale = 0.92, TimeSpan? duration = null, Action? completed = null)
    {
        if (!Enabled)
        {
            Reset(element);
            completed?.Invoke();
            return;
        }

        var span = Duration(duration ?? TimeSpan.FromMilliseconds(240));
        var (_, scale) = EnsureTransforms(element);
        element.Opacity = 1;

        var fade = new DoubleAnimation(0, 1, span) { EasingFunction = EaseOutEasing };
        if (completed is not null)
        {
            fade.Completed += (_, _) => completed();
        }

        element.BeginAnimation(UIElement.OpacityProperty, fade);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(fromScale, 1, span) { EasingFunction = OvershootEasing });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(fromScale, 1, span) { EasingFunction = OvershootEasing });
    }

    /// <summary>卡片/按钮 hover 抬起：上浮再回落（一次性演示或 hover 反馈用）。</summary>
    public static void PlayLift(FrameworkElement element, double distance = 4, TimeSpan? duration = null, Action? completed = null)
    {
        if (!Enabled)
        {
            completed?.Invoke();
            return;
        }

        var span = Duration(duration ?? TimeSpan.FromMilliseconds(160));
        var (translate, scale) = EnsureTransforms(element);

        var lift = new DoubleAnimation(0, -Distance(distance), span) { EasingFunction = EaseOutEasing };
        lift.Completed += (_, _) =>
        {
            var back = new DoubleAnimation(-Distance(distance), 0, span) { EasingFunction = EaseOutEasing };
            back.Completed += (_, _) => completed?.Invoke();
            translate.BeginAnimation(TranslateTransform.YProperty, back);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.02, 1, span));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.02, 1, span));
        };
        translate.BeginAnimation(TranslateTransform.YProperty, lift);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 1.02, span));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 1.02, span));
    }

    /// <summary>按钮按压缩放：1 → 0.94 → 1。</summary>
    public static void PlayPress(FrameworkElement element, TimeSpan? duration = null, Action? completed = null)
    {
        if (!Enabled)
        {
            completed?.Invoke();
            return;
        }

        var span = Duration(duration ?? TimeSpan.FromMilliseconds(90));
        var (_, scale) = EnsureTransforms(element);

        var down = new DoubleAnimation(1, 0.94, span) { EasingFunction = EaseOutEasing };
        down.Completed += (_, _) =>
        {
            var back = new DoubleAnimation(0.94, 1, span) { EasingFunction = EaseOutEasing };
            back.Completed += (_, _) => completed?.Invoke();
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, back);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, back);
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, down);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, down);
    }

    /// <summary>重置元素到静止状态并清除动画。</summary>
    public static void Reset(FrameworkElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1;

        if (element.RenderTransform is TransformGroup group)
        {
            group.Children.Clear();
            group.Children.Add(new TranslateTransform());
            group.Children.Add(new ScaleTransform(1, 1));
        }
    }

    private static (TranslateTransform Translate, ScaleTransform Scale) EnsureTransforms(FrameworkElement element)
    {
        if (element.RenderTransform is TransformGroup group
            && group.Children.Count >= 2
            && group.Children[0] is TranslateTransform translate
            && group.Children[1] is ScaleTransform scale)
        {
            return (translate, scale);
        }

        translate = new TranslateTransform();
        scale = new ScaleTransform(1, 1);
        var created = new TransformGroup();
        created.Children.Add(translate);
        created.Children.Add(scale);
        element.RenderTransform = created;
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        return (translate, scale);
    }

    private static IEasingFunction FreezeEasing(IEasingFunction easing)
    {
        ((Freezable)easing).Freeze();
        return easing;
    }
}
