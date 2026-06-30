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
    public async Task CountMentions_GroupsBySpeaker()
    {
        var search = new FakeTranscriptSearch
        {
            Hits =
            [
                new(Guid.NewGuid(), "R", June1, 1000, "Alice", "budget", 0.9),
                new(Guid.NewGuid(), "R", June1, 2000, "Alice", "budget again", 0.9),
                new(Guid.NewGuid(), "R", June1, 3000, "Bob", "budget too", 0.9),
            ],
        };
        var result = await new CountMentionsTool(search).ExecuteAsync(Args("{\"term\":\"budget\"}"), Ctx, default);

        Assert.Contains("3 mention(s)", result);
        Assert.Contains("Alice: 2", result);
        Assert.Contains("Bob: 1", result);
    }

    [Fact]
    public async Task CountMentions_PassesSpeakerFilter()
    {
        var search = new FakeTranscriptSearch();
        await new CountMentionsTool(search).ExecuteAsync(Args("{\"term\":\"x\",\"speaker\":\"Alice\"}"), Ctx, default);
        Assert.Equal("Alice", search.LastSearch!.Value.Speaker);
    }
}
