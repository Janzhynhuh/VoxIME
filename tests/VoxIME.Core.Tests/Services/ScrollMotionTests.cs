using VoxIME.Core.Services;

namespace VoxIME.Core.Tests.Services;

public sealed class ScrollMotionTests
{
    [Fact]
    public void DirectionFor_UsesOffsetDeltaSign()
    {
        Assert.Equal(1, ScrollMotion.DirectionFor(10, 110));
        Assert.Equal(-1, ScrollMotion.DirectionFor(110, 10));
        Assert.Equal(0, ScrollMotion.DirectionFor(42, 42));
    }

    [Fact]
    public void Next_DoesNotCrossTarget_AndStopsAtTarget()
    {
        var current = 0d;
        var target = 300d;
        var completed = false;
        var iterations = 0;

        while (!completed && iterations++ < 1000)
        {
            var step = ScrollMotion.Next(current, target, 1, 0.5);
            Assert.InRange(step.Offset, current, target);
            Assert.True(step.Offset >= current, "向下滚动不应反向移动");
            current = step.Offset;
            completed = step.Completed;
        }

        Assert.True(completed);
        Assert.Equal(target, current);
    }

    [Fact]
    public void Next_UpwardScroll_IsMonotonicAndNeverNegative()
    {
        var current = 300d;
        var target = 0d;
        var completed = false;
        var iterations = 0;

        while (!completed && iterations++ < 1000)
        {
            var step = ScrollMotion.Next(current, target, -1, 0.4);
            Assert.InRange(step.Offset, target, current);
            current = step.Offset;
            completed = step.Completed;
        }

        Assert.True(completed);
        Assert.Equal(target, current);
    }

    [Fact]
    public void Next_WhenOvershot_CompletesInsteadOfAnimatingBackwards()
    {
        // 实际偏移已经越过目标（例如 WPF 取整/夹取造成的轻微越界），期望方向仍向下：
        // 应直接落位结束，而不是继续往反方向做动画。
        var step = ScrollMotion.Next(current: 101, target: 100, direction: 1, alpha: 0.5);

        Assert.True(step.Completed);
        Assert.Equal(100, step.Offset);
    }

    [Fact]
    public void Next_NearTargetJustSnaps()
    {
        var step = ScrollMotion.Next(current: 99.2, target: 100, direction: 1, alpha: 0.25);

        Assert.True(step.Completed);
        Assert.Equal(100, step.Offset);
    }

    [Fact]
    public void Next_UnknownDirectionCompletes()
    {
        var step = ScrollMotion.Next(current: 10, target: 200, direction: 0, alpha: 0.5);

        Assert.True(step.Completed);
        Assert.Equal(200, step.Offset);
    }

    [Fact]
    public void Next_AlphaIsClamped()
    {
        var tiny = ScrollMotion.Next(current: 0, target: 100, direction: 1, alpha: -5);
        Assert.False(tiny.Completed);
        Assert.Equal(0, tiny.Offset, 6);

        var huge = ScrollMotion.Next(current: 0, target: 100, direction: 1, alpha: 5);
        Assert.True(huge.Completed);
        Assert.Equal(100, huge.Offset);
    }
}
