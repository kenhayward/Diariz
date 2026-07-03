using System.Text.Json;
using Diariz.Domain;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tools;

/// <summary>Tool: the generated meeting minutes (GitHub-flavoured Markdown) of one recording's current
/// transcription, or a note that none exist yet.</summary>
public sealed class GetMeetingMinutesTool : IChatTool
{
    private readonly DiarizDbContext _db;

    public GetMeetingMinutesTool(DiarizDbContext db) => _db = db;

    public string Name => "get_meeting_minutes";
    public string Title => "Get meeting minutes";
    public string Description =>
        "Return the formal meeting minutes (Markdown) generated for a recording, if any. Give a recording " +
        "name or id, or use the selected recording.";

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
            .Include(r => r.Transcriptions).ThenInclude(t => t.MeetingMinutes)
            .FirstOrDefaultAsync(ct);
        if (rec is null) return "No matching recording was found.";

        var current = rec.Transcriptions.OrderByDescending(t => t.Version).FirstOrDefault();
        var minutes = current?.MeetingMinutes?.Text;
        var link = ToolFormat.RecordingLink(rec.Id, rec.Name ?? rec.Title);
        return string.IsNullOrWhiteSpace(minutes)
            ? $"No meeting minutes have been generated for {link} yet."
            : $"Meeting minutes — {link}:\n\n{minutes.Trim()}";
    }
}
