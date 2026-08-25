using Diariz.Domain;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>How much each diarized speaker actually says, keyed by label.
///
/// <para>Read by the minimum-speech gate in <see cref="IdentificationRules"/> and shown next to each row in
/// the Voiceprint tab. Deliberately one implementation: a gate that disagreed with the duration printed
/// beside it would be impossible to explain to whoever is looking at both.</para></summary>
public static class SpeakerSpeech
{
    /// <summary>Pure sum per label. Anything with a blank label or a non-positive length is skipped rather
    /// than counted - a reversed span would otherwise subtract from a total and could pull a speaker under
    /// the gate, which then reads as "too little speech" rather than as the bad data it is.</summary>
    public static Dictionary<string, long> FromSegments(
        IEnumerable<(string Label, long StartMs, long EndMs)> segments)
    {
        var speech = new Dictionary<string, long>();
        foreach (var (label, start, end) in segments)
        {
            if (string.IsNullOrWhiteSpace(label)) continue;
            var ms = end - start;
            if (ms <= 0) continue;
            speech[label] = speech.GetValueOrDefault(label) + ms;
        }
        return speech;
    }

    /// <summary>Speech per label in a recording's <b>current</b> (highest-version) transcription. Empty when
    /// the recording has never been transcribed.</summary>
    public static async Task<Dictionary<string, long>> ForRecordingAsync(
        DiarizDbContext db, Guid recordingId, CancellationToken ct = default)
    {
        var trId = await db.Transcriptions
            .Where(t => t.RecordingId == recordingId)
            .OrderByDescending(t => t.Version)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(ct);
        if (trId is null) return [];

        var rows = await db.Segments
            .Where(s => s.TranscriptionId == trId)
            .Select(s => new { s.SpeakerLabel, s.StartMs, s.EndMs })
            .ToListAsync(ct);

        return FromSegments(rows.Select(r => (r.SpeakerLabel, r.StartMs, r.EndMs)));
    }

    /// <summary>A speaker with no segments in the current transcription has said nothing measurable, so it
    /// reads as zero rather than throwing partway through identifying a recording.</summary>
    public static long MsFor(IReadOnlyDictionary<string, long> speech, string label) =>
        speech.TryGetValue(label, out var ms) ? ms : 0;
}
