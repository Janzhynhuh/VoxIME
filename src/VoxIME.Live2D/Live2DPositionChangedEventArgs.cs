namespace VoxIME.Live2D;

/// <summary>
/// 桌宠窗口位置变化事件（用户拖动窗口后触发）。
/// 该事件为 <c>ILive2DController</c> 之外的附加能力：宿主可据此把 Left/Top 持久化到配置。
/// </summary>
public sealed class Live2DPositionChangedEventArgs : EventArgs
{
    public Live2DPositionChangedEventArgs(double left, double top, string? displayKey = null)
    {
        Left = left;
        Top = top;
        DisplayKey = displayKey ?? string.Empty;
    }

    /// <summary>窗口左上角 X（WPF 设备无关像素）。</summary>
    public double Left { get; }

    /// <summary>窗口左上角 Y（WPF 设备无关像素）。</summary>
    public double Top { get; }

    /// <summary>显示器记忆键（会话类型+设备名+物理分辨率）；为空表示无法识别。</summary>
    public string DisplayKey { get; }
}
