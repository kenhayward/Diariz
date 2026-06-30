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
    public void FormatHits_UsesStandardFormat_WithDeepLink()
    {
        var recId = Guid.NewGuid();
        var hits = new List<TranscriptHit>
        {
            new(recId, "Standup", When1, 252_000, "Alice", "We should cut the budget.", 0.9),
        };
        var text = ToolFormat.FormatHits(hits);
        Assert.Contains("When: 2026-06-26 13:25 (at 04:12)", text);
        Assert.Contains("Who: Alice", text);
        Assert.Contains("What: \"We should cut the budget.\"", text);
        Assert.Contains($"[Standup @ 04:12](/recordings/{recId}?t=252000)", text);
    }

    [Fact]
    public void RecordingHref_OptionalTime()
    {
        var id = Guid.NewGuid();
        Assert.Equal($"/recordings/{id}", ToolFormat.RecordingHref(id));
        Assert.Equal($"/recordings/{id}?t=5000", ToolFormat.RecordingHref(id, 5000));
    }

    [Fact]
    public void FormatHits_Empty_SaysSo() =>
        Assert.Equal("No matching transcript segments were found.", ToolFormat.FormatHits([]));

    [Fact]
    public void FormatRecordings_IncludesSnippetWhenPresent()
    {
        var recId = Guid.NewGuid();
        var recs = new List<RecordingHit>
        {
            new(recId, "Budget Review", When1, "Microphone", 60_000, ["Alice", "Bob"], "cut the budget"),
        };
        var text = ToolFormat.FormatRecordings(recs);
        Assert.Contains("When: 2026-06-26 13:25", text);
        Assert.Contains("Name: Budget Review", text);
        Assert.Contains("Speakers: Alice, Bob", text);
        Assert.Contains("Match: \"cut the budget\"", text);
        Assert.Contains($"[Budget Review](/recordings/{recId})", text);
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

    [Theory]
    [InlineData("00:25", 25_000)]
    [InlineData("1:01:01", 3_661_000)]
    [InlineData("90", 90_000)]
    public void ParseTimeMs_ParsesClockAndSeconds(string s, long expected) =>
        Assert.Equal(expected, ToolFormat.ParseTimeMs(s));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1:2:3:4")]
    public void ParseTimeMs_RejectsJunk(string s) => Assert.Null(ToolFormat.ParseTimeMs(s));

    [Fact]
    public void ReadDate_ParsesIso_OrNull()
    {
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
            ToolFormat.ReadDate(Args("{\"from\":\"2026-01-02\"}"), "from"));
        Assert.Null(ToolFormat.ReadDate(Args("{\"from\":\"not-a-date\"}"), "from"));
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
