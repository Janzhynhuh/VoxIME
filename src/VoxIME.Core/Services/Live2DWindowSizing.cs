namespace VoxIME.Core.Services;

/// <summary>
/// 桌宠窗口自适应：按 Live2D 模型的原始宽高比计算窗口尺寸。
/// 以当前窗口高度为基准计算宽度，并把结果钳制在配置允许的 160..1600 范围内。
/// </summary>
public static class Live2DWindowSizing
{
    public const int MinSize = 160;
    public const int MaxSize = 1600;

    /// <summary>
    /// 计算适配模型比例的窗口尺寸；模型尺寸非法时返回当前尺寸（钳制后）。
    /// </summary>
    public static (int Width, int Height) FitToModel(
        int currentWidth,
        int currentHeight,
        double modelWidth,
        double modelHeight)
    {
        var safeWidth = Math.Clamp(currentWidth, MinSize, MaxSize);
        var safeHeight = Math.Clamp(currentHeight, MinSize, MaxSize);
        if (!double.IsFinite(modelWidth) || !double.IsFinite(modelHeight) || modelWidth <= 0 || modelHeight <= 0)
        {
            return (safeWidth, safeHeight);
        }

        var aspect = modelWidth / modelHeight; // 宽 / 高
        var height = (double)safeHeight;
        var width = height * aspect;

        if (width > MaxSize)
        {
            width = MaxSize;
            height = width / aspect;
        }
        else if (width < MinSize)
        {
            width = MinSize;
            height = width / aspect;
        }

        height = Math.Clamp(height, MinSize, MaxSize);
        width = Math.Clamp(width, MinSize, MaxSize);
        return ((int)Math.Round(width), (int)Math.Round(height));
    }
}
