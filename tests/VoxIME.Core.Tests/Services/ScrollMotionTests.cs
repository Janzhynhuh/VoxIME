using VoxIME.Core.Services;

namespace VoxIME.Core.Tests.Services;

public sealed class ScrollMotionTests
{
    [Fact]
    public void Next_AtTarget_CompletesImmediately()
    {
        var step = ScrollMotion.Next(current: 200, target: 200, deltaSeconds: 0);

        Assert.True(step.Completed);
        Assert.Equal(200, step.Offset, 6);
    }

    [Fact]
    public void Next_ZeroDeltaSeconds_DoesNotMove()
    {
        var step = ScrollMotion.Next(current: 0, target: 100, deltaSeconds: 0);

        Assert.False(step.Completed);
        Assert.Equal(0, step.Offset, 6);
    }

    [Fact]
    public void Next_ApproachesTargetMonotonicallyWithoutOvershoot()
    {
        var current = 0d;
        var target = 300d;
        var previous = current;
        var completed = false;
        var iterations = 0;

        while (!completed && iterations++ < 10000)
        {
            var step = ScrollMotion.Next(current, target, 1d / 144d);
            Assert.InRange(step.Offset, previous, target);
            current = step.Offset;
            previous = current;
            completed = step.Completed;
        }

        Assert.True(completed);
        Assert.Equal(target, current, 6);
    }

    [Fact]
    public void Next_ApproachesTargetUpward()
    {
        var step = ScrollMotion.Next(current: 300, target: 100, deltaSeconds: 1d / 144);

        Assert.False(step.Completed);
        Assert.InRange(step.Offset, 100, 300);
    }

    [Fact]
    public void Next_IsFrameRateIndependentBelowDeltaCap()
    {
        // 0.04s 一步 vs 0.01s 四步，结果应一致（指数衰减可组合）。
        var oneShot = ScrollMotion.Next(current: 0, target: 1000, deltaSeconds: 0.04).Offset;

        var current = 0d;
        for (var i = 0; i < 4; i++)
        {
            current = ScrollMotion.Next(current, 1000, 0.01).Offset;
        }

        Assert.Equal(oneShot, current, 3);
    }

    [Fact]
    public void Next_SnapsWhenWithinThreshold()
    {
        var step = ScrollMotion.Next(current: 99.5, target: 100, deltaSeconds: 1d / 144);

        Assert.True(step.Completed);
        Assert.Equal(100, step.Offset);
    }

    [Fact]
    public void Next_ClampsDeltaSeconds()
    {
        var clamped = ScrollMotion.Next(current: 0, target: 10000, deltaSeconds: 999).Offset;
        var capped = ScrollMotion.Next(current: 0, target: 10000, deltaSeconds: ScrollMotion.MaxDeltaSeconds).Offset;

        Assert.Equal(capped, clamped, 6);
        Assert.InRange(clamped, 0, 10000);
    }
}
