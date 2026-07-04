using System.Text;

namespace Diariz.Api.Services;

/// <summary>One input segment for chunking: a speaker display name, its span, and the spoken text.</summary>
public record ChunkSegment(string SpeakerDisplay, long StartMs, long EndMs, string Text);

/// <summary>A windowed retrieval chunk produced by <see cref="TranscriptChunker"/> — pure data (no EF),
/// carrying its span, the distinct speakers it covers, and the flattened "Speaker: Text" body.</summary>
public record ChunkDraft(int Ordinal, long StartMs, long EndMs, IReadOnlyList<string> Speakers, string Text);

/// <summary>
/// Windows a transcription's ordered segments into overlapping retrieval chunks. A single segment is often a
/// useless retrieval unit (a 3-second "yeah, exactly"), so we embed windows of consecutive segments sized to a
/// character budget, with a small segment overlap so a point split doesn't sever context. Pure and IO-free so
/// the (quality-critical) windowing is unit-testable without the embedding endpoint or a database.
/// </summary>
public static class TranscriptChunker
{
    /// <summary>Approximate upper bound on a chunk's characters (~250-400 tokens for typical speech). A chunk
    /// always holds at least one whole segment, so it may slightly exceed this when a single segment is large.</summary>
    public const int DefaultTargetChars = 1200;

    /// <summary>How many trailing segments of a just-closed chunk seed the next one, for context continuity
    /// across a boundary. 0 = no overlap (each segment appears in exactly one chunk).</summary>
    public const int DefaultOverlapSegments = 1;

    public static IReadOnlyList<ChunkDraft> Chunk(
        IReadOnlyList<ChunkSegment> segments,
        int targetChars = DefaultTargetChars,
        int overlapSegments = DefaultOverlapSegments)
    {
        var result = new List<ChunkDraft>();
        if (segments is null || segments.Count == 0) return result;

        targetChars = Math.Max(1, targetChars);
        overlapSegments = Math.Max(0, overlapSegments);

        var window = new List<ChunkSegment>();
        var chars = 0;
        var ordinal = 0;

        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            window.Add(seg);
            chars += Length(seg);

            if (chars < targetChars) continue;

            result.Add(Build(ordinal++, window));

            // Seed the next window with the overlap tail — unless this was the last segment (avoids emitting a
            // trailing chunk that only repeats already-covered content).
            var isLast = i == segments.Count - 1;
            window = !isLast && overlapSegments > 0 && window.Count > overlapSegments
                ? window.Skip(window.Count - overlapSegments).ToList()
                : new List<ChunkSegment>();
            chars = window.Sum(Length);
        }

        if (window.Count > 0) result.Add(Build(ordinal, window));
        return result;
    }

    /// <summary>Characters this segment contributes to a chunk, matching the "Speaker: Text\n" rendering.</summary>
    private static int Length(ChunkSegment s) =>
        (s.SpeakerDisplay?.Length ?? 0) + 2 + (s.Text?.Length ?? 0) + 1;

    private static ChunkDraft Build(int ordinal, List<ChunkSegment> window)
    {
        var sb = new StringBuilder();
        foreach (var s in window)
            sb.Append(s.SpeakerDisplay).Append(": ").Append(s.Text).Append('\n');

        var speakers = new List<string>();
        foreach (var s in window)
            if (!string.IsNullOrWhiteSpace(s.SpeakerDisplay) && !speakers.Contains(s.SpeakerDisplay))
                speakers.Add(s.SpeakerDisplay);

        return new ChunkDraft(
            ordinal,
            window.Min(s => s.StartMs),
            window.Max(s => s.EndMs),
            speakers,
            sb.ToString().TrimEnd('\n'));
    }
}
