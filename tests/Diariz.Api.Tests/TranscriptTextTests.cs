using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class TranscriptTextTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("Hello", "Hello")]
    [InlineData("  Hello  ", "Hello")]
    [InlineData("a\nb", "a\nb")] // a single line break between lines is kept
    public void Normalize_TrimsAndKeepsSingleBreaks(string? input, string expected) =>
        Assert.Equal(expected, TranscriptText.Normalize(input));

    [Fact]
    public void Normalize_CollapsesRepeatedLineFeeds_ToOne()
    {
        Assert.Equal("Hello\nthere", TranscriptText.Normalize("Hello\n\nthere"));
        Assert.Equal("Hello\nthere", TranscriptText.Normalize("Hello\n\n\n\nthere"));
    }

    [Fact]
    public void Normalize_DropsBlankLines_IncludingWhitespaceOnly()
    {
        Assert.Equal("line1\nline2", TranscriptText.Normalize("line1\n \nline2"));
        Assert.Equal("line1\nline2", TranscriptText.Normalize("line1\n\t\n   \nline2"));
    }

    [Fact]
    public void Normalize_StripsLeadingAndTrailingBlankLines()
    {
        Assert.Equal("Hello", TranscriptText.Normalize("\n\nHello\n\n"));
    }

    [Fact]
    public void Normalize_NormalizesCarriageReturns()
    {
        Assert.Equal("Hello\nthere", TranscriptText.Normalize("Hello\r\n\r\nthere"));
        Assert.Equal("Hello\nthere", TranscriptText.Normalize("Hello\r\rthere"));
    }
}
