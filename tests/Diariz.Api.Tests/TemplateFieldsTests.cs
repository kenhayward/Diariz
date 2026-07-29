using Diariz.Api.Contracts;
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

/// <summary>The <c>field</c> block substitutions shared by the minutes pipeline and the formula run pipeline
/// (<see cref="TemplateFields"/>). The two table-valued fields - <c>action_items</c> and <c>transcript</c> - emit a
/// BARE table: the template author supplies the heading, exactly as they do for every other field.</summary>
public class TemplateFieldsTests
{
    private static readonly IReadOnlyList<SegmentDto> Segments =
    [
        new SegmentDto(Guid.NewGuid(), "SPEAKER_00", "Alice", 852, 3896, "So here's the thing."),
        new SegmentDto(Guid.NewGuid(), "SPEAKER_01", "Bob", 64000, 66500, "Right, on the API."),
    ];

    private static string? Resolve(
        string name,
        IReadOnlyList<ExtractedAction>? actions = null,
        IReadOnlyList<SegmentDto>? segments = null) =>
        TemplateFields.Resolve(
            name, new DateTimeOffset(2026, 7, 6, 9, 30, 0, TimeSpan.Zero), "Team Sync",
            ["Alice", "Bob"], 90_000, actions ?? [], notesMarkdown: null, segments);

    [Fact]
    public void Transcript_RendersATimeSpeakerTextTable()
    {
        var md = Resolve("transcript", segments: Segments);

        Assert.NotNull(md);
        Assert.StartsWith("| Time | Speaker | Text |", md);      // no heading - the table is the whole value
        Assert.Contains("| 00:00 | Alice | So here's the thing. |", md);
        Assert.Contains("| 01:04 | Bob | Right, on the API. |", md);
    }

    [Fact]
    public void Transcript_IsNullWhenThereAreNoSegments()
    {
        // Null (not an empty table) so the composer drops the block, and the section with it if that empties it.
        Assert.Null(Resolve("transcript", segments: []));
        Assert.Null(Resolve("transcript", segments: null));
    }

    [Fact]
    public void ActionItems_RendersABareTable_WithNoHeadingOfItsOwn()
    {
        var md = Resolve("action_items", actions: [new ExtractedAction("Send the report", "Bob", "2026-03-06")]);

        Assert.NotNull(md);
        Assert.DoesNotContain("## Action Items", md);            // the template's own heading is the only one
        Assert.StartsWith("| Action | Owner | Due date |", md);
        Assert.Contains("| Send the report | Bob | 2026-03-06 |", md);
    }

    [Fact]
    public void ActionItems_IsNullWhenThereAreNoActions()
    {
        Assert.Null(Resolve("action_items", actions: []));
    }

    [Fact]
    public void TheOtherFieldsAreUnchanged()
    {
        Assert.Equal("2026-07-06", Resolve("date"));
        Assert.Equal("09:30", Resolve("time"));
        Assert.Equal("Team Sync", Resolve("title"));
        Assert.Equal("Alice, Bob", Resolve("attendees"));
        Assert.Equal("1m 30s", Resolve("duration"));
        Assert.Null(Resolve("nonsense"));
    }
}
