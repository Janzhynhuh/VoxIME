namespace VoxIME.Core.Appearance;

/// <summary>
/// 一套配色方案在某个明暗模式下的语义色（十六进制，<c>#RRGGBB</c>）。
/// 只保存品牌/语义色，按钮悬停、滚动条、HUD 等派生色统一由 <see cref="ThemeTokens"/> 计算，
/// 避免每套配色手写几十个色值产生漂移。
/// </summary>
public sealed class ThemeColorSet
{
    /// <summary>窗口主背景。</summary>
    public required string Background { get; init; }

    /// <summary>卡片/面板背景。</summary>
    public required string Surface { get; init; }

    /// <summary>卡片悬停/次级按钮背景。</summary>
    public required string SurfaceHover { get; init; }

    /// <summary>输入框背景。</summary>
    public required string Input { get; init; }

    /// <summary>弹层（下拉框/提示）背景。</summary>
    public required string Popup { get; init; }

    /// <summary>强调色（主按钮渐变中段、选中态、链接）。</summary>
    public required string Accent { get; init; }

    /// <summary>强调辅助色（水平渐变终点、聆听状态）。</summary>
    public required string AccentSecondary { get; init; }

    /// <summary>正文颜色。</summary>
    public required string Text { get; init; }

    /// <summary>次要文字。</summary>
    public required string TextSubtle { get; init; }

    /// <summary>弱化/禁用文字与图标。</summary>
    public required string TextMuted { get; init; }

    /// <summary>普通边框/分割线。</summary>
    public required string Border { get; init; }

    /// <summary>强边框（窗口外框、悬停边框）。</summary>
    public required string BorderStrong { get; init; }

    /// <summary>成功/朗读状态。</summary>
    public required string Success { get; init; }

    /// <summary>危险/错误状态。</summary>
    public required string Danger { get; init; }

    /// <summary>警告/识别状态。</summary>
    public required string Warning { get; init; }

    /// <summary>
    /// 主按钮文字色覆盖；为 null 时按强调色亮度自动选择黑/白。
    /// 仅个别配色方案需要强制指定（例如 Catppuccin Latte 统一用黑字）。
    /// </summary>
    public string? AccentForeground { get; init; }
}
