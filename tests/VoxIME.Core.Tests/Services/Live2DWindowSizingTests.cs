using VoxIME.Core.Services;

namespace VoxIME.Core.Tests.Services;

public sealed class Live2DWindowSizingTests
{
    [Fact]
    public void FitToModel_KeepsHeightAndTightensWidthWithPadding()
    {
        var (width, height) = Live2DWindowSizing.FitToModel(380, 560, modelWidth: 1000, modelHeight: 2000);

        Assert.Equal(560, height);
        Assert.Equal(276, width); // 1000 * (560/2000 * 0.95) + 2*5
    }

    [Fact]
    public void FitToModel_AppliesRenderScaleToWidth()
    {
        var (width, height) = Live2DWindowSizing.FitToModel(
            380, 560, modelWidth: 1000, modelHeight: 2000, scale: 1.5);

        Assert.Equal(560, height);
        Assert.Equal(409, width); // 1000 * (560/2000 * 0.95 * 1.5) + 10
    }

    [Fact]
    public void FitToModel_WideModel_GrowsWidthWithinLimit()
    {
        var (width, height) = Live2DWindowSizing.FitToModel(380, 560, modelWidth: 2000, modelHeight: 1000);

        Assert.Equal(560, height);
        Assert.Equal(1074, width); // 2000 * (560/1000 * 0.95) + 10
    }

    [Fact]
    public void FitToModel_ExtremeAspect_ClampsWidthButKeepsHeight()
    {
        var (wide, wideHeight) = Live2DWindowSizing.FitToModel(380, 560, modelWidth: 20000, modelHeight: 1000);
        var (narrow, narrowHeight) = Live2DWindowSizing.FitToModel(380, 560, modelWidth: 100, modelHeight: 20000);

        Assert.Equal(Live2DWindowSizing.MaxSize, wide);
        Assert.Equal(560, wideHeight);
        Assert.Equal(Live2DWindowSizing.MinSize, narrow);
        Assert.Equal(560, narrowHeight);
    }

    [Fact]
    public void FitToModel_InvalidModelSize_ReturnsClampedCurrentSize()
    {
        var (width, height) = Live2DWindowSizing.FitToModel(50, 9999, modelWidth: 0, modelHeight: double.NaN);

        Assert.Equal(Live2DWindowSizing.MinSize, width);
        Assert.Equal(Live2DWindowSizing.MaxSize, height);
    }
}
