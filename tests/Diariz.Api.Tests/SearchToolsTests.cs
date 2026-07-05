using System.Text.Json;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Api.Tools;

namespace Diariz.Api.Tests;

/// <summary>The search-derived tools (search_transcripts, when_was_discussed, count_mentions) over a faked
/// <see cref="ITranscriptSearch"/>.</summary>
public class SearchToolsTests
{
    private static readonly DateTimeOffset June1 = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset June10 = new(2026, 6, 10, 9, 0, 0, TimeSpan.Zero);
    private static readonly ChatToolContext Ctx = new(Guid.NewGuid(), []);

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task SearchTranscripts_DelegatesAndFormats()
    {
        var search = new FakeTranscriptSearch
        {
            Hits = [new(Guid.NewGuid(), "Standup", June1, 1000, "Alice", "the budget is tight", 0.9)],
        };
        var result = await new SearchTranscriptsTool(search).ExecuteAsync(Args("{\"query\":\"budget\"}"), Ctx, default);

        Assert.Equal("budget", search.LastSearch!.Value.Phrase);
        Assert.Null(search.LastSearch.Value.Speaker);
        Assert.Contains("Who: Alice", result);
        Assert.Contains("/recordings/", result);
    }

    [Fact]
    public async Task WhenWasDiscussed_ReportsEarliestAndLatest()
    {
        var early = new TranscriptHit(Guid.NewGuid(), "Kickoff", June1, 1000, "Alice", "let's plan the budget", 0.8);
        var late = new TranscriptHit(Guid.NewGuid(), "Review", June10, 2000, "Bob", "the budget overran", 0.8);
        var search = new FakeTranscriptSearch { Hits = [late, early] }; // unordered on purpose

        var result = await new WhenWasDiscussedTool(search).ExecuteAsync(Args("{\"topic\":\"budget\"}"), Ctx, default);

        Assert.Contains("2 match(es)", result);
        var earliestLine = result.Split('\n').First(l => l.StartsWith("Earliest:"));
        var latestLine = result.Split('\n').First(l => l.StartsWith("Latest:"));
        Assert.Contains("Kickoff", earliestLine); // June 1 is earliest
        Assert.Contains("Review", latestLine); // June 10 is latest
    }

    [Fact]
    public async Task WhenWasDiscussed_NoMatches()
    {
        var result = await new WhenWasDiscussedTool(new FakeTranscriptSearch())
            .ExecuteAsync(Args("{\"topic\":\"unicorns\"}"), Ctx, default);
        Assert.Contains("No mentions", result);
    }

    [Fact]
    public async Task CountMentions_ReportsExactTotal_GroupedBySpeaker()
    {
        // The exact grouped count comes straight from CountMentionsAsync - no cap, no "at least".
        var search = new FakeTranscriptSearch { Counts = [new("Alice", 90), new("Bob", 47)] };
        var result = await new CountMentionsTool(search).ExecuteAsync(Args("{\"term\":\"budget\"}"), Ctx, default);

        Assert.Contains("137 mention(s)", result); // 90 + 47, well past the old cap
        Assert.DoesNotContain("At least", result);
        Assert.Contains("Alice: 90", result);
        Assert.Contains("Bob: 47", result);
    }

    [Fact]
    public async Task CountMentions_NoMatches()
    {
        var result = await new CountMentionsTool(new FakeTranscriptSearch())
            .ExecuteAsync(Args("{\"term\":\"unicorns\"}"), Ctx, default);
        Assert.Contains("No mentions", result);
    }

    [Fact]
    public async Task CountMentions_PassesSpeakerFilter()
    {
        var search = new FakeTranscriptSearch();
        await new CountMentionsTool(search).ExecuteAsync(Args("{\"term\":\"x\",\"speaker\":\"Alice\"}"), Ctx, default);
        Assert.Equal("Alice", search.LastCount!.Value.Speaker);
    }

    [Fact]
    public async Task SpeakerTalkTime_ComputesPercentagesFromExactTotals()
    {
        var search = new FakeTranscriptSearch { TalkTime = [new("Alice", 9000), new("Bob", 3000)] };
        var result = await new SpeakerTalkTimeTool(search)
            .ExecuteAsync(Args("{\"scope\":\"current\"}"), new ChatToolContext(Guid.NewGuid(), [Guid.NewGuid()]), default);

        Assert.Contains("Alice: 00:09 (75%)", result);
        Assert.Contains("Bob: 00:03 (25%)", result);
        Assert.NotNull(search.LastTalkTime!.Value.Scope); // scope 'current' forwarded a recording filter
    }

    [Theory]
    [InlineData("{\"query\":\"x\"}", 50)]        // default = the raised cap
    [InlineData("{\"query\":\"x\",\"limit\":5}", 5)]
    [InlineData("{\"query\":\"x\",\"limit\":999}", 50)] // clamped to the ceiling
    [InlineData("{\"query\":\"x\",\"limit\":0}", 1)]    // clamped up to 1
    public async Task SearchTranscripts_HonoursLimit(string json, int expected)
    {
        var search = new FakeTranscriptSearch();
        await new SearchTranscriptsTool(search).ExecuteAsync(Args(json), Ctx, default);
        Assert.Equal(expected, search.LastSearch!.Value.Limit);
    }
}
