namespace VoxIME.Core.Services;

/// <summary>
/// 平滑滚动的纯步进逻辑（无 UI 依赖，可离线单测）。
/// 采用「指数跟随目标」曲线：滚轮事件累加目标偏移，视觉偏移每帧按
/// 1-e^(-dt/τ) 的比例逼近目标。连续滚动时目标持续领先，速度为当前距离/τ，
/// 因此不会出现"每格重新起步再停下"的脉冲感，更接近拖动滚动条那种连续跟手。
/// </summary>
public static class ScrollMotion
{
    /// <summary>平滑时间常数（秒）：越小越跟手，越大越顺滑。</summary>
    public const double SmoothingTimeConstantSeconds = 0.05;

    /// <summary>距目标小于该值时直接落位（像素）。</summary>
    public const double SnapThreshold = 0.75;

    /// <summary>单帧最大时间步长（秒），避免卡顿后一次跳跃过大。</summary>
    public const double MaxDeltaSeconds = 0.05;

    /// <summary>一次步进的结果。</summary>
    /// <param name="Completed">是否已到位（应同步真实偏移并结束动画）。</param>
    /// <param name="Offset">本帧应滚动到的视觉偏移。</param>
    public readonly record struct Step(bool Completed, double Offset);

    /// <summary>
    /// 按经过时间把视觉偏移向目标逼近：单调、不越过目标，帧率无关。
    /// </summary>
    public static Step Next(double current, double target, double deltaSeconds)
    {
        var distance = target - current;
        if (Math.Abs(distance) <= SnapThreshold)
        {
            return new Step(true, target);
        }

        var dt = Math.Clamp(deltaSeconds, 0, MaxDeltaSeconds);
        if (dt <= 0)
        {
            return new Step(false, current);
        }

        var alpha = 1d - Math.Exp(-dt / SmoothingTimeConstantSeconds);
        var next = current + distance * alpha;
        return Math.Abs(target - next) <= SnapThreshold
            ? new Step(true, target)
            : new Step(false, next);
    }
}
