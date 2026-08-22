using Diariz.Api.Services;

namespace Diariz.Api.Tests;

/// <summary>The fit arithmetic for OCR captures. Pure - no Skia, no storage - for the same reason
/// <see cref="VisionImageBoundsTests"/> is: these are the cases that matter and none of them need an image.
///
/// The cap is a PARAMETER here, unlike the vision bound's constant. Four models were measured against one
/// capture and each wanted a different number - olmOCR peaks at 2048 and degrades at 2560, GLM-OCR wants
/// 2560, Qwen3-VL improves all the way to 2560 - so a constant would have been wrong for somebody whatever
/// value it took.</summary>
public class OcrImageBoundsTests
{
    [Theory]
    [InlineData(800, 600)]
    [InlineData(2048, 1358)] // exactly at the cap is NOT a resize
    [InlineData(2048, 100)]
    [InlineData(100, 2048)]
    [InlineData(1, 1)]
    public void Fit_WithinTheCap_ReturnsNull(int w, int h) =>
        Assert.Null(OcrImageBounds.Fit(w, h, 2048));

    [Fact]
    public void Fit_OverOnWidthOnly_ScalesByWidth()
    {
        var fit = OcrImageBounds.Fit(4096, 1000, 2048);
        Assert.NotNull(fit);
        Assert.Equal(2048, fit!.Value.Width);
        Assert.Equal(500, fit.Value.Height);
    }

    [Fact]
    public void Fit_OverOnHeightOnly_ScalesByHeight()
    {
        var fit = OcrImageBounds.Fit(1000, 4096, 2048);
        Assert.NotNull(fit);
        Assert.Equal(2048, fit!.Value.Height);
        Assert.Equal(500, fit.Value.Width);
    }

    /// <summary>The SMALLER scale binds - the axis that would still be outside the box under the other.</summary>
    [Fact]
    public void Fit_OverOnBoth_FitsInsideTheSquare()
    {
        var fit = OcrImageBounds.Fit(3840, 2160, 2048);
        Assert.NotNull(fit);
        Assert.True(fit!.Value.Width <= 2048);
        Assert.True(fit.Value.Height <= 2048);
        Assert.Equal(2048, fit.Value.Width); // width is the binding axis on a 16:9 capture
    }

    /// <summary>A sliver must not scale its short axis to zero - no encoder accepts a zero-width image.</summary>
    [Fact]
    public void Fit_ExtremeAspectRatio_NeverRoundsToZero()
    {
        var fit = OcrImageBounds.Fit(2, 100000, 2048);
        Assert.NotNull(fit);
        Assert.True(fit!.Value.Width >= 1);
        Assert.True(fit.Value.Height >= 1);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-5, 100)]
    [InlineData(100, -5)]
    public void Fit_WithNonsenseDimensions_ReturnsNull(int w, int h) =>
        Assert.Null(OcrImageBounds.Fit(w, h, 2048));

    /// <summary>A cap of zero or less means "no cap" rather than "shrink to nothing" - a misconfigured
    /// parameter must pass the capture through, not destroy it.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Fit_WithNonsenseCap_PassesThrough(int cap) =>
        Assert.Null(OcrImageBounds.Fit(3840, 2160, cap));

    /// <summary>The cap is honoured as given: the same image resolves differently for two models, which is
    /// the entire reason the cap is a parameter.</summary>
    [Fact]
    public void Fit_HonoursTheCapItIsGiven()
    {
        Assert.Equal(2048, OcrImageBounds.Fit(2560, 1697, 2048)!.Value.Width);
        Assert.Null(OcrImageBounds.Fit(2560, 1697, 2560));
    }
}
