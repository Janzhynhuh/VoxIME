using System.Windows;
using System.Windows.Media;

namespace VoxIME.App.Infrastructure;

/// <summary>
/// 主题画刷辅助：从当前已注入的调色板取画刷，或从十六进制字符串创建（设置页色卡预览用）。
/// </summary>
public static class ThemeBrushes
{
    /// <summary>从 <c>#RRGGBB</c>/<c>#AARRGGBB</c> 创建冻结画刷。</summary>
    public static SolidColorBrush FromHex(string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>按资源键取当前调色板画刷；资源不存在时返回 null。</summary>
    public static Brush? Find(string key) => Application.Current?.TryFindResource(key) as Brush;

    /// <summary>按资源键取画刷，缺失时回退到指定十六进制颜色。</summary>
    public static Brush FindOrFallback(string key, string fallbackHex) => Find(key) ?? FromHex(fallbackHex);

    /// <summary>按资源键取当前调色板颜色；资源不存在时返回 null。</summary>
    public static Color? FindColor(string key) => Find(key) is SolidColorBrush brush ? brush.Color : null;
}
