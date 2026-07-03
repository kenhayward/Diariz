using System.Text;
using System.Text.Json;
using Diariz.Domain;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tools;

/// <summary>Tool: metadata about one recording — name, date, duration, source, status, speakers, and whether a
/// summary / minutes / action items exist. Lets the model orient before pulling the transcript or minutes.</summary>
public sealed class GetRecordingDetailsTool : IChatTool
{
    private readonly DiarizDbContext _db;

    public GetRecordingDetailsTool(DiarizDbContext db) => _db = db;

    public string Name => "get_recording_details";
    public string Title => "Get recording details";
    public string Description =>
        "Return metadata about a recording: name, date, duration, source, status, speakers, and whether a " +
        "summary, meeting minutes, and action items exist. Give a recording name or id, or use the selection.";

    public object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            recording = RecordingArg.RecordingProperty(),
            recording_id = RecordingArg.RecordingIdProperty(),
        },
    };

    public async Task<string> ExecuteAsync(JsonElement args, ChatToolContext ctx, CancellationToken ct)
    {
        var (query, error) = RecordingArg.Resolve(_db, ctx, args);
        if (query is null) return error!;

        var rec = await query
            .Include(r => r.Speakers)
            .Include(r => r.Actions)
            .Include(r => r.Transcriptions).ThenInclude(t => t.Segments)
            .Include(r => r.Transcriptions).ThenInclude(t => t.Summary)
            .Include(r => r.Transcriptions).ThenInclude(t => t.MeetingMinutes)
            .FirstOrDefaultAsync(ct);
        if (rec is null) return "No matching recording was found.";

        var current = rec.Transcriptions.OrderByDescending(t => t.Version).FirstOrDefault();
        var date = rec.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var speakers = rec.Speakers.Select(s => s.DisplayName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();

        var sb = new StringBuilder();
        sb.Append("Name: ").Append(ToolFormat.RecordingLink(rec.Id, rec.Name ?? rec.Title)).Append('\n');
        sb.Append("When: ").Append(date).Append('\n');
        sb.Append("Duration: ").Append(rec.DurationMs > 0 ? ToolFormat.Offset(rec.DurationMs) : "unknown").Append('\n');
        sb.Append("Source: ").Append(rec.Source).Append('\n');
        sb.Append("Status: ").Append(rec.Status).Append('\n');
        sb.Append("Speakers: ").Append(speakers.Count > 0 ? string.Join(", ", speakers) : "none").Append('\n');
        sb.Append("Segments: ").Append(current?.Segments.Count ?? 0).Append('\n');
        sb.Append("Has summary: ").Append(current?.Summary is not null ? "yes" : "no").Append('\n');
        sb.Append("Has meeting minutes: ").Append(current?.MeetingMinutes is not null ? "yes" : "no").Append('\n');
        sb.Append("Action items: ").Append(rec.Actions.Count);
        return sb.ToString();
    }
}
