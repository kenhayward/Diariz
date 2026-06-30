using System.Text.Json;
using Diariz.Api.Services;
using Diariz.Api.Tools;

namespace Diariz.Api.Tests;

public class ToolFormatTests
{
    private static readonly DateTimeOffset When1 = new(2026, 6, 26, 13, 25, 0, TimeSpan.Zero);

    [Fact]
    public void When_CombinesDateAndOffset()
    {
        Assert.Equal("2026-06-26 13:25 (at 04:12)", ToolFormat.When(When1, 252_000));
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(65_000, "01:05")]
    [InlineData(3_661_000, "1:01:01")]
    public void Offset_Formats(long ms, string expected) => Assert.Equal(expected, ToolFormat.Offset(ms));

    [Fact]
    public void Snippet_CollapsesWhitespaceAndTruncates()
    {
        Assert.Equal("a b c", ToolFormat.Snippet("  a   b\n c  "));
        Assert.Equal("ab…", ToolFormat.Snippet("abcdef", 2));
    }

    [Fact]
    public void FormatHits_UsesStandardFormat()
    {
        var hits = new List<TranscriptHit>
        {
            new(Guid.NewGuid(), "Standup", When1, 252_000, "Alice", "We should cut the budget.", 0.9),
        };
        var text = ToolFormat.FormatHits(hits);
        Assert.Contains("When: 2026-06-26 13:25 (at 04:12)", text);
        Assert.Contains("Who: Alice", text);
        Assert.Contains("What: \"We should cut the budget.\"", text);
        Assert.Contains("[recording: Standup]", text);
    }

    [Fact]
    public void FormatHits_Empty_SaysSo() =>
        Assert.Equal("No matching transcript segments were found.", ToolFormat.FormatHits([]));

    [Fact]
    public void FormatRecordings_IncludesSnippetWhenPresent()
    {
        var recs = new List<RecordingHit>
        {
            new(Guid.NewGuid(), "Budget Review", When1, "Microphone", 60_000, ["Alice", "Bob"], "cut the budget"),
        };
        var text = ToolFormat.FormatRecordings(recs);
        Assert.Contains("When: 2026-06-26 13:25", text);
        Assert.Contains("Name: Budget Review", text);
        Assert.Contains("Speakers: Alice, Bob", text);
        Assert.Contains("Match: \"cut the budget\"", text);
    }

    [Fact]
    public void ResolveScope_CurrentReturnsSelection_ElseNull()
    {
        var ctx = new ChatToolContext(Guid.NewGuid(), [Guid.NewGuid()]);
        Assert.Equal(ctx.SelectedRecordingIds, ToolFormat.ResolveScope(Args("{\"scope\":\"current\"}"), ctx));
        Assert.Null(ToolFormat.ResolveScope(Args("{\"scope\":\"all\"}"), ctx));
        Assert.Null(ToolFormat.ResolveScope(Args("{}"), ctx));
    }

    [Fact]
    public void ReadString_TrimsAndNullsBlank()
    {
        Assert.Equal("hi", ToolFormat.ReadString(Args("{\"q\":\"  hi  \"}"), "q"));
        Assert.Null(ToolFormat.ReadString(Args("{\"q\":\"  \"}"), "q"));
        Assert.Null(ToolFormat.ReadString(Args("{}"), "q"));
    }

    [Fact]
    public void ReadDate_ParsesIso_OrNull()
    {
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
            ToolFormat.ReadDate(Args("{\"from\":\"2026-01-02\"}"), "from"));
        Assert.Null(ToolFormat.ReadDate(Args("{\"from\":\"not-a-date\"}"), "from"));
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
