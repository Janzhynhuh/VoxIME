using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.Win32;
using VoxIME.Core.Appearance;
using VoxIME.Core.Configuration;

namespace VoxIME.App.Infrastructure;

/// <summary>
/// 运行期主题管理：把「主题模式（跟随系统/浅色/深色）」+「配色方案」解析成完整调色板资源字典，
/// 替换 <see cref="Application"/> 资源里的调色板；所有 XAML 颜色引用都用 DynamicResource，
/// 因此换主题时已打开的窗口会立即重绘。
///
/// 调色板数据全部来自 <see cref="ThemeCatalog"/> / <see cref="ThemeTokens"/>（VoxIME.Core.Appearance），
/// 新增配色或派生色不需要改任何 XAML。
/// </summary>
public static class ThemeManager
{
    private static ResourceDictionary? _palette;
    private static bool _systemEventsHooked;
    private static string _lastThemeMode = ThemeModes.Dark;
    private static string _lastColorSchemeId = ThemeCatalog.DefaultColorSchemeId;

    /// <summary>当前生效外观（主题模式 / 实际明暗 / 配色方案）。</summary>
    public static ResolvedTheme Current { get; private set; } =
        ThemeResolver.Resolve(ThemeModes.Dark, ThemeCatalog.DefaultColorSchemeId, systemIsLight: false);

    /// <summary>调色板已替换（自绘颜色/托盘等非资源绑定元素可据此刷新）。在 UI 线程触发。</summary>
    public static event EventHandler<ResolvedTheme>? ThemeChanged;

    /// <summary>启动时调用：先套用配置里的外观，再订阅系统应用模式变化。</summary>
    public static void Initialize(AppConfig config)
    {
        HookSystemPreferences();
        ApplyConfig(config);
    }

    /// <summary>按配置应用外观（保存设置 / 导入配置 / 恢复默认后调用）。</summary>
    public static void ApplyConfig(AppConfig? config)
    {
        if (config?.Ui is null)
        {
            return;
        }

        Apply(config.Ui.Theme, config.Ui.ColorScheme);
    }

    /// <summary>应用指定主题模式与配色方案（设置页实时预览）；任意线程可调用。</summary>
    public static void Apply(string? themeMode, string? colorSchemeId) =>
        WindowHelper.RunOnUi(() => ApplyCore(themeMode, colorSchemeId));

    /// <summary>系统当前是否为浅色应用模式。</summary>
    public static bool IsSystemLight => SystemTheme.IsLight();

    private static void ApplyCore(string? themeMode, string? colorSchemeId)
    {
        var resolved = ThemeResolver.Resolve(themeMode, colorSchemeId, SystemTheme.IsLight());
        _lastThemeMode = resolved.ThemeMode;
        _lastColorSchemeId = resolved.ColorSchemeId;

        var app = Application.Current;
        if (app is null)
        {
            // 极端情况下（测试宿主/无 Application）只更新状态。
            Current = resolved;
            return;
        }

        var nextPalette = BuildPaletteDictionary(resolved);
        var merged = app.Resources.MergedDictionaries;
        if (_palette is not null)
        {
            merged.Remove(_palette);
        }

        merged.Add(nextPalette);
        _palette = nextPalette;
        Current = resolved;

        try
        {
            ThemeChanged?.Invoke(null, resolved);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ThemeManager] 通知主题变化失败：{ex.Message}");
        }
    }

    private static void HookSystemPreferences()
    {
        if (_systemEventsHooked)
        {
            return;
        }

        _systemEventsHooked = true;
        try
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
        catch (Exception ex)
        {
            // 无桌面会话/策略禁止时忽略：跟随系统退化为启动时读取一次。
            System.Diagnostics.Debug.WriteLine($"[ThemeManager] 订阅系统主题失败：{ex.Message}");
        }
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // 只有「跟随系统」模式需要跟着系统切换；浅色/深色是用户显式选择，保持不变。
        if (ThemeModes.Normalize(_lastThemeMode) != ThemeModes.System)
        {
            return;
        }

        try
        {
            Apply(_lastThemeMode, _lastColorSchemeId);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ThemeManager] 跟随系统切换失败：{ex.Message}");
        }
    }

    private static ResourceDictionary BuildPaletteDictionary(ResolvedTheme theme)
    {
        var tokens = ThemeTokens.Build(theme.Colors, theme.IsLight);
        var dictionary = new ResourceDictionary();

        foreach (var pair in tokens)
        {
            if (!pair.Key.EndsWith("Color", StringComparison.Ordinal))
            {
                continue;
            }

            var color = (Color)ColorConverter.ConvertFromString(pair.Value);
            dictionary[pair.Key] = color;

            var brush = new SolidColorBrush(color);
            brush.Freeze();
            dictionary[pair.Key[..^"Color".Length] + "Brush"] = brush;
        }

        var accent = GetColor(tokens, "AccentColor");
        dictionary["AccentGradient"] = CreateLinearGradient(
            GetColor(tokens, "AccentGradientStart"), accent, GetColor(tokens, "AccentGradientEnd"), horizontal: false, midOffset: 0.55);
        dictionary["AccentGradientHorizontal"] = CreateLinearGradient(
            accent, accent, GetColor(tokens, "AccentGradientSecondary"), horizontal: true, midOffset: 0.55);
        dictionary["ListeningGradient"] = CreateLinearGradient(
            accent, accent, GetColor(tokens, "AccentGradientSecondary"), horizontal: true, midOffset: 0.55);
        dictionary["HeroGradient"] = CreateLinearGradient(
            GetColor(tokens, "HeroGradientStart"), GetColor(tokens, "HeroGradientMid"), GetColor(tokens, "HeroGradientEnd"),
            horizontal: false, midOffset: 0.6);

        var glow = new DropShadowEffect
        {
            BlurRadius = 22,
            ShadowDepth = 0,
            Opacity = 0.65,
            Color = accent
        };
        glow.Freeze();
        dictionary["AccentGlow"] = glow;

        return dictionary;
    }

    private static Color GetColor(IReadOnlyDictionary<string, string> tokens, string key) =>
        ThemeColorMath.TryParse(tokens.TryGetValue(key, out var hex) ? hex : null, out var color)
            ? Color.FromArgb(color.A, color.R, color.G, color.B)
            : Colors.Transparent;

    private static LinearGradientBrush CreateLinearGradient(Color start, Color mid, Color end, bool horizontal, double midOffset)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = horizontal ? new Point(1, 0) : new Point(1, 1)
        };
        brush.GradientStops.Add(new GradientStop(start, 0));
        brush.GradientStops.Add(new GradientStop(mid, midOffset));
        brush.GradientStops.Add(new GradientStop(end, 1));
        brush.Freeze();
        return brush;
    }
}
