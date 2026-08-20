using System.Text.Json;
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

/// <summary>The streaming test probe accumulates content deltas and never sees a /chat/completions
/// envelope, so the pipeline's parsers had to be splittable at that seam. These pin that the split did not
/// change what the pipeline parses: every case runs the SAME input through both doors and demands the same
/// answer, so the two implementations cannot drift apart later.</summary>
public class PromptParseContentTests
{
    /// <summary>Wraps bare content in the envelope a non-streamed call returns.</summary>
    private static string Envelope(string content) =>
        JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });

    [Fact]
    public void Tags_parse_the_same_content_through_either_door()
    {
        const string content = """Here is my reasoning. [{"tag":"Budget","weight":0.9},{"tag":"Hiring","weight":0.4}]""";

        var viaContent = TagsPrompt.ParseContent(content);
        var viaEnvelope = TagsPrompt.ParseResponse(Envelope(content));

        Assert.Equal(["Budget", "Hiring"], viaContent.Select(t => t.Tag));
        Assert.Equal(viaEnvelope.Select(t => (t.Tag, t.Weight)), viaContent.Select(t => (t.Tag, t.Weight)));
    }

    [Fact]
    public void Tags_return_nothing_rather_than_throwing_when_there_is_no_array()
    {
        // The parser is TOTAL - a model that ignores the format leaves the pipeline with no tags, and the
        // test rail reports exactly that. Nothing here may throw.
        Assert.Empty(TagsPrompt.ParseContent("I could not find any topics."));
    }

    [Fact]
    public void Actions_parse_the_same_content_through_either_door()
    {
        const string content =
            """The meeting agreed two things. [{"action":"Send the deck","actor":"Sam","deadline":"2026-09-01"}]""";

        var viaContent = ActionsPrompt.ParseContent(content);
        var viaEnvelope = ActionsPrompt.ParseResponse(Envelope(content));

        var only = Assert.Single(viaContent);
        Assert.Equal("Send the deck", only.Text);
        Assert.Equal("Sam", only.Actor);
        Assert.Equal("2026-09-01", only.Deadline);
        Assert.Equal(viaEnvelope.Select(a => (a.Text, a.Actor, a.Deadline)),
            viaContent.Select(a => (a.Text, a.Actor, a.Deadline)));
    }

    [Fact]
    public void Actions_return_nothing_rather_than_throwing_when_there_is_no_array()
    {
        Assert.Empty(ActionsPrompt.ParseContent("No actions were agreed."));
    }

    [Fact]
    public void Summaries_parse_the_same_content_through_either_door()
    {
        const string content = "Quarterly Planning\nThe team agreed to revise the forecast before Friday.";

        var viaContent = SummarizationPrompt.ParseContent(content, needName: true);
        var viaEnvelope = SummarizationPrompt.ParseResponse(Envelope(content), needName: true);

        Assert.Equal("Quarterly Planning", viaContent.Name);
        Assert.Equal(viaEnvelope.Summary, viaContent.Summary);
        Assert.Equal(viaEnvelope.Name, viaContent.Name);
    }

    [Fact]
    public void Summaries_keep_plain_prose_when_no_name_was_asked_for()
    {
        var result = SummarizationPrompt.ParseContent(
            "The team agreed to revise the forecast before Friday.", needName: false);

        Assert.Null(result.Name);
        Assert.Contains("revise the forecast", result.Summary);
    }
}
