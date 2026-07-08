using Diariz.Api.Contracts;
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class TagsPromptTests
{
    private static readonly IReadOnlyList<SegmentDto> Segments =
    [
        new SegmentDto(Guid.NewGuid(), "SPEAKER_00", "Alice", 0, 3000, "Let's finalise the Q3 budget allocation."),
        new SegmentDto(Guid.NewGuid(), "SPEAKER_01", "Bob", 3000, 6000, "Agreed, and we should shortlist vendors."),
    ];

    private static string Chat(string content) =>
        "{\"choices\":[{\"message\":{\"content\":" + System.Text.Json.JsonSerializer.Serialize(content) + "}}]}";

    [Fact]
    public void BuildMessages_AsksForAJsonArrayOfTags_WithTheTranscript()
    {
        var msgs = TagsPrompt.BuildMessages(TagsPrompt.DefaultTemplate, Segments);

        Assert.Equal(2, msgs.Count);
        Assert.Equal("system", msgs[0].Role);
        Assert.Contains("tag", msgs[0].Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("JSON", msgs[0].Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("user", msgs[1].Role);
        Assert.StartsWith("Transcript:\n", msgs[1].Content);
        Assert.Contains("Alice: Let's finalise the Q3 budget allocation.", msgs[1].Content);
    }

    [Fact]
    public void ParseResponse_ReadsTagsWithWeights()
    {
        var content = """[{"tag":"Budget Planning","weight":0.9},{"tag":"Vendor Selection","weight":0.4}]""";
        var tags = TagsPrompt.ParseResponse(Chat(content));

        Assert.Equal(2, tags.Count);
        Assert.Equal("Budget Planning", tags[0].Tag);
        Assert.Equal(0.9, tags[0].Weight, 3);
        Assert.Equal("Vendor Selection", tags[1].Tag);
    }

    [Fact]
    public void ParseResponse_EmptyArray_ReturnsNoTags()
    {
        Assert.Empty(TagsPrompt.ParseResponse(Chat("[]")));
    }

    [Fact]
    public void ParseResponse_MissingWeight_DefaultsToZero()
    {
        var t = Assert.Single(TagsPrompt.ParseResponse(Chat("""[{"tag":"Onboarding"}]""")));
        Assert.Equal("Onboarding", t.Tag);
        Assert.Equal(0.0, t.Weight);
    }

    [Fact]
    public void ParseResponse_ClampsWeightsToZeroOne()
    {
        var content = """[{"tag":"Hot Topic","weight":1.7},{"tag":"Cold Topic","weight":-0.2}]""";
        var tags = TagsPrompt.ParseResponse(Chat(content));

        Assert.Equal(1.0, tags[0].Weight);
        Assert.Equal(0.0, tags[1].Weight);
    }

    [Fact]
    public void ParseResponse_ToleratesCodeFences()
    {
        var content = "```json\n[{\"tag\":\"Data Migration\",\"weight\":0.5}]\n```";
        var t = Assert.Single(TagsPrompt.ParseResponse(Chat(content)));
        Assert.Equal("Data Migration", t.Tag);
    }

    [Fact]
    public void ParseResponse_TakesTheArrayAfterReasoning_EvenWithBracketsInTheProse()
    {
        // Reasoning models prepend prose (possibly with brackets) before the JSON; take the LAST array.
        var content =
            "The meeting mostly covered [budget] matters and a vendor shortlist.\n\n" +
            """[{"tag":"Budget Planning","weight":0.8}]""";
        var t = Assert.Single(TagsPrompt.ParseResponse(Chat(content)));
        Assert.Equal("Budget Planning", t.Tag);
    }

    [Fact]
    public void ParseResponse_Garbage_ReturnsEmpty()
    {
        Assert.Empty(TagsPrompt.ParseResponse(Chat("The transcript is too thin to tag.")));
    }

    [Fact]
    public void ParseResponse_DropsEntriesWithNoTagText()
    {
        var content = """[{"tag":"","weight":0.9},{"tag":"   ","weight":0.8},{"tag":"Real One","weight":0.7}]""";
        var t = Assert.Single(TagsPrompt.ParseResponse(Chat(content)));
        Assert.Equal("Real One", t.Tag);
    }

    [Fact]
    public void ParseResponse_DedupesCaseInsensitively_FirstWins()
    {
        var content = """[{"tag":"AI Enablement","weight":0.9},{"tag":"Ai enablement","weight":0.3}]""";
        var t = Assert.Single(TagsPrompt.ParseResponse(Chat(content)));
        Assert.Equal("AI Enablement", t.Tag);
        Assert.Equal(0.9, t.Weight, 3);
    }

    [Fact]
    public void ParseResponse_CapsAtMaxTags()
    {
        var entries = string.Join(",", Enumerable.Range(0, 20).Select(i => $$"""{"tag":"Topic {{i}}","weight":0.5}"""));
        var tags = TagsPrompt.ParseResponse(Chat($"[{entries}]"));
        Assert.Equal(TagsPrompt.MaxTags, tags.Count);
        Assert.Equal("Topic 0", tags[0].Tag);
    }

    [Fact]
    public void ParseResponse_SkipsNonObjectElements()
    {
        var content = """["stray string",{"tag":"Kept","weight":0.5},42]""";
        var t = Assert.Single(TagsPrompt.ParseResponse(Chat(content)));
        Assert.Equal("Kept", t.Tag);
    }
}
