using System.Text;
using System.Text.Json;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tools;

/// <summary>Tool: the full current-version transcript of one recording as speaker-labelled, timestamped lines
/// (<c>[mm:ss] Speaker: text</c>). Size-guarded — long transcripts are truncated with a note. Complements the
/// snippet-returning search tools when the model needs the whole conversation (e.g. over MCP).</summary>
public sealed class GetTranscriptTool : IChatTool
{
    /// <summary>Default cap on returned characters; overridable via <c>max_chars</c>.</summary>
    public const int DefaultMaxChars = 16000;

    private readonly DiarizDbContext _db;

    public GetTranscriptTool(DiarizDbContext db) => _db = db;

    public string Name => "get_transcript";
    public string Title => "Get full transcript";
    public string Description =>
        "Return the full transcript of one recording as speaker-labelled, timestamped lines. Use this when you " +
        "need the whole conversation rather than search snippets. Long transcripts are truncated; raise " +
        "'max_chars' to get more.";

    public object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            recording = RecordingArg.RecordingProperty(),
            recording_id = RecordingArg.RecordingIdProperty(),
            max_chars = new { type = "integer", description = $"Max characters to return (default {DefaultMaxChars})." },
        },
    };

    public async Task<string> ExecuteAsync(JsonElement args, ChatToolContext ctx, CancellationToken ct)
    {
        var (query, error) = RecordingArg.Resolve(_db, ctx, args);
        if (query is null) return error!;

        var max = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("max_chars", out var mc)
                  && mc.ValueKind == JsonValueKind.Number
            ? Math.Clamp(mc.GetInt32(), 500, 200_000)
            : DefaultMaxChars;

        var rec = await query
            .Include(r => r.Speakers)
            .Include(r => r.Transcriptions).ThenInclude(t => t.Segments)
            .FirstOrDefaultAsync(ct);
        if (rec is null) return "No matching recording was found.";

        var current = rec.Transcriptions.OrderByDescending(t => t.Version).FirstOrDefault();
        var segs = current?.Segments.OrderBy(s => s.Ordinal).ToList() ?? [];
        if (segs.Count == 0) return "That recording has no transcript yet.";

        var names = rec.Speakers.ToDictionary(s => s.Label, s => s.DisplayName);
        return FormatTranscript(rec.Id, rec.Name ?? rec.Title, rec.CreatedAt, segs, names, max);
    }

    /// <summary>Renders the transcript lines with a header, stopping once <paramref name="maxChars"/> is
    /// reached and appending a truncation note. Pure so it can be unit-tested without a database.</summary>
    public static string FormatTranscript(
        Guid recordingId, string recordingName, DateTimeOffset createdAt, IReadOnlyList<Segment> segments,
        IReadOnlyDictionary<string, string> speakerNames, int maxChars)
    {
        var date = createdAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.Append("Transcript — ").Append(ToolFormat.RecordingLink(recordingId, recordingName))
          .Append(" (").Append(date).Append(")\n");

        var shown = 0;
        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            var who = speakerNames.TryGetValue(s.SpeakerLabel, out var dn) ? dn : s.SpeakerLabel;
            var line = $"[{ToolFormat.Offset(s.StartMs)}] {who}: {(s.Revised ?? s.Original).Trim()}\n";
            if (sb.Length + line.Length > maxChars && shown > 0)
            {
                sb.Append("… (truncated — ").Append(shown).Append(" of ").Append(segments.Count)
                  .Append(" segments shown; raise max_chars for more)");
                return sb.ToString();
            }
            sb.Append(line);
            shown++;
        }
        return sb.ToString().TrimEnd();
    }
}
