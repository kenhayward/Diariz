using Diariz.Api.Services;

namespace Diariz.Api.Tests;

/// <summary>Where a note attaches in a transcript (after the segment being spoken when it was written) and
/// which segment boundaries a note therefore blocks from merging.</summary>
public class TranscriptNoteAnchorTests
{
    private static readonly long[] Starts = [0, 1000, 2000, 3000];

    [Fact]
    public void AnchorIndex_picks_the_last_segment_started_at_or_before_the_note()
    {
        Assert.Equal(0, TranscriptNoteAnchor.AnchorIndex(Starts, 500));   // during segment 0
        Assert.Equal(1, TranscriptNoteAnchor.AnchorIndex(Starts, 1000));  // exactly at segment 1's start
        Assert.Equal(3, TranscriptNoteAnchor.AnchorIndex(Starts, 9999));  // after the last segment
    }

    [Fact]
    public void AnchorIndex_is_minus_one_before_the_first_segment()
    {
        Assert.Equal(-1, TranscriptNoteAnchor.AnchorIndex([1000, 2000], 500));
    }

    [Fact]
    public void BreakBeforeIndices_blocks_the_segment_after_each_note_anchor()
    {
        // Notes during segment 0 (→ anchor 0, block 1) and during segment 2 (→ anchor 2, block 3).
        var breaks = TranscriptNoteAnchor.BreakBeforeIndices(Starts, [500, 2500]);
        Assert.Equal(new HashSet<int> { 1, 3 }, breaks);
    }

    [Fact]
    public void BreakBeforeIndices_ignores_a_note_after_the_last_segment_and_before_the_first()
    {
        var breaks = TranscriptNoteAnchor.BreakBeforeIndices(Starts, [9999, -5]);
        Assert.Empty(breaks); // anchor 3 has no following segment; anchor -1 has nothing before it
    }
}
