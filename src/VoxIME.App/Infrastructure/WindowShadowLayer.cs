using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace VoxIME.App.Infrastructure;

/// <summary>
/// 静态窗口阴影层：替换窗口根容器的 <see cref="DropShadowEffect"/>。
/// 首次显示时按窗口尺寸把阴影渲染成位图并缓存（只渲染一次），之后滚动/动画/内容变化
/// 都不会重新计算阴影——避开透明（layered）窗口下 Effect 逐帧重算导致的掉帧和闪烁。
/// </summary>
public sealed class WindowShadowLayer : FrameworkElement
{
    /// <summary>阴影参数：与旧 WindowShadow 资源保持一致。</summary>
    private const double BlurRadius = 32;
    private const double ShadowDepth = 8;
    private const double ShadowOpacity = 0.55;

    /// <summary>窗口内容四周留白（RootBorder 的 Margin）。</summary>
    public static readonly DependencyProperty InsetProperty = DependencyProperty.Register(
        nameof(Inset), typeof(double), typeof(WindowShadowLayer),
        new FrameworkPropertyMetadata(18d, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>RootBorder 的圆角半径。</summary>
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(WindowShadowLayer),
        new FrameworkPropertyMetadata(16d, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Dictionary<(int Width, int Height, double Inset, double Radius), BitmapSource> Cache = new();

    public double Inset
    {
        get => (double)GetValue(InsetProperty);
        set => SetValue(InsetProperty, value);
    }

    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var width = (int)Math.Round(ActualWidth);
        var height = (int)Math.Round(ActualHeight);
        if (width < 8 || height < 8)
        {
            return;
        }

        var inset = Math.Max(0, Inset);
        var radius = Math.Max(0, CornerRadius);
        var key = (width, height, Math.Round(inset, 1), Math.Round(radius, 1));
        if (!Cache.TryGetValue(key, out var shadow))
        {
            shadow = CreateShadowBitmap(width, height, inset, radius);
            Cache[key] = shadow;
        }

        drawingContext.DrawImage(shadow, new Rect(0, 0, ActualWidth, ActualHeight));
    }

    /// <summary>
    /// 渲染「圆角矩形 + DropShadowEffect」，再把源矩形区域的像素挖掉，只留阴影。
    /// 结果是一张阴影位图，后续逐帧绘制零特效开销。
    /// </summary>
    private static BitmapSource CreateShadowBitmap(int width, int height, double inset, double radius)
    {
        var rect = new Rect(
            inset,
            inset,
            Math.Max(1, width - inset * 2),
            Math.Max(1, height - inset * 2));

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRoundedRectangle(Brushes.Black, null, rect, radius, radius);
        }

        var effect = new DropShadowEffect
        {
            BlurRadius = BlurRadius,
            ShadowDepth = ShadowDepth,
            Direction = 315,
            Opacity = ShadowOpacity,
            Color = Colors.Black,
            RenderingBias = RenderingBias.Quality
        };
        effect.Freeze();
        visual.Effect = effect;

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        // 把源圆角矩形内部（RootBorder 会盖住的部分）的像素清掉，只保留外圈阴影。
        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (IsInsideRoundedRect(x + 0.5, y + 0.5, rect, radius))
                {
                    pixels[y * stride + x * 4 + 3] = 0;
                }
            }
        }

        var shadow = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        shadow.Freeze();
        return shadow;
    }

    private static bool IsInsideRoundedRect(double x, double y, Rect rect, double radius)
    {
        if (!rect.Contains(x, y))
        {
            return false;
        }

        if (radius <= 0)
        {
            return true;
        }

        var left = rect.Left + radius;
        var right = rect.Right - radius;
        var top = rect.Top + radius;
        var bottom = rect.Bottom - radius;

        if (x < left && y < top)
        {
            return (x - left) * (x - left) + (y - top) * (y - top) <= radius * radius;
        }

        if (x > right && y < top)
        {
            return (x - right) * (x - right) + (y - top) * (y - top) <= radius * radius;
        }

        if (x < left && y > bottom)
        {
            return (x - left) * (x - left) + (y - bottom) * (y - bottom) <= radius * radius;
        }

        if (x > right && y > bottom)
        {
            return (x - right) * (x - right) + (y - bottom) * (y - bottom) <= radius * radius;
        }

        return true;
    }
}
