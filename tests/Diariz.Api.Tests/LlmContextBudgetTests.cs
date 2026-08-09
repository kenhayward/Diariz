using Diariz.Api.Services;

namespace Diariz.Api.Tests;

/// <summary>The single context budget every LLM call site now derives from the model's context window.
/// Before this existed each site carried its own hard-coded constant (24k/32k/48k chars) which had no
/// relationship to the configured window - a 131k-token model was being fed ~6k tokens of context.</summary>
public class LlmContextBudgetTests
{
    [Fact]
    public void CharsFor_ReservesHeadroomForTheTemplateAndCompletion()
    {
        // 131,072 tokens * 60% input share * 4 chars/token.
        Assert.Equal(314_572, LlmContextBudget.CharsFor(131_072));
    }

    [Fact]
    public void CharsFor_ScalesWithTheWindow()
    {
        Assert.Equal(LlmContextBudget.CharsFor(64_000) * 2, LlmContextBudget.CharsFor(128_000));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void CharsFor_NonPositiveWindow_FallsBackToTheFloor(int window)
    {
        // A misconfigured or unset window must never produce a zero budget - that would send the model
        // an empty transcript rather than a truncated one.
        Assert.Equal(LlmContextBudget.MinimumChars, LlmContextBudget.CharsFor(window));
    }

    [Fact]
    public void CharsFor_TinyWindow_StillClearsTheFloor()
    {
        Assert.Equal(LlmContextBudget.MinimumChars, LlmContextBudget.CharsFor(100));
    }

    [Fact]
    public void CharsFor_HugeWindow_DoesNotOverflow()
    {
        Assert.True(LlmContextBudget.CharsFor(int.MaxValue) > 0);
    }
}
