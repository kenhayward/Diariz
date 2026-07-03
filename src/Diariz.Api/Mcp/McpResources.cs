using System.Globalization;
using System.Text;
using Diariz.Domain.Entities;

namespace Diariz.Api.Mcp;

/// <summary>Pure helpers for the MCP resource surface: the <c>diariz://recording/{id}/{kind}</c> URI scheme
/// and plain-Markdown transcript rendering. Kept free of the SDK + database so the URI round-trip and
/// formatting are unit-testable.</summary>
public static class McpResources
{
    public const string TranscriptKind = "transcript";
    public const string MinutesKind = "minutes";
    public const string MarkdownMime = "text/markdown";

    public static string TranscriptUri(Guid recordingId) => $"diariz://recording/{recordingId}/{TranscriptKind}";
    public static string MinutesUri(Guid recordingId) => $"diariz://recording/{recordingId}/{MinutesKind}";

    /// <summary>Parses a resource URI into a recording id + kind (<c>transcript</c> | <c>minutes</c>).
    /// Returns false for anything that isn't a well-formed diariz recording URI.</summary>
    public static bool TryParse(string? uri, out Guid recordingId, out string kind)
    {
        recordingId = Guid.Empty;
        kind = "";
        if (string.IsNullOrWhiteSpace(uri)) return false;
        const string prefix = "diariz://recording/";
        if (!uri.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var rest = uri[prefix.Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0 || slash == rest.Length - 1) return false;
        if (!Guid.TryParse(rest[..slash], out recordingId)) return false;
        kind = rest[(slash + 1)..];
        return kind is TranscriptKind or MinutesKind;
    }

    /// <summary>Renders a transcript as plain Markdown (a heading + one <c>[mm:ss] Speaker: text</c> line per
    /// segment) — the body of a transcript resource. No deep-link (the resource *is* the transcript).</summary>
    public static string TranscriptText(
        string recordingName, DateTimeOffset createdAt, IReadOnlyList<Segment> segments,
        IReadOnlyDictionary<string, string> speakerNames)
    {
        var date = createdAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.Append("# ").Append(recordingName).Append(" — ").Append(date).Append("\n\n");
        foreach (var s in segments.OrderBy(s => s.Ordinal))
        {
            var who = speakerNames.TryGetValue(s.SpeakerLabel, out var dn) ? dn : s.SpeakerLabel;
            sb.Append('[').Append(McpOffset(s.StartMs)).Append("] ").Append(who).Append(": ")
              .Append((s.Revised ?? s.Original).Trim()).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    private static string McpOffset(long ms)
    {
        var ts = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}
