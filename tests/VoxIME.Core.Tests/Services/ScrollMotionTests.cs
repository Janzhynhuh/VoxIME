using VoxIME.Core.Services;

namespace VoxIME.Core.Tests.Services;

public sealed class ScrollMotionTests
{
    [Fact]
    public void EaseOutFluent_HasExpectedEndpointsAndIsMonotonic()
    {
        Assert.Equal(0, ScrollMotion.EaseOutFluent(0));
        Assert.Equal(1, ScrollMotion.EaseOutFluent(1), 10);

        var previous = 0d;
        for (var i = 0; i <= 20; i++)
        {
            var value = ScrollMotion.EaseOutFluent(i / 20d);
            Assert.InRange(value, previous, 1d);
            previous = value;
        }
    }

    [Fact]
    public void EaseOutFluent_StartsFastAndEndsSoft()
    {
        // 起步阶段速度应明显高于尾段（Fluent 手感）。
        var first = ScrollMotion.EaseOutFluent(0.2) - ScrollMotion.EaseOutFluent(0.1);
        var last = ScrollMotion.EaseOutFluent(0.9) - ScrollMotion.EaseOutFluent(0.8);
        Assert.True(first > last * 3, $"起步速度应远高于收尾：{first:0.###} vs {last:0.###}");
    }

    [Fact]
    public void Next_StartsAtStartOffset()
    {
        var step = ScrollMotion.Next(startOffset: 120, target: 400, elapsedSeconds: 0);

        Assert.False(step.Completed);
        Assert.Equal(120, step.Offset, 6);
    }

    [Fact]
    public void Next_CompletesAtTargetAfterDuration()
    {
        var step = ScrollMotion.Next(startOffset: 120, target: 400, elapsedSeconds: ScrollMotion.DurationSeconds);

        Assert.True(step.Completed);
        Assert.Equal(400, step.Offset);
    }

    [Fact]
    public void Next_IsMonotonicAndNeverOvershoots_Downward()
    {
        var previous = 120d;
        for (var i = 1; i < 100; i++)
        {
            var elapsed = ScrollMotion.DurationSeconds * i / 100d;
            var step = ScrollMotion.Next(120, 400, elapsed);
            Assert.InRange(step.Offset, previous, 400d);
            previous = step.Offset;
        }
    }

    [Fact]
    public void Next_IsMonotonicAndNeverOvershoots_Upward()
    {
        var previous = 400d;
        for (var i = 1; i < 100; i++)
        {
            var elapsed = ScrollMotion.DurationSeconds * i / 100d;
            var step = ScrollMotion.Next(400, 120, elapsed);
            Assert.InRange(step.Offset, 120d, previous);
            previous = step.Offset;
        }
    }

    [Fact]
    public void Next_AlreadyAtTarget_StaysThere()
    {
        var step = ScrollMotion.Next(startOffset: 200, target: 200, elapsedSeconds: 0.1);

        Assert.False(step.Completed);
        Assert.Equal(200, step.Offset, 6);
    }

    [Fact]
    public void Next_NegativeElapsedTreatedAsZero()
    {
        var step = ScrollMotion.Next(startOffset: 50, target: 300, elapsedSeconds: -1);

        Assert.False(step.Completed);
        Assert.Equal(50, step.Offset, 6);
    }
}
