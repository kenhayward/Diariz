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
        Assert.Contains("## Transcript\n\n| Time | Speaker | Text |\n| --- | --- | --- |", md);
        Assert.Contains("| 00:00 | Al\\|ce | a \\| b |", md); // pipes escaped so they don't break the table
        Assert.Contains("{: col-widths=\"13,16,71\" }", md); // column-width hint after the table
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
