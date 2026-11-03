namespace VoxIME.Core.Services;

/// <summary>
/// 平滑滚动的纯步进逻辑（无 UI 依赖，可离线单测）。
/// UI 层每帧把当前偏移、目标偏移、期望方向与缓动系数传进来，得到下一帧偏移。
///
/// 关键规则（解决缓慢滚动时的"回弹"）：
/// 1. 距离目标小于阈值 → 直接落位，不再补动画；
/// 2. 实际偏移已经越过目标（WPF 滚动偏移会取整/夹取）或方向与期望相反 →
///    直接落位，绝不反向补动画；
/// 3. 正常插值不允许越过目标。
/// </summary>
public static class ScrollMotion
{
    /// <summary>距离目标小于该值时直接落位（像素）。</summary>
    public const double SnapThreshold = 1.5;

    /// <summary>插值后距离目标小于该值时直接落到目标（像素）。</summary>
    public const double TailSnapThreshold = 1.0;

    /// <summary>一次步进的结果。</summary>
    /// <param name="Completed">是否已结束本轮滚动（直接落位）。</param>
    /// <param name="Offset">下一帧应滚动到的偏移。</param>
    public readonly record struct Step(bool Completed, double Offset);

    /// <summary>期望方向：目标在当前之上为 -1，之下为 +1，相等为 0。</summary>
    public static int DirectionFor(double current, double target) => Math.Sign(target - current);

    /// <summary>
    /// 计算下一帧滚动偏移。
    /// </summary>
    /// <param name="current">当前实际偏移。</param>
    /// <param name="target">目标偏移。</param>
    /// <param name="direction">期望方向（+1 向下 / -1 向上 / 0 未定）。</param>
    /// <param name="alpha">本帧缓动比例 0..1（由时间增量算出）。</param>
    public static Step Next(double current, double target, int direction, double alpha)
    {
        var distance = target - current;

        // 到位/越界/方向不符：直接落位，避免反向动画（回弹）。
        if (direction == 0
            || Math.Sign(distance) != direction
            || Math.Abs(distance) <= SnapThreshold)
        {
            return new Step(true, target);
        }

        var ratio = Math.Clamp(alpha, 0d, 1d);
        var next = current + distance * ratio;
        var remaining = target - next;

        // 不允许越过目标：剩余距离方向反转或已经很小时直接落到目标。
        if (Math.Abs(remaining) <= TailSnapThreshold || Math.Sign(remaining) != Math.Sign(distance))
        {
            return new Step(true, target);
        }

        return new Step(false, next);
    }
}
