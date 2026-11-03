using VoxIME.Core.Services;

namespace VoxIME.Core.Tests.Services;

public sealed class ScrollMotionTests
{
    [Fact]
    public void VelocityForDelta_ConvertsPixelsToVelocityWithDecayConstant()
    {
        Assert.Equal(100 / ScrollMotion.DecayTimeConstantSeconds, ScrollMotion.VelocityForDelta(100), 6);
        Assert.Equal(0, ScrollMotion.VelocityForDelta(0));
        Assert.Equal(-ScrollMotion.VelocityForDelta(80), ScrollMotion.VelocityForDelta(-80), 6);
    }

    [Fact]
    public void VelocityForDelta_IsClamped()
    {
        Assert.Equal(ScrollMotion.MaxVelocity, ScrollMotion.VelocityForDelta(999999));
        Assert.Equal(-ScrollMotion.MaxVelocity, ScrollMotion.VelocityForDelta(-999999));
    }

    [Fact]
    public void SingleWheelImpulse_MovesAboutRequestedPixels()
    {
        // 速度从 v0 = delta/tau 起按 e^(-t/tau) 衰减，总位移收敛到 delta。
        var offset = 0d;
        var velocity = ScrollMotion.VelocityForDelta(100);
        var completed = false;
        var iterations = 0;
        while (!completed && iterations++ < 10000)
        {
            var step = ScrollMotion.Next(offset, velocity, 1d / 144d, 0, 10000);
            offset = step.Offset;
            velocity = step.Velocity;
            completed = step.Completed;
        }

        Assert.True(completed);
        Assert.InRange(offset, 99, 101);
    }

    [Fact]
    public void Next_DecaysMonotonicallyToStop()
    {
        var offset = 0d;
        var velocity = ScrollMotion.VelocityForDelta(300);
        var previous = offset;
        var completed = false;
        var iterations = 0;
        while (!completed && iterations++ < 10000)
        {
            var step = ScrollMotion.Next(offset, velocity, 1d / 144d, 0, 10000);
            Assert.True(step.Offset >= previous, "单方向滚动不应反向");
            Assert.InRange(step.Velocity, 0, velocity);
            offset = step.Offset;
            velocity = step.Velocity;
            previous = offset;
            completed = step.Completed;
        }

        Assert.True(completed);
        Assert.InRange(offset, 299, 301);
    }

    [Fact]
    public void Next_ClampsAtBoundsAndStops()
    {
        var step = ScrollMotion.Next(offset: 0, velocity: -2000, deltaSeconds: 0.05, minOffset: 0, maxOffset: 1000);
        Assert.True(step.Completed);
        Assert.Equal(0, step.Offset);
        Assert.Equal(0, step.Velocity);

        var top = ScrollMotion.Next(offset: 1000, velocity: 2000, deltaSeconds: 0.05, minOffset: 0, maxOffset: 1000);
        Assert.True(top.Completed);
        Assert.Equal(1000, top.Offset);
        Assert.Equal(0, top.Velocity);
    }

    [Fact]
    public void Next_ZeroElapsedAtBoundary_DoesNotComplete()
    {
        // 起始恰好在顶部、速度向下、dt=0：这一帧还没移动，不能误判为"撞边界结束"。
        var step = ScrollMotion.Next(offset: 0, velocity: 900, deltaSeconds: 0, minOffset: 0, maxOffset: 2700);

        Assert.False(step.Completed);
        Assert.Equal(0, step.Offset, 6);
    }
    [Fact]
    public void Next_ClampsDeltaSeconds()
    {
        var step = ScrollMotion.Next(offset: 0, velocity: 1000, deltaSeconds: 999, minOffset: 0, maxOffset: 10000);

        Assert.True(step.Offset <= 1000 * ScrollMotion.MaxDeltaSeconds);
        Assert.True(step.Velocity > 0);
    }

    [Fact]
    public void Next_ZeroVelocity_CompletesImmediately()
    {
        var step = ScrollMotion.Next(offset: 500, velocity: 0, deltaSeconds: 1d / 144, minOffset: 0, maxOffset: 1000);

        Assert.True(step.Completed);
        Assert.Equal(500, step.Offset, 6);
        Assert.Equal(0, step.Velocity, 6);
    }
}
