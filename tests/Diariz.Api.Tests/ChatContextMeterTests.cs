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
}
