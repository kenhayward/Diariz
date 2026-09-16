using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class ActionMergeTests
{
    [Theory]
    [InlineData("Book the room.", "book the room")]
    [InlineData("  Send   the\tdeck!! ", "send the deck")]
    [InlineData("Follow-up with Grace", "follow up with grace")]
    public void Normalize_IgnoresCasePunctuationAndSpacing(string input, string expected) =>
        Assert.Equal(expected, ActionMerge.Normalize(input));

    [Fact]
    public void WithoutDuplicates_DropsItemsMatchingExisting_AndRepeatsWithinTheExtraction()
    {
        var extracted = new[]
        {
            new ExtractedAction("Book the room.", "Grace", ""),
            new ExtractedAction("Chase the invoice", "Ada", "Friday"),
            new ExtractedAction("chase the invoice", "", ""),
        };

        var kept = ActionMerge.WithoutDuplicates(extracted, ["book the ROOM"]);

        Assert.Equal(["Chase the invoice"], kept.Select(a => a.Text));
        Assert.Equal("Ada", kept[0].Actor); // the first occurrence wins, with its fields
    }
}
