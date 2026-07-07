using Diariz.Api.Contracts;
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class NotesEnhancerTests
{
    private static MeetingNoteDto Note(int i, string text, long? ms = null) =>
        new(Guid.NewGuid(), text, ms, i, DateTimeOffset.UtcNow);

    private static readonly List<SegmentDto> Segments =
    [
        new(Guid.NewGuid(), "SPEAKER_00", "Alice", 0, 60_000, "We discussed the budget.", null),
        new(Guid.NewGuid(), "SPEAKER_01", "Bob", 60_000, 120_000, "Comp needs to be market-rate.", null),
    ];

    [Fact]
    public void BuildMessages_NumbersEachLine_IncludesStamps_AndDemandsStrictJson()
    {
        var messages = NotesEnhancer.BuildMessages(
            [Note(0, "Comp expectations", 61_000), Note(1, "IPO experience APAC")], Segments, 10_000);

        Assert.Equal(2, messages.Count);
        Assert.Equal("system", messages[0].Role);
        Assert.Contains("JSON array", messages[0].Content);
        Assert.Contains("notDiscussed", messages[0].Content);
        Assert.Contains("0: Comp expectations (written at 1:01)", messages[1].Content);
        Assert.Contains("1: IPO experience APAC", messages[1].Content);
        Assert.DoesNotContain("1: IPO experience APAC (written", messages[1].Content); // no stamp when null
        // The transcript carries [ms=...] start markers - without them the model cannot return timesMs.
        Assert.Contains("[ms=60000] Bob: Comp needs to be market-rate.", messages[1].Content);
        Assert.Contains("[ms=0] Alice: We discussed the budget.", messages[1].Content);
    }

    [Fact]
    public void ParseResponse_MapsExpansionsAndTimes()
    {
        var result = NotesEnhancer.ParseResponse(
            """[{"i":0,"expansion":"Bob said comp must be market-rate.","timesMs":[60000]},{"i":1,"notDiscussed":true}]""", 2);

        Assert.Equal(2, result.Count);
        Assert.False(result[0].NotDiscussed);
        Assert.Equal("Bob said comp must be market-rate.", result[0].Expansion);
        Assert.Equal([60_000L], result[0].TimesMs);
        Assert.True(result[1].NotDiscussed);
    }

    [Fact]
    public void ParseResponse_RepairsMissingLines_ToNotDiscussed()
    {
        var result = NotesEnhancer.ParseResponse("""[{"i":1,"expansion":"covered","timesMs":[]}]""", 3);
        Assert.Equal(3, result.Count);
        Assert.True(result[0].NotDiscussed);
        Assert.Equal("covered", result[1].Expansion);
        Assert.True(result[2].NotDiscussed);
    }

    [Fact]
    public void ParseResponse_IgnoresOutOfRangeAndDuplicates_AndUnfencesCodeBlocks()
    {
        var fenced = "```json\n[{\"i\":0,\"expansion\":\"first\",\"timesMs\":[]},{\"i\":0,\"expansion\":\"dupe\",\"timesMs\":[]},{\"i\":9,\"expansion\":\"oob\",\"timesMs\":[]}]\n```";
        var result = NotesEnhancer.ParseResponse(fenced, 1);
        Assert.Single(result);
        Assert.Equal("first", result[0].Expansion); // first wins; duplicate + out-of-range dropped
    }

    [Fact]
    public void ParseResponse_Garbage_NeverThrows_AllNotDiscussed()
    {
        var result = NotesEnhancer.ParseResponse("sorry, I can't do that", 2);
        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.True(e.NotDiscussed));
    }

    [Fact]
    public void ParseResponse_ExtractsTheArray_FromReasoningModelPreamble()
    {
        // Local reasoning models (e.g. qwen) emit thinking prose before the JSON - the parser must find the
        // last balanced array in the text, not require the whole response to be JSON (the ActionsPrompt
        // lesson). The prose may even contain stray brackets [like this].
        var response =
            "Let me check each note [line by line]. The first is covered at 60000ms.\n" +
            """Here is the result: [{"i":0,"expansion":"Covered.","timesMs":[60000]}]""";

        var result = NotesEnhancer.ParseResponse(response, 1);

        Assert.False(result[0].NotDiscussed);
        Assert.Equal("Covered.", result[0].Expansion);
        Assert.Equal([60_000L], result[0].TimesMs);
    }
}
