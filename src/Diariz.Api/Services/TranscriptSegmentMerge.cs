using Diariz.Api.Contracts;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>Collapses consecutive same-speaker segments of one transcription into single blocks, in the
/// change tracker. Shared by the on-demand "Merge rows" action and - when the recording's owner has opted
/// in (<see cref="UserSettings.AutoMergeSpeakerSegments"/>) - the worker callback, so both produce an
/// identical transcript from identical input. Mirrors <see cref="SpeakerLabeling"/>: it mutates and leaves
/// the save to the caller.</summary>
public static class TranscriptSegmentMerge
{
    /// <returns>True when segments were collapsed and the caller should save; false when nothing was
    /// adjacent to merge (or there were no segments), in which case the change tracker is untouched.</returns>
    public static async Task<bool> ApplyAsync(
        DiarizDbContext db, Guid recordingId, Guid transcriptionId, CancellationToken ct = default)
    {
        var segments = await db.Segments.Where(s => s.TranscriptionId == transcriptionId)
            .OrderBy(s => s.Ordinal).ToListAsync(ct);
        if (segments.Count == 0) return false;

        // Group by the speaker's effective identity (assigned person, else display name), not the raw
        // diarization label - so two labels reassigned to the same person merge together.
        var speakers = await db.Speakers.Where(s => s.RecordingId == recordingId)
            .ToDictionaryAsync(s => s.Label, s => s, ct);
        string KeyFor(string label)
        {
            if (!speakers.TryGetValue(label, out var sp)) return $"l:{label}";
            if (sp.PersonId is Guid pid) return $"p:{pid}";
            return string.IsNullOrEmpty(sp.DisplayName) ? $"l:{label}" : $"n:{sp.DisplayName}";
        }

        // A note or a screenshot sits between two segments; don't let a same-speaker merge swallow that
        // boundary (the note or image would jump to after the whole merged block). Flag the segment after
        // each anchor. Both kinds of capture use the same rule, so they share one break set.
        var noteTimes = await db.MeetingNotes
            .Where(n => n.RecordingId == recordingId && n.CapturedAtMs != null)
            .Select(n => n.CapturedAtMs!.Value)
            .ToListAsync(ct);
        var shotTimes = await db.MeetingScreenshots
            .Where(s => s.RecordingId == recordingId)
            .Select(s => s.CapturedAtMs)
            .ToListAsync(ct);
        var breakBefore = TranscriptNoteAnchor.BreakBeforeIndices(
            segments.Select(s => s.StartMs).ToList(), noteTimes.Concat(shotTimes));

        // A revised segment contributes no words. Merge writes EffectiveText into a fresh Original, so its
        // merged text is the user's wording while the timings describe the model's - carrying them would
        // let a later split cut at a boundary that is not present in the text being cut.
        static IReadOnlyList<SegmentWord>? WordsOf(Segment s) =>
            s.Revised is not null ? null : SegmentWords.Parse(s.WordsJson) is { Count: > 0 } w ? w : null;

        var merged = SegmentMerger.Merge(segments
            .Select((s, i) => new SegmentMerger.Part(
                KeyFor(s.SpeakerLabel), s.SpeakerLabel, s.StartMs, s.EndMs, s.EffectiveText,
                breakBefore.Contains(i), WordsOf(s)))
            .ToList());
        if (merged.Count == segments.Count) return false; // nothing adjacent to merge

        db.Segments.RemoveRange(segments);
        var ordinal = 0;
        foreach (var p in merged)
            db.Segments.Add(new Segment
            {
                Id = Guid.NewGuid(),
                TranscriptionId = transcriptionId,
                SpeakerLabel = p.SpeakerLabel,
                StartMs = p.StartMs,
                EndMs = p.EndMs,
                // Merge consolidates the displayed (effective) text; the per-segment original/revised split
                // is intentionally collapsed into a fresh Original on the merged row.
                Original = p.Text,
                WordsJson = SegmentWords.Serialize(p.Words),
                Ordinal = ordinal++
            });
        return true;
    }
}
