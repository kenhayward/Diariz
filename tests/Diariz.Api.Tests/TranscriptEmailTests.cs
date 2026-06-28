using Diariz.Api.Contracts;
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class TranscriptEmailTests
{
    private static SegmentDto Seg(string display, long startMs, string text) =>
        new(Guid.NewGuid(), "SPEAKER_00", display, startMs, startMs + 1000, text);

    [Fact]
    public void Subject_PrefixesWithName() =>
        Assert.Equal("Transcript for Weekly Sync", TranscriptEmail.Subject("Weekly Sync"));

    [Fact]
    public void BuildHtml_HasBoldHeadings_Table_AndFooter()
    {
        var html = TranscriptEmail.BuildHtml("Weekly Sync", "Key decisions.",
        [
            Seg("Alice", 0, "Hello there"),
            Seg("Bob", 65_000, "Hi"),
        ]);

        Assert.Contains("<strong>Transcript Name</strong>", html);
        Assert.Contains("Weekly Sync", html);
        Assert.Contains("<strong>Summary</strong>", html);
        Assert.Contains("Key decisions.", html);
        Assert.Contains("<strong>Transcript</strong>", html);
        Assert.Contains("<table", html);
        Assert.Contains("<td>00:00</td><td>Alice</td><td>Hello there</td>", html);
        Assert.Contains("<td>01:05</td><td>Bob</td><td>Hi</td>", html); // 65s → 01:05
        Assert.Contains("Sent from Diariz", html);
    }

    [Fact]
    public void BuildHtml_NoSummary_ShowsDash()
    {
        var html = TranscriptEmail.BuildHtml("X", null, [Seg("A", 0, "hi")]);
        Assert.Contains("<strong>Summary</strong><br>&mdash;", html);
    }

    [Fact]
    public void BuildHtml_WithActions_InsertsAnActionsTable_AfterTheSummary()
    {
        var html = TranscriptEmail.BuildHtml("X", "Sum", [Seg("A", 0, "hi")],
        [
            new RecordingActionDto(Guid.NewGuid(), "Send the report", "Bob", "Friday", 0),
        ]);

        Assert.Contains("<strong>Actions</strong>", html);
        Assert.Contains("<th align=\"left\">Action</th><th align=\"left\">Actor</th><th align=\"left\">Deadline</th>", html);
        Assert.Contains("<td>Send the report</td><td>Bob</td><td>Friday</td>", html);
        // Actions table comes before the Transcript heading.
        Assert.True(html.IndexOf("<strong>Actions</strong>", StringComparison.Ordinal)
                    < html.IndexOf("<strong>Transcript</strong>", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildHtml_NoActions_OmitsTheActionsTable()
    {
        var html = TranscriptEmail.BuildHtml("X", "Sum", [Seg("A", 0, "hi")]);
        Assert.DoesNotContain("<strong>Actions</strong>", html);
    }

    [Fact]
    public void BuildHtml_RendersParagraphBreaksFromMergedText()
    {
        var html = TranscriptEmail.BuildHtml("X", null, [Seg("A", 0, "para one\n\npara two")]);
        Assert.Contains("para one<br><br>para two", html);
    }

    [Fact]
    public void BuildHtml_EscapesHtmlInContent()
    {
        var html = TranscriptEmail.BuildHtml("X", null, [Seg("<b>evil</b>", 0, "a & b <script>")]);
        Assert.DoesNotContain("<b>evil</b>", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;b&gt;evil&lt;/b&gt;", html);
        Assert.Contains("a &amp; b &lt;script&gt;", html);
    }
}
