using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace VoxIME.App.Infrastructure;

/// <summary>bool → Visibility；ConverterParameter="invert" 反转。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (IsInvert(parameter))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible ^ IsInvert(parameter);

    internal static bool IsInvert(object? parameter) =>
        parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase);
}

/// <summary>bool 取反。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;
}

/// <summary>null / 空字符串 → Visibility。</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasValue = value is not null && (value is not string s || s.Length > 0);
        if (BoolToVisibilityConverter.IsInvert(parameter))
        {
            hasValue = !hasValue;
        }

        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>枚举值 == ConverterParameter 时可见；parameter 支持 "Invert" 反转。</summary>
public sealed class EnumToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value?.ToString() ?? "";
        var expected = parameter?.ToString() ?? "";
        var invert = false;
        if (expected.EndsWith("!", StringComparison.Ordinal))
        {
            invert = true;
            expected = expected[..^1];
        }

        var match = string.Equals(text, expected, StringComparison.OrdinalIgnoreCase);
        return (match ^ invert) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>枚举值 == ConverterParameter → true（RadioButton 用）。</summary>
public sealed class EnumToBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter is not null && targetType.IsEnum)
        {
            try
            {
                return Enum.Parse(targetType, parameter.ToString()!, true);
            }
            catch (ArgumentException)
            {
                return Binding.DoNothing;
            }
        }

        return Binding.DoNothing;
    }
}

/// <summary>麦克风电平 0..1 → 圆环 StrokeDashArray（dash/gap 以线宽为单位）。</summary>
public sealed class LevelToDashArrayConverter : IValueConverter
{
    /// <summary>圆周长（px）。</summary>
    public double Circumference { get; set; } = 125.66;

    /// <summary>圆环线宽（px）。</summary>
    public double Thickness { get; set; } = 4;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var level = value is double d ? d : 0;
        level = Math.Clamp(level, 0.015, 1.0);
        var totalUnits = Math.Max(0.01, Circumference / Math.Max(0.1, Thickness));
        var dash = Math.Max(0.0001, totalUnits * level);
        return new DoubleCollection { dash, Math.Max(0.0001, totalUnits - dash) };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>AppState → 状态色（取当前调色板语义色：强调=聆听、警告=识别、成功=朗读、危险=出错）。</summary>
public sealed class StateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var state = value?.ToString();
        return state switch
        {
            "Listening" => ThemeBrushes.FindOrFallback("AccentBrush", "#6B7280"),
            "Recognizing" => ThemeBrushes.FindOrFallback("WarningBrush", "#FBBF24"),
            "Speaking" => ThemeBrushes.FindOrFallback("SuccessBrush", "#4ADE80"),
            "Error" => ThemeBrushes.FindOrFallback("DangerBrush", "#FF6B6B"),
            _ => ThemeBrushes.FindOrFallback("SubtleTextBrush", "#8A8AA0")
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
