namespace VoxIME.Core.Services;

/// <summary>
/// 桌宠窗口自适应：以当前窗口高度为基准，把窗口宽度收紧到模型显示宽度左右各留
/// <see cref="DefaultPadding"/> DIP 的 padding（对齐前端 canvas 的 95% fill + 5px padding）。
/// 高度保持不变，只调整宽度；结果钳制在配置允许的 160..1600 范围内。
/// </summary>
public static class Live2DWindowSizing
{
    public const int MinSize = 160;
    public const int MaxSize = 1600;

    /// <summary>前后端约定的模型填充比例：app.js 中 fitScale 使用 0.95。</summary>
    public const double FillRatio = 0.95;

    /// <summary>窗口左右各留的 padding（WPF DIP，约等于前端 5px）。</summary>
    public const int DefaultPadding = 5;

    /// <summary>
    /// 计算紧贴模型宽度的窗口尺寸；高度沿用当前配置高度。
    /// 模型尺寸非法时返回当前尺寸（钳制后）。
    /// </summary>
    /// <param name="currentWidth">当前配置宽度（模型非法时作为兜底）。</param>
    /// <param name="currentHeight">当前配置高度（最终窗口高度）。</param>
    /// <param name="modelWidth">Live2D 模型原始宽度（像素/canvas 单位）。</param>
    /// <param name="modelHeight">Live2D 模型原始高度。</param>
    /// <param name="scale">模型渲染缩放（Live2DConfig.Scale）。</param>
    /// <param name="padding">窗口左右各留的 padding（DIP）。</param>
    public static (int Width, int Height) FitToModel(
        int currentWidth,
        int currentHeight,
        double modelWidth,
        double modelHeight,
        double scale = 1.0,
        int padding = DefaultPadding)
    {
        var safeWidth = Math.Clamp(currentWidth, MinSize, MaxSize);
        var safeHeight = Math.Clamp(currentHeight, MinSize, MaxSize);
        var safeScale = double.IsFinite(scale) ? Math.Clamp(scale, 0.2, 3.0) : 1.0;
        var safePadding = Math.Clamp(padding, 0, 100);

        if (!double.IsFinite(modelWidth) || !double.IsFinite(modelHeight) || modelWidth <= 0 || modelHeight <= 0)
        {
            return (safeWidth, safeHeight);
        }

        // app.js: fitScale = min(windowWidth / modelWidth, windowHeight / modelHeight) * 0.95 * scale。
        // 宽度按此反推后，height 约束仍是较小值，因此模型实际显示宽度 = modelWidth * heightRatio。
        var heightRatio = safeHeight / modelHeight * FillRatio * safeScale;
        var modelDisplayWidth = modelWidth * heightRatio;
        var width = Math.Clamp(modelDisplayWidth + safePadding * 2.0, MinSize, MaxSize);

        return ((int)Math.Round(width), safeHeight);
    }
}
