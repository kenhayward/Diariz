namespace Diariz.Api.Services;

/// <summary>One segment of a source transcript being merged. <see cref="DisplayName"/> is the speaker's
/// shown name (a rename/identification, else the raw label); <see cref="ProfileId"/>/<see cref="IdentifiedAuto"/>/
/// <see cref="IsMultiSpeaker"/> carry the speaker's identity state so it survives the merge.</summary>
public record MergeSegmentInput(
    string SpeakerLabel, string DisplayName, long StartMs, long EndMs, string Text,
    Guid? ProfileId = null, bool IdentifiedAuto = false, bool IsMultiSpeaker = false);

/// <summary>One source recording's transcript in a merge. <see cref="Index"/> is its 0-based position in
/// chronological order; <see cref="DurationMs"/> is used to offset later sources' timestamps.</summary>
public record MergeSourceInput(int Index, long DurationMs, IReadOnlyList<MergeSegmentInput> Segments);

public record MergedSegmentOutput(string SpeakerLabel, string DisplayName, long StartMs, long EndMs, string Text, int Ordinal);
public record MergedSpeaker(string Label, string DisplayName, Guid? ProfileId, bool IdentifiedAuto, bool IsMultiSpeaker);
public record TranscriptMergeOutput(IReadOnlyList<MergedSegmentOutput> Segments, IReadOnlyList<MergedSpeaker> Speakers);

/// <summary>Pure transcript-concatenation logic for merging several recordings into one. Sources are laid
/// end-to-end in chronological order: each later source's timestamps are shifted by the cumulative duration
/// of the sources before it, and ordinals are renumbered across the whole sequence. Speaker labels are
/// namespaced per source (<c>S1-…</c>, <c>S2-…</c>) so identical diarization labels from different sources
/// (e.g. each starts at <c>SPEAKER_00</c>) stay distinct — the user can re-identify/merge speakers after.
/// Mirrors <see cref="SegmentMerger"/> in being EF-free and fully unit-testable.</summary>
public static class TranscriptMerger
{
    public static TranscriptMergeOutput Merge(IReadOnlyList<MergeSourceInput> sources)
    {
        var segments = new List<MergedSegmentOutput>();
        var speakers = new Dictionary<string, MergedSpeaker>(); // namespaced label -> speaker (first wins)
        long offset = 0;
        var ordinal = 0;

        foreach (var src in sources.OrderBy(s => s.Index))
        {
            foreach (var seg in src.Segments)
            {
                var label = $"S{src.Index + 1}-{seg.SpeakerLabel}";
                segments.Add(new MergedSegmentOutput(
                    label, seg.DisplayName, seg.StartMs + offset, seg.EndMs + offset, seg.Text, ordinal++));
                speakers.TryAdd(label, new MergedSpeaker(
                    label, seg.DisplayName, seg.ProfileId, seg.IdentifiedAuto, seg.IsMultiSpeaker));
            }
            offset += src.DurationMs;
        }

        return new TranscriptMergeOutput(segments, speakers.Values.ToList());
    }
}
