using System.Globalization;
using System.Text;
using System.Text.Json;
using Diariz.Api.Services;

namespace Diariz.Api.Tools;

/// <summary>Pure helpers shared by the transcript tools: argument reading + the standard
/// When / Who / What result formatting. Kept separate so the formatting is unit-testable without a DB.</summary>
public static class ToolFormat
{
    /// <summary>Max characters of a segment's text included in a result line.</summary>
    public const int SnippetChars = 200;

    /// <summary>Reads a string argument, returning null when absent/blank.</summary>
    public static string? ReadString(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : null;

    /// <summary>Resolves the <c>scope</c> argument to a recording-id filter: <c>"current"</c> → the selected
    /// recordings; anything else (default <c>"all"</c>) → null (the whole library).</summary>
    public static IReadOnlyList<Guid>? ResolveScope(JsonElement args, ChatToolContext ctx) =>
        string.Equals(ReadString(args, "scope"), "current", StringComparison.OrdinalIgnoreCase)
            ? ctx.SelectedRecordingIds
            : null;

    /// <summary>Parses a time position into milliseconds: accepts <c>mm:ss</c> / <c>h:mm:ss</c>, or a plain
    /// number of seconds. Returns null when blank/unparseable.</summary>
    public static long? ParseTimeMs(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.Contains(':'))
        {
            var parts = s.Split(':');
            if (parts.Length is < 2 or > 3) return null;
            if (!parts.All(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
                return null;
            var nums = parts.Select(int.Parse).ToArray();
            var total = parts.Length == 3
                ? (nums[0] * 3600) + (nums[1] * 60) + nums[2]
                : (nums[0] * 60) + nums[1];
            return (long)total * 1000;
        }
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var sec)
            ? (long)(sec * 1000)
            : null;
    }

    /// <summary>Parses an ISO-8601 date/time argument as UTC, or null when absent/unparseable.</summary>
    public static DateTimeOffset? ReadDate(JsonElement args, string name)
    {
        var s = ReadString(args, name);
        return s is not null && DateTimeOffset.TryParse(
                s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
            ? d
            : null;
    }

    /// <summary>Reads an integer <c>limit</c> argument, clamped to <c>[1, max]</c>; returns
    /// <paramref name="fallback"/> when absent or not a valid number. Lets the model ask for fewer/more
    /// results (up to the tool's ceiling) instead of always getting the maximum.</summary>
    public static int ReadLimit(JsonElement args, int fallback, int max)
    {
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("limit", out var v)
            && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out var n))
            return Math.Clamp(n, 1, max);
        return Math.Clamp(fallback, 1, max);
    }

    /// <summary>The shared <c>limit</c> JSON-Schema property for retrieval tools (how many results to return,
    /// up to <paramref name="max"/>).</summary>
    public static object LimitProperty(int max) => new
    {
        type = "integer",
        minimum = 1,
        maximum = max,
        description = $"Maximum number of results to return (1-{max}). Defaults to {max}. Ask for more when a " +
            "broad query likely has many relevant matches.",
    };

    /// <summary>The shared <c>scope</c> JSON-Schema property (all vs current selection).</summary>
    public static object ScopeProperty() => new
    {
        type = "string",
        @enum = new[] { "all", "current" },
        description = "Which recordings to search: 'all' (the whole library, default) or " +
            "'current' (only the recordings selected as the chat context).",
    };

    /// <summary>A relative, in-app deep-link to a recording, optionally seeking to a segment time
    /// (ms). The web client opens it in the transcript panel and scrolls to that moment.</summary>
    public static string RecordingHref(Guid recordingId, long? startMs = null) =>
        startMs is { } ms ? $"/recordings/{recordingId}?t={ms}" : $"/recordings/{recordingId}";

    /// <summary>A markdown link to a recording moment, e.g. <c>[Budget Review @ 04:12](/recordings/…?t=…)</c>.
    /// The model is told to cite these so the chat answer can link straight to the transcript.</summary>
    public static string SegmentLink(Guid recordingId, string recordingName, long startMs) =>
        $"[{LinkLabel(recordingName)} @ {Offset(startMs)}]({RecordingHref(recordingId, startMs)})";

    /// <summary>A markdown link to a whole recording.</summary>
    public static string RecordingLink(Guid recordingId, string recordingName) =>
        $"[{LinkLabel(recordingName)}]({RecordingHref(recordingId)})";

    /// <summary>Strips the markdown link-syntax characters from a label so the link can't be broken.</summary>
    private static string LinkLabel(string name)
    {
        var cleaned = (name ?? "").Replace('[', '(').Replace(']', ')');
        return string.IsNullOrWhiteSpace(cleaned) ? "recording" : cleaned.Trim();
    }

    /// <summary>The "When" field: the recording date/time plus the segment's offset, e.g.
    /// <c>2026-06-26 13:25 (at 04:12)</c>.</summary>
    public static string When(DateTimeOffset recordingCreatedAt, long startMs)
    {
        var date = recordingCreatedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        return $"{date} (at {Offset(startMs)})";
    }

    /// <summary>An mm:ss (or h:mm:ss) offset from a millisecond position.</summary>
    public static string Offset(long ms)
    {
        var ts = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    /// <summary>Collapses whitespace and truncates to <see cref="SnippetChars"/> with an ellipsis.</summary>
    public static string Snippet(string text, int max = SnippetChars)
    {
        var collapsed = string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= max ? collapsed : collapsed[..max].TrimEnd() + "…";
    }

    /// <summary>Formats segment hits as numbered When / Who / What lines (the standard format).</summary>
    public static string FormatHits(IReadOnlyList<TranscriptHit> hits)
    {
        if (hits.Count == 0) return "No matching transcript segments were found.";
        var sb = new StringBuilder();
        for (var i = 0; i < hits.Count; i++)
        {
            var h = hits[i];
            sb.Append(i + 1).Append(". When: ").Append(When(h.RecordingCreatedAt, h.StartMs))
              .Append(" · Who: ").Append(h.SpeakerName)
              .Append(" · What: \"").Append(Snippet(h.Text)).Append('"')
              .Append(" · Link: ").Append(SegmentLink(h.RecordingId, h.RecordingName, h.StartMs))
              .Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Formats recording-level results (When · Name · speakers · optional matching snippet).</summary>
    public static string FormatRecordings(IReadOnlyList<RecordingHit> recs)
    {
        if (recs.Count == 0) return "No matching recordings were found.";
        var sb = new StringBuilder();
        for (var i = 0; i < recs.Count; i++)
        {
            var r = recs[i];
            var date = r.RecordingCreatedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            sb.Append(i + 1).Append(". When: ").Append(date)
              .Append(" · Name: ").Append(r.RecordingName);
            if (r.Speakers.Count > 0)
                sb.Append(" · Speakers: ").Append(string.Join(", ", r.Speakers));
            if (!string.IsNullOrWhiteSpace(r.BestSnippet))
                sb.Append(" · Match: \"").Append(Snippet(r.BestSnippet!)).Append('"');
            sb.Append(" · Link: ").Append(RecordingLink(r.RecordingId, r.RecordingName));
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd();
    }
}
