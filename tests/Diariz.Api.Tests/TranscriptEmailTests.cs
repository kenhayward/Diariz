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
    public void BuildHtml_EscapesHtmlInContent()
    {
        var html = TranscriptEmail.BuildHtml("X", null, [Seg("<b>evil</b>", 0, "a & b <script>")]);
        Assert.DoesNotContain("<b>evil</b>", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;b&gt;evil&lt;/b&gt;", html);
        Assert.Contains("a &amp; b &lt;script&gt;", html);
    }
}
