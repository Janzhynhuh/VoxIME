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
    /// 计算颜色亮度（0..1，sRGB 加权：0.2126R + 0.7152G + 0.0722B），
    /// 用于「按背景亮度匹配明暗主题」。
    /// </summary>
    public static double Luminance(string hex)
    {
        var color = Parse(hex);
        return (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255d;
    }
}