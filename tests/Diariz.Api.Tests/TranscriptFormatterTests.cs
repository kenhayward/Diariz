using Diariz.Api.Contracts;
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class TranscriptFormatterTests
{
    private static readonly IReadOnlyList<SegmentDto> Segments =
    [
        new SegmentDto(Guid.NewGuid(), "SPEAKER_00", "Alice", 852, 3896, "So here's the thing."),
        new SegmentDto(Guid.NewGuid(), "SPEAKER_01", "Bob", 64000, 66500, "Right, on the API."),
    ];

    private static readonly IReadOnlyList<RecordingActionDto> Actions =
    [
        new RecordingActionDto(Guid.NewGuid(), "Send the report", "Bob", "Friday", 0),
        new RecordingActionDto(Guid.NewGuid(), "Book the room", "", "", 1),
    ];

    [Fact]
    public void ToText_HasHeadings_Summary_AndOneParagraphPerSegment()
    {
        var text = TranscriptFormatter.ToText("Team Sync", "We discussed the API.", Segments);

        Assert.Contains("Transcript Name\nTeam Sync", text);
        Assert.Contains("Summary\nWe discussed the API.", text);
        Assert.Contains("Transcript\n", text);
        Assert.Contains("[00:00] Alice\nSo here's the thing.", text);
        Assert.Contains("[01:04] Bob\nRight, on the API.", text);
    }

    [Fact]
    public void ToText_CollapsesBlankLines_NoDoubleLineBreaks()
    {
        var text = TranscriptFormatter.ToText("Team Sync", "We discussed the API.", Segments);

        Assert.DoesNotContain("\n\n", text); // runs of blank lines collapsed to one
    }

    [Fact]
    public void ToText_NoSummary_ShowsEmDash()
    {
        Assert.Contains("Summary\n—", TranscriptFormatter.ToText("x", null, Segments));
    }

    [Fact]
    public void ToMarkdown_HasHeadingsAndATable_EscapingPipes()
    {
        var segs = new List<SegmentDto> { new(Guid.NewGuid(), "S", "Al|ce", 0, 1000, "a | b") };
        var md = TranscriptFormatter.ToMarkdown("Team Sync", "The summary.", segs);

        Assert.Contains("# Team Sync", md);
        Assert.Contains("## Summary\n\nThe summary.", md);
        // 13/16/71% widths carried by proportional dash counts in the separator row (no stray IAL line).
        var sep = "| " + new string('-', 13) + " | " + new string('-', 16) + " | " + new string('-', 71) + " |";
        Assert.Contains("## Transcript\n\n| Time | Speaker | Text |\n" + sep + "\n", md);
        Assert.Contains("| 00:00 | Al\\|ce | a \\| b |", md); // pipes escaped so they don't break the table
        Assert.DoesNotContain("col-widths", md); // no attribute-list line that editors render as a stray paragraph
    }

    [Fact]
    public void ToMarkdown_NoSummary_ShowsPlaceholder()
    {
        Assert.Contains("## Summary\n\n_(none)_", TranscriptFormatter.ToMarkdown("x", "", Segments));
    }

    [Fact]
    public void ToRtf_IsWellFormed_WithHeadingsAndTableCells_AndEscaping()
    {
        var segs = new List<SegmentDto> { new(Guid.NewGuid(), "S", "A{B}", 0, 1000, "café") };
        var rtf = TranscriptFormatter.ToRtf("Team Sync", "Sum", segs);

        Assert.StartsWith(@"{\rtf1", rtf);
        Assert.EndsWith("}", rtf);
        Assert.Contains(@"{\b Transcript Name}\line Team Sync", rtf);
        Assert.Contains(@"{\b Summary}\line Sum\par\par", rtf); // extra para mark after the summary
        Assert.Contains(@"\trhdr", rtf); // first table row marked as a repeating header
        Assert.Contains(@"\cellx1248\cellx2784\cellx9600", rtf); // 13/16/71% column widths
        Assert.Contains(@"\cell", rtf); // table cells present
        Assert.Contains(@"A\{B\}", rtf); // braces escaped
        Assert.Contains(@"caf\u233?", rtf); // é -> unicode escape
    }

    [Fact]
    public void ToPlainText_OneLinePerSegment_ForChatContext()
    {
        Assert.Equal(
            "[00:00] Alice: So here's the thing.\n[01:04] Bob: Right, on the API.\n",
            TranscriptFormatter.ToPlainText(Segments));
    }

    [Fact]
    public void ToText_WithActions_InsertsActionsAfterSummary_BeforeTranscript()
    {
        var text = TranscriptFormatter.ToText("Team Sync", "We discussed the API.", Segments, Actions);

        Assert.Contains("Summary\nWe discussed the API.", text);
        Assert.Contains("Actions\n", text);
        Assert.Contains("Send the report\nActor: Bob   Deadline: Friday", text);
        Assert.Contains("Book the room\nActor: —   Deadline: —", text); // empty fields → em-dash
        // Actions come before the Transcript heading.
        Assert.True(text.IndexOf("Actions\n", StringComparison.Ordinal) < text.IndexOf("Transcript\n", StringComparison.Ordinal));
    }

    [Fact]
    public void ToText_NoActions_OmitsTheActionsHeading()
    {
        Assert.DoesNotContain("Actions", TranscriptFormatter.ToText("x", "s", Segments));
        Assert.DoesNotContain("Actions", TranscriptFormatter.ToText("x", "s", Segments, []));
    }

    [Fact]
    public void ToMarkdown_WithActions_HasAnActionsTable_BeforeTheTranscript()
    {
        var md = TranscriptFormatter.ToMarkdown("Team Sync", "The summary.", Segments, Actions);

        Assert.Contains("## Actions\n\n| Action | Actor | Deadline |", md);
        Assert.Contains("| Send the report | Bob | Friday |", md);
        Assert.True(md.IndexOf("## Actions", StringComparison.Ordinal) < md.IndexOf("## Transcript", StringComparison.Ordinal));
    }

    [Fact]
    public void ToMarkdown_NoActions_OmitsTheActionsTable()
    {
        Assert.DoesNotContain("## Actions", TranscriptFormatter.ToMarkdown("x", "s", Segments));
    }

    [Fact]
    public void ToRtf_WithActions_HasABoldActionsTable_BeforeTheTranscript()
    {
        var rtf = TranscriptFormatter.ToRtf("Team Sync", "Sum", Segments, Actions);

        Assert.Contains(@"{\b Actions}\par", rtf);
        Assert.Contains(@"{\b Action}\cell {\b Actor}\cell {\b Deadline}", rtf);
        Assert.Contains(@"\cellx5760\cellx7488\cellx9600", rtf); // 60/18/22% action columns
        Assert.True(rtf.IndexOf(@"{\b Actions}", StringComparison.Ordinal) < rtf.IndexOf(@"{\b Transcript}", StringComparison.Ordinal));
    }

    [Fact]
    public void ToRtf_NoActions_OmitsTheActionsTable()
    {
        Assert.DoesNotContain(@"{\b Actions}", TranscriptFormatter.ToRtf("x", "s", Segments));
    }

    [Fact]
    public void ActionsForChat_FormatsActorAndDeadline_AndIsEmptyWithoutActions()
    {
        Assert.Equal("", TranscriptFormatter.ActionsForChat([]));

        var text = TranscriptFormatter.ActionsForChat(Actions);
        Assert.StartsWith("Actions:\n", text);
        Assert.Contains("- Send the report (Actor: Bob; Deadline: Friday)", text);
        Assert.Contains("- Book the room\n", text); // no actor/deadline → no parenthetical
    }

    [Fact]
    public void ToSrt_ProducesNumberedCuesWithMillisecondTimestamps()
    {
        var srt = TranscriptFormatter.ToSrt(Segments);

        Assert.Equal(
            "1\n" +
            "00:00:00,852 --> 00:00:03,896\n" +
            "Alice: So here's the thing.\n" +
            "\n" +
            "2\n" +
            "00:01:04,000 --> 00:01:06,500\n" +
            "Bob: Right, on the API.\n",
            srt);
    }
}
