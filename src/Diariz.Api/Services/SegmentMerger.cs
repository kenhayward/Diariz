namespace Diariz.Api.Services;

/// <summary>Pure (EF-free) collapsing of consecutive same-speaker transcript segments into one, so a
/// cleaned-up transcript reads as fewer, larger blocks. Run after speakers have been reassigned.</summary>
public static class SegmentMerger
{
    /// <param name="SpeakerKey">The effective speaker identity used to group adjacent parts (e.g. the
    /// assigned profile or display name) — two different diarization labels reassigned to the same person
    /// share a key and so merge.</param>
    /// <param name="SpeakerLabel">The diarization label stored on the resulting segment (the first label in
    /// a merged run is kept, so the block still resolves to that speaker).</param>
    /// <param name="BreakBefore">When true, this part always starts a new block even if its speaker key
    /// matches the previous one - used to stop a note-taker's note being swallowed by a same-speaker merge
    /// (the note sits between the two parts, so they must stay separate).</param>
    public record Part(string SpeakerKey, string SpeakerLabel, long StartMs, long EndMs, string Text, bool BreakBefore = false);

    /// <summary>Merge runs of adjacent parts that share a speaker <em>key</em>: the parts are joined with a
    /// single line break (never a blank line - see <see cref="TranscriptText"/>), the span runs from the first
    /// start to the last end, and the first part's label is kept. A part flagged <see cref="Part.BreakBefore"/>
    /// never merges into the previous block. Input must already be in display order.</summary>
    public static List<Part> Merge(IReadOnlyList<Part> ordered)
    {
        var result = new List<Part>();
        foreach (var p in ordered)
        {
            if (result.Count > 0 && result[^1].SpeakerKey == p.SpeakerKey && !p.BreakBefore)
            {
                var prev = result[^1];
                // One line break between merged sections, and collapse any blank lines the parts carried.
                var text = TranscriptText.Normalize($"{prev.Text}\n{p.Text}");
                result[^1] = prev with { EndMs = p.EndMs, Text = text };
            }
            else
            {
                // BreakBefore has done its job (this part starts a new block); clear it on the output so a
                // merged part compares cleanly and carries no residual flag.
                result.Add(p with { Text = TranscriptText.Normalize(p.Text), BreakBefore = false });
            }
        }
        return result;
    }
}
