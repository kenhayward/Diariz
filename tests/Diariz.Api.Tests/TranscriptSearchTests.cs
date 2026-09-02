using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class TranscriptSearchTests
{
    /// <summary>The ANN walk width. It was previously exercised only through the recall integration test,
    /// which was removed in issue #726 for asserting a property of the machine it ran on - so the clamp is
    /// pinned directly here rather than left with no coverage at all.</summary>
    [Theory]
    [InlineData(1, 100)]      // far below the floor; pgvector's own default of 40 is smaller still
    [InlineData(10, 100)]     // 80, floored - the case the floor exists for
    [InlineData(20, 160)]     // inside the range, scaled 8x
    [InlineData(125, 1000)]   // exactly the ceiling
    [InlineData(5000, 1000)]  // way over it: clamped, never passed through to pgvector
    public void EfSearch_ScalesWithTheLimit_AndStaysInsidePgvectorsPermittedRange(int limit, int expected) =>
        Assert.Equal(expected, TranscriptSearch.EfSearch(limit));
}
