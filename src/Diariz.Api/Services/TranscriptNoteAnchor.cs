namespace Diariz.Api.Services;

/// <summary>Pure rule for where a note-taker's note attaches within a transcript, shared by the transcript
/// view (weaving notes in) and the merge (keeping same-speaker text either side of a note separate). A note is
/// anchored <em>after</em> the last segment that had started when it was written (greatest <c>StartMs</c> ≤ the
/// note's captured time); a note written before the first segment anchors at the very top (index -1).</summary>
public static class TranscriptNoteAnchor
{
    /// <summary>Index of the segment a note captured at <paramref name="capturedMs"/> attaches after, or -1
    /// for the very top. Segment starts are expected in display order.</summary>
    public static int AnchorIndex(IReadOnlyList<long> segmentStartMs, long capturedMs)
    {
        var idx = -1;
        for (var i = 0; i < segmentStartMs.Count; i++)
            if (segmentStartMs[i] <= capturedMs) idx = i;
        return idx;
    }

    /// <summary>Segment indices that must NOT merge into their previous segment because a note sits between
    /// them: for each note anchored after a segment i that has a following segment, that following index i+1.</summary>
    public static HashSet<int> BreakBeforeIndices(IReadOnlyList<long> segmentStartMs, IEnumerable<long> noteCapturedMs)
    {
        var breaks = new HashSet<int>();
        foreach (var ms in noteCapturedMs)
        {
            var i = AnchorIndex(segmentStartMs, ms);
            if (i >= 0 && i + 1 < segmentStartMs.Count) breaks.Add(i + 1);
        }
        return breaks;
    }
}
