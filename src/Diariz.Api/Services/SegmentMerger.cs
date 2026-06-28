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
    public record Part(string SpeakerKey, string SpeakerLabel, long StartMs, long EndMs, string Text);

    /// <summary>Merge runs of adjacent parts that share a speaker <em>key</em>: each original part becomes
    /// its own paragraph (joined by a blank line) so the block stays readable, the span runs from the first
    /// start to the last end, and the first part's label is kept. Input must already be in display order.</summary>
    public static List<Part> Merge(IReadOnlyList<Part> ordered)
    {
        var result = new List<Part>();
        foreach (var p in ordered)
        {
            if (result.Count > 0 && result[^1].SpeakerKey == p.SpeakerKey)
            {
                var prev = result[^1];
                var text = string.IsNullOrWhiteSpace(prev.Text) ? p.Text
                    : string.IsNullOrWhiteSpace(p.Text) ? prev.Text
                    : $"{prev.Text}\n\n{p.Text}"; // paragraph break between merged sections
                result[^1] = prev with { EndMs = p.EndMs, Text = text };
            }
            else
            {
                result.Add(p);
            }
        }
        return result;
    }
}
