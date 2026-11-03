using System.Globalization;

namespace VoxIME.Core.Appearance;

/// <summary>
/// 十六进制颜色工具（不依赖 WPF/System.Drawing，纯 Core 可单测）：
/// 解析、混合、调亮/调暗、改透明度。支持 <c>#RRGGBB</c> 与 <c>#AARRGGBB</c>。
/// </summary>
public static class ThemeColorMath
{
    /// <summary>是否为合法颜色字符串。</summary>
    public static bool IsValid(string? hex) => TryParse(hex, out _);

    /// <summary>解析颜色；非法时抛 <see cref="FormatException"/>。</summary>
    public static (byte A, byte R, byte G, byte B) Parse(string hex) =>
        TryParse(hex, out var color) ? color : throw new FormatException($"无效的颜色值：{hex}");

    /// <summary>尝试解析颜色。</summary>
    public static bool TryParse(string? hex, out (byte A, byte R, byte G, byte B) color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        var text = hex.Trim();
        if (text.StartsWith('#'))
        {
            text = text[1..];
        }

        if (text.Length == 6)
        {
            text = "FF" + text;
        }

        if (text.Length != 8)
        {
            return false;
        }

        try
        {
            color = (
                byte.Parse(text[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(text[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(text[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(text[6..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            return true;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            return false;
        }
    }

    /// <summary>格式化为 <c>#RRGGBB</c>（不透明）或 <c>#AARRGGBB</c>。</summary>
    public static string ToHex((byte A, byte R, byte G, byte B) color) => color.A == 0xFF
        ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
        : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>按比例把 <paramref name="fromHex"/> 向 <paramref name="toHex"/> 混合（amount=0 原样，1 全为目标色）。</summary>
    public static string Blend(string fromHex, string toHex, double amount)
    {
        var from = Parse(fromHex);
        var to = Parse(toHex);
        var ratio = Math.Clamp(amount, 0d, 1d);

        byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * ratio, MidpointRounding.AwayFromZero);

        return ToHex((from.A, Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B)));
    }

    /// <summary>向白色混合，得到更亮的颜色。</summary>
    public static string Lighten(string hex, double amount) => Blend(hex, "#FFFFFF", amount);

    /// <summary>向黑色混合，得到更暗的颜色。</summary>
    public static string Darken(string hex, double amount) => Blend(hex, "#000000", amount);

    /// <summary>替换透明度（保留 RGB）。</summary>
    public static string WithAlpha(string hex, byte alpha)
    {
        var color = Parse(hex);
        return ToHex((alpha, color.R, color.G, color.B));
    }

    /// <summary>
    /// 按 HSL 明度做反色（保留色相、饱和度与透明度）：黑↔白、浅色字↔深色字。
    /// 用于「浅色背景 + 深色文字」的文字反色：深色主题的整套令牌反色后就是浅色界面。
    /// </summary>
    public static string InvertLightness(string hex)
    {
        var color = Parse(hex);
        var r = color.R / 255d;
        var g = color.G / 255d;
        var b = color.B / 255d;

        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var lightness = (max + min) / 2d;
        double hue = 0;
        var saturation = 0d;

        if (max > min)
        {
            var delta = max - min;
            saturation = lightness > 0.5 ? delta / (2d - max - min) : delta / (max + min);
            if (max == r)
            {
                hue = (g - b) / delta + (g < b ? 6 : 0);
            }
            else if (max == g)
            {
                hue = (b - r) / delta + 2;
            }
            else
            {
                hue = (r - g) / delta + 4;
            }

            hue /= 6d;
        }

        lightness = 1d - lightness;

        double invertedR;
        double invertedG;
        double invertedB;
        if (saturation <= double.Epsilon)
        {
            invertedR = invertedG = invertedB = lightness;
        }
        else
        {
            var q = lightness < 0.5 ? lightness * (1 + saturation) : lightness + saturation - lightness * saturation;
            var p = 2 * lightness - q;
            invertedR = HueToRgb(p, q, hue + 1d / 3d);
            invertedG = HueToRgb(p, q, hue);
            invertedB = HueToRgb(p, q, hue - 1d / 3d);
        }

        return ToHex((
            color.A,
            (byte)Math.Round(Math.Clamp(invertedR, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(invertedG, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(invertedB, 0, 1) * 255)));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0)
        {
            t += 1;
        }

        if (t > 1)
        {
            t -= 1;
        }

        if (t < 1d / 6d)
        {
            return p + (q - p) * 6 * t;
        }

        if (t < 1d / 2d)
        {
            return q;
        }

        if (t < 2d / 3d)
        {
            return p + (q - p) * (2d / 3d - t) * 6;
        }

        return p;
    }
}
