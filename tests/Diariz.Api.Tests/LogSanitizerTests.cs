using Diariz.Api.Services;
using Xunit;

namespace Diariz.Api.Tests;

public class LogSanitizerTests
{
    [Fact]
    public void Clean_StripsCarriageReturnsAndNewlines_SoAValueCannotForgeALogLine()
    {
        // A CRLF + a fake "log line" must not survive as separate lines.
        Assert.Equal("evil INJECTED WARNING fake", LogSanitizer.Clean("evil\r\nINJECTED WARNING fake"));
    }

    [Fact]
    public void Clean_ReplacesTabsAndOtherControlCharsWithASpace()
    {
        Assert.Equal("a b", LogSanitizer.Clean("a\tb"));
        Assert.Equal("a b", LogSanitizer.Clean("ab")); // bell control char
    }

    [Fact]
    public void Clean_CollapsesWhitespaceRunsAndTrims()
    {
        Assert.Equal("trim me", LogSanitizer.Clean("  trim\r\n\tme  "));
    }

    [Fact]
    public void Clean_LeavesOrdinaryTextUntouched()
    {
        Assert.Equal("user@example.com", LogSanitizer.Clean("user@example.com"));
        Assert.Equal("access_denied", LogSanitizer.Clean("access_denied"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Clean_ReturnsEmptyForNullOrEmpty(string? input)
    {
        Assert.Equal("", LogSanitizer.Clean(input));
    }
}
