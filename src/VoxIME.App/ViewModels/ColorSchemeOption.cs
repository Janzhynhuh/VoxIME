using System.Windows.Media;
using VoxIME.App.Infrastructure;
using VoxIME.Core.Appearance;

namespace VoxIME.App.ViewModels;

/// <summary>
/// 设置页「配色方案」卡片：名称/说明/适用模式 + 当前明暗下的迷你色卡预览。
/// 色卡直接取 <see cref="ColorScheme"/> 的语义色，保证预览与实际生效主题一致。
/// </summary>
public sealed class ColorSchemeOption : ObservableObject
{
    private bool _isSelected;

    public ColorSchemeOption(ColorScheme scheme, bool isLight, bool isSelected)
    {
        ArgumentNullException.ThrowIfNull(scheme);

        SchemeId = scheme.Id;
        DisplayName = scheme.DisplayName;
        Description = scheme.Description;
        ModeLabel = scheme.SupportsLight && scheme.SupportsDark
            ? "浅色 / 深色"
            : isLight ? "仅浅色" : "仅深色";
        _isSelected = isSelected;

        var colors = scheme.For(isLight) ?? ThemeCatalog.Default.For(isLight)
            ?? throw new InvalidOperationException("配色方案缺少当前明暗下的颜色。");
        PreviewBackgroundBrush = ThemeBrushes.FromHex(colors.Background);
        PreviewSurfaceBrush = ThemeBrushes.FromHex(colors.Surface);
        PreviewBorderBrush = ThemeBrushes.FromHex(colors.Border);
        PreviewAccentBrush = ThemeBrushes.FromHex(colors.Accent);
        PreviewTextBrush = ThemeBrushes.FromHex(colors.Text);
    }

    public string SchemeId { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public string ModeLabel { get; }

    /// <summary>迷你窗口背景。</summary>
    public Brush PreviewBackgroundBrush { get; }

    /// <summary>迷你卡片背景。</summary>
    public Brush PreviewSurfaceBrush { get; }

    /// <summary>迷你卡片边框。</summary>
    public Brush PreviewBorderBrush { get; }

    /// <summary>强调色圆点。</summary>
    public Brush PreviewAccentBrush { get; }

    /// <summary>模拟正文的色条。</summary>
    public Brush PreviewTextBrush { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
