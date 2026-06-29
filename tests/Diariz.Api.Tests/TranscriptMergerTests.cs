using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class TranscriptMergerTests
{
    private static MergeSegmentInput Seg(string label, long start, long end, string text, string? display = null) =>
        new(label, display ?? label, start, end, text);

    [Fact]
    public void Merge_LaysSourcesEndToEnd_OffsettingTimestampsByCumulativeDuration()
    {
        var sources = new List<MergeSourceInput>
        {
            new(0, DurationMs: 1000, Segments: [Seg("SPEAKER_00", 0, 1000, "Hello")]),
            new(1, DurationMs: 2000, Segments: [Seg("SPEAKER_00", 0, 800, "World")]),
        };

        var result = TranscriptMerger.Merge(sources);

        Assert.Equal(2, result.Segments.Count);
        // First source unshifted; second shifted by the first's 1000 ms duration.
        Assert.Equal((0, 1000, 0), (result.Segments[0].StartMs, result.Segments[0].EndMs, result.Segments[0].Ordinal));
        Assert.Equal((1000, 1800, 1), (result.Segments[1].StartMs, result.Segments[1].EndMs, result.Segments[1].Ordinal));
        Assert.Equal(["Hello", "World"], result.Segments.Select(s => s.Text));
    }

    [Fact]
    public void Merge_NamespacesSpeakerLabelsPerSource_SoIdenticalLabelsStayDistinct()
    {
        var sources = new List<MergeSourceInput>
        {
            new(0, 1000, [Seg("SPEAKER_00", 0, 500, "a", display: "Alice")]),
            new(1, 1000, [Seg("SPEAKER_00", 0, 500, "b", display: "Bob")]), // same raw label, different person
        };

        var result = TranscriptMerger.Merge(sources);

        Assert.Equal(["S1-SPEAKER_00", "S2-SPEAKER_00"], result.Segments.Select(s => s.SpeakerLabel));
        Assert.Equal(
            [("S1-SPEAKER_00", "Alice"), ("S2-SPEAKER_00", "Bob")],
            result.Speakers.Select(sp => (sp.Label, sp.DisplayName)));
    }

    [Fact]
    public void Merge_DedupesSpeakersWithinASource_KeepingTheFirstDisplayName()
    {
        var sources = new List<MergeSourceInput>
        {
            new(0, 1000, [Seg("SPEAKER_00", 0, 1, "x", "Alice"), Seg("SPEAKER_00", 1, 2, "y", "Alice")]),
        };

        var result = TranscriptMerger.Merge(sources);

        var sp = Assert.Single(result.Speakers);
        Assert.Equal(("S1-SPEAKER_00", "Alice"), (sp.Label, sp.DisplayName));
    }
}
