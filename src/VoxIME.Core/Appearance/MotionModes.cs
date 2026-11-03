namespace VoxIME.Core.Appearance;

/// <summary>
/// 界面动效档位（与主题/配色正交）：
/// <c>expressive</c> 明显（默认，位移/缩放更足）、<c>subtle</c> 克制、<c>off</c> 关闭。
/// 具体时长/缓动由 UI 层 Motion 辅助类解释，Core 只负责配置合法性与解析。
/// </summary>
public static class MotionModes
{
    /// <summary>明显：默认档，带位移与轻微缩放。</summary>
    public const string Expressive = "expressive";

    /// <summary>克制：时长更短、幅度更小。</summary>
    public const string Subtle = "subtle";

    /// <summary>关闭：全部瞬时完成。</summary>
    public const string Off = "off";

    /// <summary>全部合法取值。</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Expressive, Subtle, Off };

    /// <summary>规范化；无法识别时回退 <see cref="Expressive"/>（默认观感）。</summary>
    public static string Normalize(string? value)
    {
        var text = value?.Trim();
        if (string.Equals(text, Subtle, StringComparison.OrdinalIgnoreCase))
        {
            return Subtle;
        }

        if (string.Equals(text, Off, StringComparison.OrdinalIgnoreCase))
        {
            return Off;
        }

        return Expressive;
    }

    /// <summary>是否启用动效（<c>off</c> 时不播放 Storyboard）。</summary>
    public static bool IsEnabled(string? value) => Normalize(value) != Off;

    /// <summary>是否为“克制”档。</summary>
    public static bool IsSubtle(string? value) => Normalize(value) == Subtle;
}
