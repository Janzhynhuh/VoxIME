using VoxIME.Core.Services;

namespace VoxIME.Core.Tests.Services;

public sealed class Live2DWindowSizingTests
{
    [Fact]
    public void FitToModel_KeepsHeightAndAdaptsWidthToAspect()
    {
        var (width, height) = Live2DWindowSizing.FitToModel(380, 560, modelWidth: 1000, modelHeight: 2000);

        Assert.Equal(560, height);
        Assert.Equal(280, width); // 1000/2000 * 560
    }

    [Fact]
    public void FitToModel_WideModel_GrowsWidthWithinLimit()
    {
        var (width, height) = Live2DWindowSizing.FitToModel(380, 560, modelWidth: 2000, modelHeight: 1000);

        Assert.Equal(560, height);
        Assert.Equal(1120, width); // 2 * 560
    }

    [Fact]
    public void FitToModel_ExtremeAspect_ClampsBothSides()
    {
        var (wide, wideHeight) = Live2DWindowSizing.FitToModel(380, 560, modelWidth: 20000, modelHeight: 1000);
        var (narrow, narrowHeight) = Live2DWindowSizing.FitToModel(380, 560, modelWidth: 100, modelHeight: 20000);

        Assert.Equal(Live2DWindowSizing.MaxSize, wide);
        Assert.Equal(Live2DWindowSizing.MinSize, wideHeight);
        Assert.Equal(Live2DWindowSizing.MinSize, narrow);
        Assert.Equal(Live2DWindowSizing.MaxSize, narrowHeight);
    }

    [Fact]
    public void FitToModel_InvalidModelSize_ReturnsClampedCurrentSize()
    {
        var (width, height) = Live2DWindowSizing.FitToModel(50, 9999, modelWidth: 0, modelHeight: double.NaN);

        Assert.Equal(Live2DWindowSizing.MinSize, width);
        Assert.Equal(Live2DWindowSizing.MaxSize, height);
    }
}
