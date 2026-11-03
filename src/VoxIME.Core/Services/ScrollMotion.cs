namespace VoxIME.Core.Services;

/// <summary>
/// 平滑滚动的纯物理步进逻辑（无 UI 依赖，可离线单测）。
/// 采用「滚轮冲量 + 指数衰减」的惯性模型：每次滚轮给速度一个冲量，之后每帧
/// 按指数衰减推进；连续滚动时速度叠加而不是每个滚轮重新起步，因此不会出现
/// "每转一格就顿一下"的 60fps 观感，也更接近 PCL 的连续滚动。
/// </summary>
public static class ScrollMotion
{
    /// <summary>速度衰减时间常数（秒）：越小停得越快。</summary>
    public const double DecayTimeConstantSeconds = 0.065;

    /// <summary>允许的最大速度（像素/秒），防止快速连滚时速度失控。</summary>
    public const double MaxVelocity = 9000;

    /// <summary>低于该速度视为停止（像素/秒）。</summary>
    public const double StopVelocity = 6;

    /// <summary>单帧最大时间步长（秒），避免卡顿后一次跳跃过大。</summary>
    public const double MaxDeltaSeconds = 0.05;

    /// <summary>一次步进的结果。</summary>
    /// <param name="Completed">是否已停止（速度低于阈值，应同步真实偏移并结束动画）。</param>
    /// <param name="Offset">本帧应滚动到的视觉偏移。</param>
    /// <param name="Velocity">本帧结束后的速度（像素/秒）。</param>
    public readonly record struct Step(bool Completed, double Offset, double Velocity);

    /// <summary>把一次滚轮位移换算成速度冲量。</summary>
    public static double VelocityForDelta(double deltaPixels) =>
        Math.Clamp(deltaPixels / DecayTimeConstantSeconds, -MaxVelocity, MaxVelocity);

    /// <summary>
    /// 按经过时间推进一帧：位置 += 速度 × dt，速度按指数衰减；到边界则夹住并清零速度。
    /// </summary>
    public static Step Next(double offset, double velocity, double deltaSeconds, double minOffset, double maxOffset)
    {
        if (minOffset > maxOffset)
        {
            (minOffset, maxOffset) = (maxOffset, minOffset);
        }

        var dt = Math.Clamp(deltaSeconds, 0, MaxDeltaSeconds);
        var decay = Math.Exp(-dt / DecayTimeConstantSeconds);
        var decayed = velocity * decay;

        // 用连续积分计算本帧位移：∫ v0·e^(-t/τ) dt = v0·τ·(1-e^(-dt/τ))，
        // 这样总位移与帧率无关，单次冲量正好等于滚轮位移。
        var traveled = velocity * DecayTimeConstantSeconds * (1 - decay);
        var next = offset + traveled;

        // 只有真正朝边界外移动时才夹住并停止；起始时恰好在边界且尚未移动的一帧不能误判为结束。
        if (next < minOffset || (next <= minOffset && velocity < 0))
        {
            return new Step(true, minOffset, 0);
        }

        if (next > maxOffset || (next >= maxOffset && velocity > 0))
        {
            return new Step(true, maxOffset, 0);
        }

        return new Step(Math.Abs(decayed) <= StopVelocity, next, decayed);
    }
}
