using Diariz.Api.Services;

namespace Diariz.Api.Tests;

/// <summary>The fit arithmetic for vision attachments. Pure - no Skia, no storage - which is the whole
/// reason it is a separate type: these are the cases that matter and none of them need an image.</summary>
public class VisionImageBoundsTests
{
    [Theory]
    [InlineData(800, 600)]
    [InlineData(1920, 1080)] // exactly at the bound is NOT a resize
    [InlineData(1920, 100)]
    [InlineData(100, 1080)]
    [InlineData(1, 1)]
    public void Fit_WithinBounds_ReturnsNull(int w, int h) =>
        Assert.Null(VisionImageBounds.Fit(w, h));

    [Fact]
    public void Fit_OverOnWidthOnly_ScalesByWidth()
    {
        var fit = VisionImageBounds.Fit(3840, 1000);
        Assert.NotNull(fit);
        Assert.Equal(1920, fit!.Value.Width);
        Assert.Equal(500, fit.Value.Height);
    }

    [Fact]
    public void Fit_OverOnHeightOnly_ScalesByHeight()
    {
        var fit = VisionImageBounds.Fit(1000, 2160);
        Assert.NotNull(fit);
        Assert.Equal(1080, fit!.Value.Height);
        Assert.Equal(500, fit.Value.Width);
    }

    [Fact]
    public void Fit_OverOnBoth_FitsInsideTheBox()
    {
        var fit = VisionImageBounds.Fit(3840, 2160);
        Assert.NotNull(fit);
        Assert.Equal(1920, fit!.Value.Width);
        Assert.Equal(1080, fit.Value.Height);
    }

    /// <summary>A tall strip must clamp on its LONG axis. Taking the larger scale instead would return
    /// something still far outside the box, which is the mutation this test exists to catch.</summary>
    [Fact]
    public void Fit_ExtremeAspectRatio_ClampsOnTheLongAxis()
    {
        var fit = VisionImageBounds.Fit(1000, 8000);
        Assert.NotNull(fit);
        Assert.Equal(1080, fit!.Value.Height);
        Assert.Equal(135, fit.Value.Width);
        Assert.True(fit.Value.Width <= VisionImageBounds.MaxWidth);
        Assert.True(fit.Value.Height <= VisionImageBounds.MaxHeight);
    }

    [Theory]
    [InlineData(3840, 2160)]
    [InlineData(5000, 100)]
    [InlineData(100, 5000)]
    [InlineData(12000, 9000)]
    public void Fit_NeverReturnsSomethingLargerThanTheInput(int w, int h)
    {
        var fit = VisionImageBounds.Fit(w, h);
        Assert.NotNull(fit);
        Assert.True(fit!.Value.Width <= w);
        Assert.True(fit.Value.Height <= h);
    }

    /// <summary>A sliver must not round away to a zero-width image, which no encoder will accept.</summary>
    [Fact]
    public void Fit_ExtremeSliver_NeverRoundsAnAxisToZero()
    {
        var fit = VisionImageBounds.Fit(2, 100_000);
        Assert.NotNull(fit);
        Assert.True(fit!.Value.Width >= 1);
        Assert.True(fit.Value.Height >= 1);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-5, 100)]
    public void Fit_NonPositiveDimensions_ReturnsNull(int w, int h) =>
        Assert.Null(VisionImageBounds.Fit(w, h));
}
