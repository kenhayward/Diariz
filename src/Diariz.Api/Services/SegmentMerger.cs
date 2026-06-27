namespace Diariz.Api.Services;

/// <summary>Pure (EF-free) collapsing of consecutive same-speaker transcript segments into one, so a
/// cleaned-up transcript reads as fewer, larger blocks. Run after speakers have been reassigned.</summary>
public static class SegmentMerger
{
    public record Part(string SpeakerLabel, long StartMs, long EndMs, string Text);

    /// <summary>Merge runs of adjacent parts that share a speaker label: each original part becomes its
    /// own paragraph (joined by a blank line) so the block stays readable, and the span runs from the
    /// first start to the last end. Input must already be in display order.</summary>
    public static List<Part> Merge(IReadOnlyList<Part> ordered)
    {
        var result = new List<Part>();
        foreach (var p in ordered)
        {
            if (result.Count > 0 && result[^1].SpeakerLabel == p.SpeakerLabel)
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
