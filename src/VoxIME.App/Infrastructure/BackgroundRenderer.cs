using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using VoxIME.Core.Appearance;
using VoxIME.Core.Configuration;

namespace VoxIME.App.Infrastructure;

/// <summary>
/// 生成「设置中心 / 欢迎 / 历史 / 关于」等窗口的自定义背景画刷（PCL2 风格）：
/// 主题色 / 纯色 / 渐变 / 本地图片（预模糊、透明度、暗化、填充方式）。
/// 结果以 DynamicResource 键 <see cref="BackgroundBrushKey"/> / <see cref="DimBrushKey"/> 注入调色板，
/// 窗口 XAML 只引用这两个键；录音悬浮窗（HUD）通过 <see cref="PopulateHud"/> 输出
/// <see cref="HudBackgroundBrushKey"/> / <see cref="HudDimBrushKey"/>，与低频窗口背景互相独立。
/// </summary>
public static class BackgroundRenderer
{
    /// <summary>窗口背景画刷资源键。</summary>
    public const string BackgroundBrushKey = "WindowBackgroundBrush";

    /// <summary>暗化遮罩画刷资源键（保证文字对比度）。</summary>
    public const string DimBrushKey = "WindowDimBrush";

    /// <summary>录音悬浮窗（HUD）背景画刷资源键。</summary>
    public const string HudBackgroundBrushKey = "HudBackgroundBrush";

    /// <summary>录音悬浮窗（HUD）暗化遮罩资源键。</summary>
    public const string HudDimBrushKey = "HudDimBrush";

    /// <summary>图片预模糊的渲染宽度（与设置窗口内宽接近，其它窗口按比例拉伸）。</summary>
    private const int TargetWidth = 980;

    /// <summary>HUD 尺寸较小，按更小的宽度解码即可，避免大图浪费内存。</summary>
    private const int HudTargetWidth = 640;

    private static string? _cachedKey;
    private static ImageBrush? _cachedBrush;
    private static string? _hudCachedKey;
    private static ImageBrush? _hudCachedBrush;

    /// <summary>把当前背景配置展开成两个画刷，写入调色板字典。</summary>
    public static void Populate(ResourceDictionary dictionary, BackgroundConfig? background, ResolvedTheme theme)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        ArgumentNullException.ThrowIfNull(theme);

        var config = Clone(background);
        config.Normalize();
        dictionary[BackgroundBrushKey] = CreateBackgroundBrush(config, theme);
        dictionary[DimBrushKey] = CreateDimBrush(config, theme);
    }

    /// <summary>
    /// 把录音悬浮窗（HUD）的背景配置展开成两个画刷。
    /// 主题模式沿用主题生成的 HudCardBrush；自定义模式才覆盖为纯色/渐变/图片，并给出暗化遮罩。
    /// </summary>
    public static void PopulateHud(ResourceDictionary dictionary, BackgroundConfig? background, ResolvedTheme theme)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        ArgumentNullException.ThrowIfNull(theme);

        var config = Clone(background);
        config.Normalize();

        if (!config.IsCustom)
        {
            var themeBrush = dictionary.Contains("HudCardBrush") ? dictionary["HudCardBrush"] : null;
            dictionary[HudBackgroundBrushKey] = themeBrush is Brush brush
                ? brush
                : CreateSolid(theme.Colors.Surface);
            dictionary[HudDimBrushKey] = CreateTransparent();
            return;
        }

        dictionary[HudBackgroundBrushKey] = CreateBackgroundBrush(config, theme, forHud: true);
        dictionary[HudDimBrushKey] = CreateDimBrush(config, theme);
    }

    /// <summary>图片模式但文件缺失/损坏时返回 null（调用方回退主题背景）。</summary>
    public static Brush? TryCreateImageBrush(BackgroundConfig background, ResolvedTheme theme)
    {
        var config = Clone(background);
        config.Normalize();
        return TryCreateImageBrushCore(config, theme);
    }

    private static BackgroundConfig Clone(BackgroundConfig? source) =>
        source is null ? new BackgroundConfig() : ConfigJson.Clone(source);

    private static Brush CreateBackgroundBrush(BackgroundConfig config, ResolvedTheme theme, bool forHud = false)
    {
        switch (config.Mode)
        {
            case BackgroundConfig.ModeImage:
                return TryCreateImageBrushCore(config, theme, forHud) is { } imageBrush
                    ? imageBrush
                    : CreateSolid(theme.Colors.Background);

            case BackgroundConfig.ModeSolid:
                return CreateSolid(config.SolidColor ?? theme.Colors.Surface);

            case BackgroundConfig.ModeGradient:
            {
                var start = ParseColor(config.GradientStart) ?? ParseColor(theme.Colors.Accent) ?? Colors.Gray;
                var end = ParseColor(config.GradientEnd) ?? ParseColor(theme.Colors.Background) ?? Colors.Black;
                var gradient = new LinearGradientBrush(start, end, new Point(0, 0), new Point(1, 1));
                gradient.Freeze();
                return gradient;
            }

            default:
                return CreateSolid(theme.Colors.Background);
        }
    }

    private static SolidColorBrush CreateSolid(string hex)
    {
        var color = ParseColor(hex) ?? Colors.Black;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush CreateTransparent()
    {
        var brush = new SolidColorBrush(Colors.Transparent);
        brush.Freeze();
        return brush;
    }

    private static ImageBrush? TryCreateImageBrushCore(BackgroundConfig config, ResolvedTheme theme, bool forHud = false)
    {
        var path = config.ImagePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var stamp = File.GetLastWriteTimeUtc(path).Ticks;
            var key = string.Join('|',
                path, stamp, config.BlurRadius.ToString("0.#"), config.ImageOpacity.ToString("0.##"),
                config.Fit, theme.IsLight ? "light" : "dark", forHud ? "hud" : "window");
            if (forHud)
            {
                if (string.Equals(key, _hudCachedKey, StringComparison.Ordinal) && _hudCachedBrush is not null)
                {
                    return _hudCachedBrush;
                }
            }
            else if (string.Equals(key, _cachedKey, StringComparison.Ordinal) && _cachedBrush is not null)
            {
                return _cachedBrush;
            }

            var source = LoadBitmap(path, forHud ? HudTargetWidth : TargetWidth);
            var blurred = BlurBitmap(source, config.BlurRadius, ParseColor(theme.Colors.Background) ?? Colors.Black);
            var brush = new ImageBrush(blurred)
            {
                Stretch = config.Fit switch
                {
                    "contain" => Stretch.Uniform,
                    "fill" => Stretch.Fill,
                    _ => Stretch.UniformToFill
                },
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center,
                Opacity = config.ImageOpacity
            };
            brush.Freeze();

            if (forHud)
            {
                _hudCachedKey = key;
                _hudCachedBrush = brush;
            }
            else
            {
                _cachedKey = key;
                _cachedBrush = brush;
            }

            return brush;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BackgroundRenderer] 加载背景图片失败：{ex.Message}");
            return null;
        }
    }

    private static BitmapSource LoadBitmap(string path, int decodeWidth)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.DecodePixelWidth = decodeWidth;
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>
    /// 用 RenderTargetBitmap 把 BlurEffect 烘焙进图片（只做一次并缓存），
    /// 避免每个窗口挂实时模糊特效。
    /// </summary>
    private static BitmapSource BlurBitmap(BitmapSource source, double radius, Color backdrop)
    {
        var width = Math.Max(1, source.PixelWidth);
        var height = Math.Max(1, source.PixelHeight);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var background = new SolidColorBrush(backdrop);
            background.Freeze();
            dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
            dc.DrawImage(source, new Rect(0, 0, width, height));
        }

        if (radius > 0.5)
        {
            var effect = new BlurEffect
            {
                Radius = radius,
                KernelType = KernelType.Gaussian,
                RenderingBias = RenderingBias.Quality
            };
            effect.Freeze();
            visual.Effect = effect;
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static Brush CreateDimBrush(BackgroundConfig config, ResolvedTheme theme)
    {
        if (!config.IsCustom)
        {
            var transparent = new SolidColorBrush(Colors.Transparent);
            transparent.Freeze();
            return transparent;
        }

        var baseColor = ParseColor(theme.Colors.Background) ?? Colors.Black;
        var alpha = (byte)Math.Round(Math.Clamp(config.Dim, 0, 1) * 255);
        var brush = new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
        brush.Freeze();
        return brush;
    }

    private static Color? ParseColor(string? hex) =>
        ThemeColorMath.TryParse(hex, out var color) ? Color.FromArgb(color.A, color.R, color.G, color.B) : null;
}
