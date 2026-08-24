using Diariz.Api.Contracts;
using Diariz.Api.Services;
using static Diariz.Api.Services.SegmentMerger;

namespace Diariz.Api.Tests;

public class SegmentMergerTests
{
    // Part is (SpeakerKey, SpeakerLabel, StartMs, EndMs, Text): runs are grouped by SpeakerKey
    // (the effective speaker identity), but the original SpeakerLabel of the first part is kept.

    [Fact]
    public void Merge_CollapsesConsecutiveSameSpeaker_JoinsText_SpansTimestamps()
    {
        var parts = new List<Part>
        {
            new("SPEAKER_00", "SPEAKER_00", 0, 1000, "Hello"),
            new("SPEAKER_00", "SPEAKER_00", 1000, 2000, "there"),
            new("SPEAKER_01", "SPEAKER_01", 2000, 3000, "Hi"),
            new("SPEAKER_00", "SPEAKER_00", 3000, 4000, "Bye"),
        };

        var merged = Merge(parts);

        Assert.Equal(3, merged.Count);
        Assert.Equal(new Part("SPEAKER_00", "SPEAKER_00", 0, 2000, "Hello\nthere"), merged[0]);
        Assert.Equal(new Part("SPEAKER_01", "SPEAKER_01", 2000, 3000, "Hi"), merged[1]);
        Assert.Equal(new Part("SPEAKER_00", "SPEAKER_00", 3000, 4000, "Bye"), merged[2]);
    }

    [Fact]
    public void Merge_DifferentLabelsSameKey_AreMerged_KeepingFirstLabel()
    {
        // Two diarization labels both reassigned to the same person (same key) must merge,
        // and the merged block keeps the first label so it still resolves to that speaker.
        var parts = new List<Part>
        {
            new("p:alice", "SPEAKER_00", 0, 1000, "Hello"),
            new("p:alice", "SPEAKER_01", 1000, 2000, "again"),
        };

        var merged = Merge(parts);

        Assert.Single(merged);
        Assert.Equal(new Part("p:alice", "SPEAKER_00", 0, 2000, "Hello\nagain"), merged[0]);
    }

    [Fact]
    public void Merge_SameLabelDifferentKey_IsNotMerged()
    {
        // Defensive: identity is the key, not the label.
        var parts = new List<Part>
        {
            new("n:Alice", "SPEAKER_00", 0, 1000, "x"),
            new("n:Bob", "SPEAKER_00", 1000, 2000, "y"),
        };

        Assert.Equal(parts, Merge(parts));
    }

    [Fact]
    public void Merge_BreakBefore_KeepsSameSpeakerPartsSeparate()
    {
        // A note sits between the two middle same-speaker parts (BreakBefore on the third): the run must not
        // collapse across it - the parts either side of the note stay separate.
        var parts = new List<Part>
        {
            new("S", "S", 0, 1000, "a"),
            new("S", "S", 1000, 2000, "b"),
            new("S", "S", 2000, 3000, "c", BreakBefore: true),
            new("S", "S", 3000, 4000, "d"),
        };

        var merged = Merge(parts);

        Assert.Equal(2, merged.Count);
        Assert.Equal(new Part("S", "S", 0, 2000, "a\nb"), merged[0]);
        Assert.Equal(new Part("S", "S", 2000, 4000, "c\nd"), merged[1]);
    }

    [Fact]
    public void Merge_AllSameSpeaker_ProducesOnePart()
    {
        var parts = new List<Part>
        {
            new("S", "S", 0, 1000, "a"),
            new("S", "S", 1000, 2000, "b"),
            new("S", "S", 2000, 3000, "c"),
        };

        var merged = Merge(parts);

        Assert.Single(merged);
        Assert.Equal(new Part("S", "S", 0, 3000, "a\nb\nc"), merged[0]);
    }

    [Fact]
    public void Merge_AllDifferentSpeakers_IsUnchanged()
    {
        var parts = new List<Part>
        {
            new("A", "A", 0, 1000, "x"),
            new("B", "B", 1000, 2000, "y"),
        };

        Assert.Equal(parts, Merge(parts));
    }

    [Fact]
    public void Merge_SkipsBlankTextWithoutBlankLine()
    {
        var parts = new List<Part>
        {
            new("S", "S", 0, 1000, "hello"),
            new("S", "S", 1000, 2000, ""),
            new("S", "S", 2000, 3000, "world"),
        };

        Assert.Equal("hello\nworld", Merge(parts).Single().Text);
    }

    [Fact]
    public void Merge_CollapsesBlankLinesWithinMergedText()
    {
        // Merged parts may themselves carry blank lines (e.g. from a hand-edited revision); the merge must
        // leave a single line break, never a blank line.
        var parts = new List<Part>
        {
            new("S", "S", 0, 1000, "a\n\nb"),
            new("S", "S", 1000, 2000, "c"),
        };

        Assert.Equal("a\nb\nc", Merge(parts).Single().Text);
    }

    [Fact]
    public void Merge_Empty_ReturnsEmpty() => Assert.Empty(Merge([]));

    /// <summary>Auto-merge runs on every transcription for users who enabled it, and it deletes and
    /// rebuilds every segment. If word timings did not survive that rebuild, merging would silently
    /// destroy splittability for exactly the users most likely to want it - and no results-based test
    /// would have noticed, because the transcript reads identically either way.</summary>
    [Fact]
    public void Merge_ConcatenatesWordsOfMergedParts()
    {
        var merged = Merge([
            new Part("k", "S0", 0, 1000, "Hello", Words: [new SegmentWord("Hello", 0, 1000)]),
            new Part("k", "S0", 1100, 2000, "world", Words: [new SegmentWord("world", 1100, 2000)]),
        ]);

        var block = Assert.Single(merged);
        Assert.Equal([new SegmentWord("Hello", 0, 1000), new SegmentWord("world", 1100, 2000)], block.Words);
    }

    [Fact]
    public void Merge_DropsWordsWhenAnyPartInTheRunHasNone()
    {
        // One unaligned part makes the block's word list an incomplete description of its own text.
        // Splitting on that would cut at a boundary that is not where the text says it is, so the whole
        // block becomes unsplittable instead - visibly, via a null.
        var merged = Merge([
            new Part("k", "S0", 0, 1000, "Hello", Words: [new SegmentWord("Hello", 0, 1000)]),
            new Part("k", "S0", 1100, 2000, "world", Words: null),
        ]);

        Assert.Null(Assert.Single(merged).Words);
    }

    [Fact]
    public void Merge_KeepsEachUnmergedPartsOwnWords()
    {
        var merged = Merge([
            new Part("a", "S0", 0, 1000, "Hello", Words: [new SegmentWord("Hello", 0, 1000)]),
            new Part("b", "S1", 1100, 2000, "world", Words: [new SegmentWord("world", 1100, 2000)]),
        ]);

        Assert.Equal(2, merged.Count);
        Assert.Equal([new SegmentWord("Hello", 0, 1000)], merged[0].Words);
        Assert.Equal([new SegmentWord("world", 1100, 2000)], merged[1].Words);
    }
}
