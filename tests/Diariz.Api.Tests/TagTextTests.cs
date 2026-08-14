using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class TagTextTests
{
    [Theory]
    [InlineData("metadata", "metadata")]
    [InlineData("  metadata  ", "metadata")]
    [InlineData("Data Collection", "Data-Collection")]
    [InlineData("budget planning 2026", "budget-planning-2026")]
    [InlineData("spaced\tout\nword", "spaced-out-word")]
    [InlineData("many   spaces", "many-spaces")]
    [InlineData("-leading", "leading")]
    [InlineData("trailing-", "trailing")]
    [InlineData("--both--", "both")]
    public void Normalize_CollapsesWhitespaceToHyphens_AndTrims(string raw, string expected) =>
        Assert.Equal(expected, TagText.Normalize(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-")]
    [InlineData("---")]
    public void Normalize_ReturnsNull_WhenThereIsNoUsableText(string? raw) =>
        Assert.Null(TagText.Normalize(raw));

    [Fact]
    public void Normalize_PreservesCase()
    {
        Assert.Equal("Roadmap", TagText.Normalize("Roadmap"));
        Assert.Equal("iOS", TagText.Normalize("iOS"));
    }

    [Fact]
    public void Normalize_TruncatesToTheColumnLength()
    {
        var result = TagText.Normalize(new string('x', 100));
        Assert.Equal(64, result!.Length);
    }
}
