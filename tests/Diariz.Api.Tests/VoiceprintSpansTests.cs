using Diariz.Api.Contracts;
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class VoiceprintSpansTests
{
    private static readonly IReadOnlyList<VoiceprintSpan> Two = [new(1000, 2000), new(5000, 6500)];

    [Fact]
    public void Serialize_ThenParse_RoundTrips() =>
        Assert.Equal(Two, VoiceprintSpans.Parse(VoiceprintSpans.Serialize(Two)));

    [Fact]
    public void Serialize_NullOrEmpty_IsNull()
    {
        // Null is "the whole speaker" - the state every sample enrolled before selection existed is in, so
        // nothing needed backfilling. An empty array would mean "train on nothing".
        Assert.Null(VoiceprintSpans.Serialize(null));
        Assert.Null(VoiceprintSpans.Serialize([]));
    }

    [Fact]
    public void Parse_NullOrGarbage_IsEmpty()
    {
        Assert.Empty(VoiceprintSpans.Parse(null));
        Assert.Empty(VoiceprintSpans.Parse("not json"));
    }

    [Fact]
    public void TotalMs_SumsTheSpans() => Assert.Equal(2500, VoiceprintSpans.TotalMs(Two));

    [Fact]
    public void TotalMs_DoesNotDoubleCountOverlaps() =>
        Assert.Equal(2000, VoiceprintSpans.TotalMs([new(1000, 2500), new(2000, 3000)]));

    [Fact]
    public void Coverage_WithNoSpans_IsIncluded() =>
        // No spans means the whole speaker, so every segment is in.
        Assert.Equal(SpanCoverage.Included, VoiceprintSpans.Coverage(1000, 2000, []));

    [Theory]
    [InlineData(1000, 2000, SpanCoverage.Included)]  // exactly a span
    [InlineData(1200, 1800, SpanCoverage.Included)]  // inside a span
    [InlineData(3000, 4000, SpanCoverage.Excluded)]  // between the spans
    [InlineData(1500, 2500, SpanCoverage.Partial)]   // straddles the end of one
    [InlineData(500, 1500, SpanCoverage.Partial)]    // straddles the start of one
    public void Coverage_ClassifiesASegmentAgainstTheSpans(long start, long end, SpanCoverage expected) =>
        Assert.Equal(expected, VoiceprintSpans.Coverage(start, end, Two));

    [Fact]
    public void Coverage_SpanningTwoTouchingSpans_IsIncluded()
    {
        // Two spans that meet cover the segment between them completely, and reporting Partial would show a
        // permanently half-ticked row that no amount of clicking could resolve. The ordered walk carries the
        // cursor across the join on its own - this pins the behaviour, not the merging step.
        Assert.Equal(SpanCoverage.Included,
            VoiceprintSpans.Coverage(1500, 2500, [new(1000, 2000), new(2000, 3000)]));
    }

    [Fact]
    public void Coverage_SpanningTwoOverlappingSpans_IsIncluded() =>
        // Overlapping picks would send the walk backwards without the merge, leaving the cursor short of
        // the segment's end and reporting Partial for a fully-selected row.
        Assert.Equal(SpanCoverage.Included,
            VoiceprintSpans.Coverage(1500, 3500, [new(2000, 4000), new(1000, 2500)]));

    [Fact]
    public void Coverage_WithAGapInsideTheSegment_IsPartial() =>
        Assert.Equal(SpanCoverage.Partial,
            VoiceprintSpans.Coverage(1000, 3000, [new(1000, 1500), new(2500, 3000)]));

    [Fact]
    public void FromSegments_MergesOverlappingAndTouchingSpans()
    {
        // The user ticks segments; what gets stored is the audio those segments occupy. Adjacent picks must
        // collapse, or a 40-minute selection becomes hundreds of one-line spans in the job payload.
        Assert.Equal([new VoiceprintSpan(1000, 3000), new VoiceprintSpan(5000, 6000)],
            VoiceprintSpans.FromSegments([(2000, 3000), (1000, 2000), (5000, 6000)]));
    }

    [Fact]
    public void FromSegments_WithNothingSelected_IsEmpty() =>
        Assert.Empty(VoiceprintSpans.FromSegments([]));

    [Fact]
    public void FromSegments_DropsZeroAndNegativeLengthSpans() =>
        Assert.Equal([new VoiceprintSpan(1000, 2000)],
            VoiceprintSpans.FromSegments([(1000, 2000), (3000, 3000), (5000, 4000)]));

    [Fact]
    public void FromSegments_ThenCoverage_TicksBackEverySelectedSegment()
    {
        // The round trip the UI depends on: ticking a set of segments and re-reading it must show exactly
        // those ticked, or a selection would appear to change itself the moment it was saved.
        (long, long)[] picked = [(1000, 2000), (2000, 3000), (7000, 8000)];
        var spans = VoiceprintSpans.FromSegments(picked);

        Assert.All(picked, p => Assert.Equal(SpanCoverage.Included, VoiceprintSpans.Coverage(p.Item1, p.Item2, spans)));
        Assert.Equal(SpanCoverage.Excluded, VoiceprintSpans.Coverage(4000, 5000, spans));
    }
}
