using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using VoxIME.Core.Abstractions;

namespace VoxIME.Live2D;

/// <summary>
/// 内置 WPF 矢量兜底桌宠形象：紫罗兰色萌系小球生物。
/// 在“未配置模型 / WebView2 不可用 / 模型加载失败”时替代 Live2D 模型显示，
/// 保证桌宠窗口永远不是一个空白/白块窗口。
///
/// 动画：呼吸浮动、自动眨眼、说话口型（<see cref="MouthOpen"/>）、聆听波纹、
/// 思考转圈点、出错抖动变色、待机闪星；视线跟随 <see cref="GazeX"/>/<see cref="GazeY"/>。
/// 所有绘制资源均由代码生成，不依赖图片资源，DPI 无关。
/// </summary>
public sealed class FallbackAvatar : FrameworkElement
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private static readonly Brush EyeWhite = CreateBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
    private static readonly Brush PupilBrush = CreateBrush(Color.FromArgb(0xFF, 0x3B, 0x2A, 0x7A));
    private static readonly Brush PupilShine = CreateBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
    private static readonly Brush CheekBrush = CreateBrush(Color.FromArgb(0x4D, 0xFF, 0x82, 0xAA));
    private static readonly Brush MouthInner = CreateBrush(Color.FromArgb(0xFF, 0x4A, 0x12, 0x30));
    private static readonly Brush TongueBrush = CreateBrush(Color.FromArgb(0xFF, 0xFF, 0x8F, 0xA8));
    private static readonly Brush HighlightBrush = CreateBrush(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF));
    private static readonly Pen BodyOutlinePen = CreatePen(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF), 2);
    private static readonly Pen MouthSmilePen = CreatePen(Color.FromArgb(0xD9, 0x3C, 0x1E, 0x5A), 2.2);
    private static readonly Pen ErrorEyePen = CreatePen(Color.FromArgb(0xEB, 0xFF, 0xEB, 0xEB), 3.2);
    private static readonly Brush ErrorTintBrush = CreateBrush(Color.FromArgb(0x33, 0xFF, 0x50, 0x5A));
    private static readonly Brush SparkleBrush = CreateBrush(Color.FromArgb(0x8C, 0xFF, 0xFA, 0xDC));
    private static readonly Brush ThoughtDotBrush = CreateBrush(Color.FromArgb(0xD9, 0xE8, 0xE8, 0xF0));

    private bool _running;
    private double _time;

    public FallbackAvatar()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => Stop();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                Start();
            }
            else
            {
                Stop();
            }
        };
    }

    /// <summary>当前状态（影响表情/特效/颜色）。</summary>
    public Live2DState State { get; set; } = Live2DState.Idle;

    /// <summary>张嘴幅度 0..1（麦克风电平 / TTS 音量）。</summary>
    public double MouthOpen { get; set; }

    /// <summary>视线目标 -1..1（眼动跟随）。</summary>
    public double GazeX { get; set; }

    public double GazeY { get; set; }

    /// <summary>主题名（当前仅 violet，为后续扩展保留）。</summary>
    public string Theme { get; set; } = "violet";

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        CompositionTarget.Rendering += OnCompositionTargetRendering;
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        CompositionTarget.Rendering -= OnCompositionTargetRendering;
    }

    /// <summary>重置动画时钟（截图/测试用）。</summary>
    public void ResetAnimation()
    {
        _time = 0;
        InvalidateVisual();
    }

    /// <summary>直接设定动画时间（秒），用于截图/自动化验证时获得确定性画面。</summary>
    public void SetAnimationTime(double seconds)
    {
        _time = Math.Max(0, seconds);
        InvalidateVisual();
    }

    private void OnCompositionTargetRendering(object? sender, EventArgs e)
    {
        _time = Clock.Elapsed.TotalSeconds;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 1 || h <= 1)
        {
            return;
        }

        // 背景完全透明：不填充任何底色。
        var t = _time;
        var state = State;
        var r = Math.Clamp(Math.Min(w, h) * 0.215, 34, 140);
        var cx = w / 2;
        var cy = h * 0.52;
        var shake = state == Live2DState.Error ? Math.Sin(t * 42) * r * 0.035 : 0;
        var bob = Math.Sin(t * 1.7) * r * 0.05;

        dc.PushTransform(new TranslateTransform(cx + shake, cy + bob));

        DrawGroundShadow(dc, r);
        if (state == Live2DState.Listening)
        {
            DrawListeningRipples(dc, r, t);
        }

        // 呼吸缩放（身体部分整体缩放）
        var breath = 1 + Math.Sin(t * 1.9) * 0.018;
        dc.PushTransform(new ScaleTransform(breath, breath));

        DrawEars(dc, r, t);
        DrawBody(dc, r);
        DrawHighlight(dc, r);
        DrawCheeks(dc, r);
        DrawEyes(dc, r, t);
        DrawMouth(dc, r);

        if (state == Live2DState.Thinking)
        {
            DrawThinkingDots(dc, r, t);
        }

        if (state == Live2DState.Speaking && MouthOpen > 0.1)
        {
            DrawSpeakingWaves(dc, r);
        }

        if (state == Live2DState.Error)
        {
            DrawErrorTint(dc, r);
        }

        dc.Pop(); // breath

        DrawSparkles(dc, r, t);
        dc.Pop(); // translate
    }

    private static void DrawGroundShadow(DrawingContext dc, double r)
    {
        dc.PushTransform(new TranslateTransform(0, r * 1.16));
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x4D, 0x18, 0x0C, 0x3C), 0),
                new GradientStop(Color.FromArgb(0x00, 0x18, 0x0C, 0x3C), 1)
            }
        };
        dc.DrawEllipse(brush, null, new Point(0, 0), r * 0.95, r * 0.24);
        dc.Pop();
    }

    private static void DrawListeningRipples(DrawingContext dc, double r, double t)
    {
        for (var i = 0; i < 3; i++)
        {
            var phase = (t * 0.7 + i / 3.0) % 1.0;
            var radius = r * (0.95 + phase * 0.75);
            var alpha = (byte)Math.Clamp(0x6E * (1 - phase), 0, 255);
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(alpha, 0xB3, 0x9D, 0xFF)), 2.2);
            pen.Freeze();
            dc.DrawEllipse(null, pen, new Point(0, -r * 0.05), radius, radius);
        }
    }

    private static void DrawEars(DrawingContext dc, double r, double t)
    {
        var swing = Math.Sin(t * 1.9 + 0.7) * 0.08;
        DrawEar(dc, r, -1, swing);
        DrawEar(dc, r, 1, swing);
    }

    private static void DrawEar(DrawingContext dc, double r, int side, double swing)
    {
        dc.PushTransform(new TranslateTransform(side * r * 0.52, -r * 0.86));
        dc.PushTransform(new RotateTransform(side * (0.24 + swing) * 180 / Math.PI));

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(0, 0), isFilled: true, isClosed: true);
            ctx.QuadraticBezierTo(
                new Point(side * r * 0.20, -r * 0.55),
                new Point(side * r * 0.02, -r * 0.78),
                true,
                false);
            ctx.QuadraticBezierTo(
                new Point(-side * r * 0.22, -r * 0.32),
                new Point(0, 0),
                true,
                false);
        }

        geometry.Freeze();

        var brush = new LinearGradientBrush(
            Color.FromRgb(0x7C, 0x5C, 0xFF),
            Color.FromRgb(0xC4, 0xB2, 0xFF),
            new Point(0, 1),
            new Point(0, 0));
        dc.DrawGeometry(brush, null, geometry);
        dc.Pop();
        dc.Pop();
    }

    private static void DrawBody(DrawingContext dc, double r)
    {
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.30, 0.26),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.72,
            RadiusY = 0.72,
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0xD3, 0xC4, 0xFF), 0),
                new GradientStop(Color.FromRgb(0x9B, 0x7C, 0xFF), 0.42),
                new GradientStop(Color.FromRgb(0x7C, 0x5C, 0xFF), 0.78),
                new GradientStop(Color.FromRgb(0x4F, 0x37, 0xB8), 1)
            }
        };
        brush.Freeze();
        dc.DrawEllipse(brush, BodyOutlinePen, new Point(0, 0), r, r);
    }

    private static void DrawHighlight(DrawingContext dc, double r)
    {
        dc.PushTransform(new RotateTransform(-32));
        dc.DrawEllipse(HighlightBrush, null, new Point(-r * 0.36, -r * 0.40), r * 0.31, r * 0.20);
        dc.Pop();
    }

    private static void DrawCheeks(DrawingContext dc, double r)
    {
        dc.DrawEllipse(CheekBrush, null, new Point(-r * 0.58, r * 0.18), r * 0.15, r * 0.10);
        dc.DrawEllipse(CheekBrush, null, new Point(r * 0.58, r * 0.18), r * 0.15, r * 0.10);
    }

    private void DrawEyes(DrawingContext dc, double r, double t)
    {
        var open = BlinkFactor(t);
        var eyeW = r * 0.20;
        var eyeH = Math.Max(0.8, r * 0.25 * open);
        var eyeY = -r * 0.10;

        if (State == Live2DState.Error)
        {
            DrawErrorEyes(dc, r, eyeW, eyeH, eyeY);
            return;
        }

        foreach (var side in new[] { -1, 1 })
        {
            var ex = side * r * 0.36;
            dc.DrawEllipse(EyeWhite, null, new Point(ex, eyeY), eyeW, eyeH);

            var px = ex + GazeX * eyeW * 0.42;
            var py = eyeY + GazeY * eyeH * 0.32;
            var pupil = Math.Min(eyeW, eyeH) * 0.55;
            if (pupil < 0.6)
            {
                pupil = 0.6;
            }

            dc.DrawEllipse(PupilBrush, null, new Point(px, py), pupil, pupil);
            dc.DrawEllipse(PupilShine, null, new Point(px - pupil * 0.32, py - pupil * 0.35), pupil * 0.30, pupil * 0.30);
        }
    }

    private void DrawErrorEyes(DrawingContext dc, double r, double eyeW, double eyeH, double eyeY)
    {
        foreach (var side in new[] { -1, 1 })
        {
            var ex = side * r * 0.36;
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(ex - eyeW * 0.5, eyeY - eyeH * 0.5), false, false);
                ctx.LineTo(new Point(ex + eyeW * 0.5, eyeY + eyeH * 0.5), true, false);
                ctx.BeginFigure(new Point(ex + eyeW * 0.5, eyeY - eyeH * 0.5), false, false);
                ctx.LineTo(new Point(ex - eyeW * 0.5, eyeY + eyeH * 0.5), true, false);
            }

            geometry.Freeze();
            dc.DrawGeometry(null, ErrorEyePen, geometry);
        }
    }

    private void DrawMouth(DrawingContext dc, double r)
    {
        var my = r * 0.30;
        var mw = r * 0.22;
        var open = Math.Clamp(MouthOpen, 0, 1);
        if (open > 0.06)
        {
            var mh = r * (0.05 + open * 0.30);
            dc.DrawEllipse(MouthInner, null, new Point(0, my + mh * 0.2), mw * (0.75 + open * 0.25), mh);
            dc.DrawEllipse(TongueBrush, null, new Point(0, my + mh * 0.72), mw * 0.5, mh * 0.42);
        }
        else
        {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(-mw * 0.55, my - r * 0.02), false, false);
                ctx.QuadraticBezierTo(
                    new Point(0, my + r * 0.06),
                    new Point(mw * 0.55, my - r * 0.02),
                    true,
                    false);
            }

            geometry.Freeze();
            dc.DrawGeometry(null, MouthSmilePen, geometry);
        }
    }

    private static void DrawThinkingDots(DrawingContext dc, double r, double t)
    {
        for (var i = 0; i < 3; i++)
        {
            var angle = t * 2.4 + i * Math.PI * 2 / 3;
            var x = Math.Cos(angle) * r * 1.25;
            var y = Math.Sin(angle) * r * 0.5 - r * 0.35;
            var alpha = (byte)Math.Clamp(0x8C + 0x50 * (0.5 + 0.5 * Math.Sin(angle)), 0, 255);
            var brush = new SolidColorBrush(Color.FromArgb(alpha, 0xE8, 0xE8, 0xF0));
            brush.Freeze();
            dc.DrawEllipse(brush, null, new Point(x, y), r * 0.075, r * 0.075);
        }
    }

    private void DrawSpeakingWaves(DrawingContext dc, double r)
    {
        for (var s = 0; s < 2; s++)
        {
            var side = s == 0 ? -1 : 1;
            var waveRadius = r * (0.18 + Math.Clamp(MouthOpen, 0, 1) * 0.16);
            var centerX = side * r * 0.92;
            var centerY = r * 0.08;
            var alpha = (byte)Math.Clamp(0x4D + MouthOpen * 0x66, 0, 255);
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(alpha, 0xB3, 0x9D, 0xFF)), 2.4);
            pen.Freeze();

            var start = new Point(centerX + side * waveRadius * Math.Cos(-0.42 * Math.PI), centerY + waveRadius * Math.Sin(-0.42 * Math.PI));
            var end = new Point(centerX + side * waveRadius * Math.Cos(0.42 * Math.PI), centerY + waveRadius * Math.Sin(0.42 * Math.PI));
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(start, false, false);
                ctx.ArcTo(end, new Size(waveRadius, waveRadius), 0, false,
                    side > 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true, false);
            }

            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }
    }

    private static void DrawErrorTint(DrawingContext dc, double r)
    {
        dc.PushClip(new EllipseGeometry(new Point(0, 0), r, r));
        dc.DrawEllipse(ErrorTintBrush, null, new Point(0, 0), r, r);
        dc.Pop();
    }

    private static void DrawSparkles(DrawingContext dc, double r, double t)
    {
        for (var i = 0; i < 3; i++)
        {
            var phase = i * 2.1 + 0.8;
            var px = Math.Sin(t * (0.6 + i * 0.17) + phase) * r * (0.55 + i * 0.18);
            var py = -r * (0.95 + 0.18 * Math.Cos(t * 0.5 + phase));
            var size = r * (0.045 + 0.022 * Math.Sin(t * 3 + phase));
            var alpha = (byte)Math.Clamp(0x70 + 0x40 * Math.Sin(t * 2.3 + phase), 0, 255);
            var brush = new SolidColorBrush(Color.FromArgb(alpha, 0xFF, 0xFA, 0xDC));
            brush.Freeze();

            dc.PushTransform(new TranslateTransform(px, py));
            dc.PushTransform(new RotateTransform(t * 34 + phase * 40));
            dc.DrawGeometry(brush, null, CreateStarGeometry(size));
            dc.Pop();
            dc.Pop();
        }
    }

    private static double BlinkFactor(double t)
    {
        // 确定性眨眼：约 4.6 秒一个周期，眨眼持续 0.26 秒。
        var phase = t % 4.6;
        if (phase < 4.28)
        {
            return 1;
        }

        var progress = (phase - 4.28) / 0.32;
        return Math.Max(0.04, Math.Abs(Math.Cos(progress * Math.PI)));
    }

    private static Geometry CreateStarGeometry(double size)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (var i = 0; i < 8; i++)
            {
                var angle = i * Math.PI / 4;
                var radius = i % 2 == 0 ? size : size * 0.34;
                var point = new Point(Math.Cos(angle) * radius, Math.Sin(angle) * radius);
                if (i == 0)
                {
                    ctx.BeginFigure(point, isFilled: true, isClosed: true);
                }
                else
                {
                    ctx.LineTo(point, true, false);
                }
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private static Brush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen CreatePen(Color color, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        pen.Freeze();
        return pen;
    }
}
