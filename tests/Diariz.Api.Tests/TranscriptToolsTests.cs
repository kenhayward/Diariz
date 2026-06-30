using System.Text.Json;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Api.Tools;

namespace Diariz.Api.Tests;

/// <summary>The three transcript tools: argument handling and that they delegate to the search engine with
/// the right parameters and format results in the standard form. Search itself is faked.</summary>
public class TranscriptToolsTests
{
    private static readonly DateTimeOffset When1 = new(2026, 6, 26, 13, 25, 0, TimeSpan.Zero);

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task WhoSaidThat_PassesPhrase_AndFormatsHits()
    {
        var search = new FakeTranscriptSearch
        {
            Hits = [new(Guid.NewGuid(), "Standup", When1, 252_000, "Alice", "cut the budget", 0.9)],
        };
        var tool = new WhoSaidThatTool(search);
        var ctx = new ChatToolContext(Guid.NewGuid(), []);

        var result = await tool.ExecuteAsync(Args("{\"phrase\":\"budget\"}"), ctx, default);

        Assert.Equal("budget", search.LastSearch!.Value.Phrase);
        Assert.Null(search.LastSearch.Value.Speaker);
        Assert.Null(search.LastSearch.Value.Scope); // default scope = all
        Assert.Contains("Who: Alice", result);
    }

    [Fact]
    public async Task WhoSaidThat_CurrentScope_PassesSelection()
    {
        var search = new FakeTranscriptSearch();
        var selected = new[] { Guid.NewGuid() };
        var ctx = new ChatToolContext(Guid.NewGuid(), selected);

        await new WhoSaidThatTool(search).ExecuteAsync(Args("{\"phrase\":\"x\",\"scope\":\"current\"}"), ctx, default);

        Assert.Equal(selected, search.LastSearch!.Value.Scope);
    }

    [Fact]
    public async Task WhoSaidThat_MissingPhrase_Explains()
    {
        var result = await new WhoSaidThatTool(new FakeTranscriptSearch())
            .ExecuteAsync(Args("{}"), new ChatToolContext(Guid.NewGuid(), []), default);
        Assert.Contains("phrase", result);
    }

    [Fact]
    public async Task WhatDidTheySay_PassesSpeakerAndTopic()
    {
        var search = new FakeTranscriptSearch();
        var ctx = new ChatToolContext(Guid.NewGuid(), []);

        await new WhatDidTheySayTool(search)
            .ExecuteAsync(Args("{\"speaker\":\"Alice\",\"topic\":\"budget\"}"), ctx, default);

        Assert.Equal("budget", search.LastSearch!.Value.Phrase);
        Assert.Equal("Alice", search.LastSearch.Value.Speaker);
    }

    [Fact]
    public async Task WhatDidTheySay_RequiresBoth()
    {
        var tool = new WhatDidTheySayTool(new FakeTranscriptSearch());
        var ctx = new ChatToolContext(Guid.NewGuid(), []);
        Assert.Contains("speaker", await tool.ExecuteAsync(Args("{\"topic\":\"x\"}"), ctx, default));
        Assert.Contains("topic", await tool.ExecuteAsync(Args("{\"speaker\":\"Alice\"}"), ctx, default));
    }

    [Fact]
    public async Task ListRecordings_PassesFilters_AndFormats()
    {
        var search = new FakeTranscriptSearch
        {
            Recordings = [new(Guid.NewGuid(), "Budget Review", When1, "Microphone", 60_000, ["Alice"], "cut the budget")],
        };
        var ctx = new ChatToolContext(Guid.NewGuid(), []);

        var result = await new ListRecordingsTool(search).ExecuteAsync(
            Args("{\"from\":\"2026-06-01\",\"speaker\":\"Alice\",\"contains\":\"budget\"}"), ctx, default);

        Assert.Equal(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), search.LastList!.Value.From);
        Assert.Equal("Alice", search.LastList.Value.Speaker);
        Assert.Equal("budget", search.LastList.Value.Contains);
        Assert.Contains("Name: Budget Review", result);
        Assert.Contains("Match: \"cut the budget\"", result);
    }
}
