using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class ChatContextMeterTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(8, 2)]
    [InlineData(100, 25)]
    public void EstimateFromChars_IsCeilingOfQuarter(long chars, int expected) =>
        Assert.Equal(expected, ChatContextMeter.EstimateFromChars(chars));

    [Fact]
    public void EstimateFromChars_ClampsNegativeToZero() =>
        Assert.Equal(0, ChatContextMeter.EstimateFromChars(-10));

    [Fact]
    public void EstimateTokens_NullOrEmpty_IsZero()
    {
        Assert.Equal(0, ChatContextMeter.EstimateTokens(null));
        Assert.Equal(0, ChatContextMeter.EstimateTokens(""));
    }

    [Fact]
    public void EstimateTokens_UsesStringLength()
    {
        // 9 chars -> ceil(9/4) = 3.
        Assert.Equal(3, ChatContextMeter.EstimateTokens("123456789"));
    }

    // ---- Images ----

    /// <summary>Roughly the common tile-based approximation. It is an estimate, like the chars/4 above -
    /// the point is that an attached screenshot stops being invisible to the dial, not that the number is
    /// exact. With no cap on attachments, this gauge is the only thing telling a user a tray has grown
    /// expensive.</summary>
    [Theory]
    [InlineData(1920, 1080, 2765)]
    [InlineData(800, 600, 640)]
    [InlineData(1, 1, 1)]
    public void EstimateImageTokens_IsCeilingOfPixelsOver750(int w, int h, int expected) =>
        Assert.Equal(expected, ChatContextMeter.EstimateImageTokens(w, h));

    [Fact]
    public void EstimateImageTokens_ASmallerImageCostsLess() =>
        Assert.True(ChatContextMeter.EstimateImageTokens(640, 480)
            < ChatContextMeter.EstimateImageTokens(1920, 1080));

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-4, 100)]
    public void EstimateImageTokens_NonPositiveDimensionsCostNothing(int w, int h) =>
        Assert.Equal(0, ChatContextMeter.EstimateImageTokens(w, h));
}
