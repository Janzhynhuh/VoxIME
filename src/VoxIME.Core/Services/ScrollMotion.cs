namespace VoxIME.Core.Services;

/// <summary>
/// 平滑滚动的纯步进逻辑（无 UI 依赖，可离线单测）。
/// 采用 PCL 风格的固定时长 + Fluent ease-out（参考其 AniEaseOutFluent(6) 手感）：
/// 每个动画周期固定 300ms，进度按 1-(1-t)^6 缓出，起步快、收尾柔和；滚轮事件累积目标、
/// 重锚起点，因此不会出现"追着当前偏移补帧"导致的回弹。
/// </summary>
public static class ScrollMotion
{
    /// <summary>单个动画周期时长（秒）。</summary>
    public const double DurationSeconds = 0.3;

    /// <summary>Fluent ease-out 的幂次（与 PCL 的 AniEaseOutFluent(6) 一致）。</summary>
    public const int EasePower = 6;

    /// <summary>一次步进的结果。</summary>
    /// <param name="Completed">本轮动画是否已结束（应直接落到目标）。</param>
    /// <param name="Offset">本帧应滚动到的偏移。</param>
    public readonly record struct Step(bool Completed, double Offset);

    /// <summary>Fluent ease-out：0→0、1→1，起步快、尾段缓慢。</summary>
    public static double EaseOutFluent(double progress)
    {
        var value = Math.Clamp(progress, 0d, 1d);
        return 1d - Math.Pow(1d - value, EasePower);
    }

    /// <summary>
    /// 根据「起点偏移、目标偏移、已播放时间」计算下一帧偏移。
    /// 单调逼近目标、不会越过目标；时间到 300ms 后直接落位。
    /// </summary>
    public static Step Next(double startOffset, double target, double elapsedSeconds)
    {
        if (elapsedSeconds >= DurationSeconds)
        {
            return new Step(true, target);
        }

        var progress = elapsedSeconds <= 0 ? 0 : elapsedSeconds / DurationSeconds;
        var eased = EaseOutFluent(progress);
        return new Step(false, startOffset + (target - startOffset) * eased);
    }
}
