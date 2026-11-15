namespace VoxIME.Core.Services;

/// <summary>
/// 设置页「模型位置预览」的尺寸计算：在最大框内按桌宠窗口宽高比等比缩放预览区域。
/// </summary>
public static class Live2DPreviewLayout
{
    /// <summary>
    /// 计算预览显示尺寸：保持 windowWidth/windowHeight 的宽高比，并完整放进 maxWidth × maxHeight。
    /// 参数非法时返回安全的 1×1 或最大框，避免 NaN/负数传入 WPF。
    /// </summary>
    public static (double Width, double Height) Fit(
        double windowWidth,
        double windowHeight,
        double maxWidth,
        double maxHeight)
    {
        var safeMaxWidth = IsFinite(maxWidth) && maxWidth > 0 ? maxWidth : 1.0;
        var safeMaxHeight = IsFinite(maxHeight) && maxHeight > 0 ? maxHeight : 1.0;

        if (!IsFinite(windowWidth) || !IsFinite(windowHeight) || windowWidth <= 0 || windowHeight <= 0)
        {
            return (safeMaxWidth, safeMaxHeight);
        }

        var scale = Math.Min(safeMaxWidth / windowWidth, safeMaxHeight / windowHeight);
        return (
            Math.Max(1.0, windowWidth * scale),
            Math.Max(1.0, windowHeight * scale));
    }

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);
}
