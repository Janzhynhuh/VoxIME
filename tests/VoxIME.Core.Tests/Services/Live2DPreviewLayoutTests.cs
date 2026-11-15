using VoxIME.Core.Services;
using Xunit;

namespace VoxIME.Core.Tests.Services;

public sealed class Live2DPreviewLayoutTests
{
    [Fact]
    public void Fit_KeepsAspectWithinBounds()
    {
        var (width, height) = Live2DPreviewLayout.Fit(482, 600, 520, 320);
        Assert.Equal(257.1, Math.Round(width, 1));
        Assert.Equal(320.0, Math.Round(height, 1));
    }

    [Fact]
    public void Fit_WideWindow_IsBoundedByHeight()
    {
        var (width, height) = Live2DPreviewLayout.Fit(1600, 400, 520, 320);
        Assert.Equal(520, Math.Round(width, 0));
        Assert.Equal(130, Math.Round(height, 0));
    }

    [Fact]
    public void Fit_InvalidWindowFallsBackToMaxBox()
    {
        Assert.Equal((520.0, 320.0), Live2DPreviewLayout.Fit(0, -1, 520, 320));
        Assert.Equal((520.0, 320.0), Live2DPreviewLayout.Fit(double.NaN, 600, 520, 320));
    }

    [Fact]
    public void Fit_InvalidMaxFallsBackSafely()
    {
        var (width, height) = Live2DPreviewLayout.Fit(482, 600, 0, -10);
        Assert.Equal(1.0, width);
        Assert.Equal(1.0, height);
    }
}
