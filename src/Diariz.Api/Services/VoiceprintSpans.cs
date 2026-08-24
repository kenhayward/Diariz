using System.Text.Json;
using Diariz.Api.Contracts;

namespace Diariz.Api.Services;

/// <summary>How much of a segment the stored spans cover.</summary>
public enum SpanCoverage
{
    /// <summary>Every millisecond of the segment is selected.</summary>
    Included,

    /// <summary>None of it is.</summary>
    Excluded,

    /// <summary>Some of it. Only reachable after a re-transcription moved the segment boundaries under a
    /// selection made against the old ones; re-ticking rewrites the spans and normalises it away.</summary>
    Partial,
}

/// <summary>Reads and writes the <c>VoiceSample.SpansJson</c> column, and reconciles the stored spans
/// against whatever segments the current transcription happens to have.
///
/// <para><b>No spans means the whole speaker</b> - the behaviour every sample had before selection existed.
/// That is why the column is nullable and why nothing needed backfilling.</para>
///
/// <para><b>Spans, not segment ids.</b> Segment rows belong to a transcription <em>version</em>; a
/// re-transcribe replaces every one of them and stored ids would dangle. Speaker rows survive
/// re-transcription, and so do wall-clock times - which are also exactly what the worker needs in order to
/// slice audio.</para></summary>
public static class VoiceprintSpans
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <returns>Null for a null or empty list. Null is "the whole speaker"; an empty array would mean
    /// "train on nothing", which is a different and useless thing.</returns>
    public static string? Serialize(IReadOnlyList<VoiceprintSpan>? spans) =>
        spans is null || spans.Count == 0 ? null : JsonSerializer.Serialize(spans, Options);

    /// <summary>Never throws: an unreadable value reads as "the whole speaker", which is what the sample
    /// did before anyone selected anything.</summary>
    public static IReadOnlyList<VoiceprintSpan> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<VoiceprintSpan>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static long TotalMs(IReadOnlyList<VoiceprintSpan> spans) =>
        Merge(spans).Sum(s => s.EndMs - s.StartMs);

    /// <summary>Classify one segment against the selection. An empty selection is "the whole speaker", so
    /// everything is <see cref="SpanCoverage.Included"/>.</summary>
    public static SpanCoverage Coverage(long startMs, long endMs, IReadOnlyList<VoiceprintSpan> spans)
    {
        if (spans.Count == 0) return SpanCoverage.Included;

        // Merged and ordered so the walk below can consume the segment left to right. The walk carries the
        // cursor across a join by itself, so touching spans cover the segment between them either way;
        // merging is what keeps overlapping spans from making the walk go backwards.
        var merged = Merge(spans);
        if (!merged.Any(s => s.StartMs < endMs && s.EndMs > startMs)) return SpanCoverage.Excluded;

        var cursor = startMs;
        foreach (var s in merged)
        {
            if (s.EndMs <= cursor) continue;
            if (s.StartMs > cursor) break; // a gap the selection does not cover
            cursor = s.EndMs;
            if (cursor >= endMs) return SpanCoverage.Included;
        }
        return SpanCoverage.Partial;
    }

    /// <summary>Turn the segments the user ticked into the audio they occupy: sorted, zero-length dropped,
    /// and overlapping or touching spans collapsed. Without the collapse a long selection becomes hundreds
    /// of one-line spans in the job payload.</summary>
    public static IReadOnlyList<VoiceprintSpan> FromSegments(IEnumerable<(long StartMs, long EndMs)> selected) =>
        Merge(selected.Select(s => new VoiceprintSpan(s.StartMs, s.EndMs)).ToList());

    private static List<VoiceprintSpan> Merge(IReadOnlyList<VoiceprintSpan> spans)
    {
        var result = new List<VoiceprintSpan>();
        foreach (var s in spans.Where(s => s.EndMs > s.StartMs).OrderBy(s => s.StartMs).ThenBy(s => s.EndMs))
        {
            if (result.Count > 0 && s.StartMs <= result[^1].EndMs)
                result[^1] = result[^1] with { EndMs = Math.Max(result[^1].EndMs, s.EndMs) };
            else
                result.Add(s);
        }
        return result;
    }
}
