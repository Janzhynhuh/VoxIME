using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VoxIME.Core.Appearance;
using VoxIME.Core.Configuration;

namespace VoxIME.App.Infrastructure;

/// <summary>
/// 估算自定义背景的亮度（0=黑、1=白），供「按背景亮度匹配明暗主题」使用：
/// 浅色背景建议配浅色主题（深色文字 + 浅色玻璃卡片），深色背景配深色主题。
/// </summary>
public static class BackgroundTone
{
    /// <summary>返回背景亮度；无法判断（图片缺失/颜色为空/主题背景）时返回 null。</summary>
    public static double? TryGetBrightness(BackgroundConfig? background, ResolvedTheme theme)
    {
        if (background is null || theme is null)
        {
            return null;
        }

        var config = ConfigJson.Clone(background);
        config.Normalize();

        return config.Mode switch
        {
            BackgroundConfig.ModeImage => GetImageBrightness(config.ImagePath),
            BackgroundConfig.ModeSolid => GetColorBrightness(config.SolidColor ?? theme.Colors.Surface),
            BackgroundConfig.ModeGradient => GetGradientBrightness(config, theme),
            _ => null
        };
    }

    private static double? GetGradientBrightness(BackgroundConfig config, ResolvedTheme theme)
    {
        var start = config.GradientStart ?? theme.Colors.Accent;
        var end = config.GradientEnd ?? theme.Colors.Background;
        try
        {
            return (ThemeColorMath.Luminance(start) + ThemeColorMath.Luminance(end)) / 2d;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static double? GetColorBrightness(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return null;
        }

        try
        {
            return ThemeColorMath.Luminance(color);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static double? GetImageBrightness(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.DecodePixelWidth = 32;
            image.EndInit();
            image.Freeze();

            var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            var width = Math.Max(1, converted.PixelWidth);
            var height = Math.Max(1, converted.PixelHeight);
            var stride = width * 4;
            var pixels = new byte[stride * height];
            converted.CopyPixels(pixels, stride, 0);

            var total = 0d;
            var count = width * height;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var b = pixels[i];
                var g = pixels[i + 1];
                var r = pixels[i + 2];
                total += (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255d;
            }

            return total / count;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BackgroundTone] 读取背景亮度失败：{ex.Message}");
            return null;
        }
    }
}
