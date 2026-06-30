using System.Text;
using System.Text.Json;
using Diariz.Domain;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tools;

/// <summary>Tool: return the summary of a recording — matched by name, or the selected recordings when no
/// name is given. Uses the current (highest-version) transcription's summary.</summary>
public sealed class GetRecordingSummaryTool : IChatTool
{
    private readonly DiarizDbContext _db;

    public GetRecordingSummaryTool(DiarizDbContext db) => _db = db;

    public string Name => "get_recording_summary";
    public string Title => "Get recording summary";
    public string Description =>
        "Return the summary of a recording. Give a 'name' to pick one by name, or omit it to summarise the " +
        "recordings currently selected as chat context.";

    public object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            name = new { type = "string", description = "Recording name to match (substring). Omit to use the selection." },
        },
    };

    public async Task<string> ExecuteAsync(JsonElement args, ChatToolContext ctx, CancellationToken ct)
    {
        var name = ToolFormat.ReadString(args, "name");

        var q = _db.Recordings.Where(r => r.UserId == ctx.UserId);
        if (name is not null)
            q = q.Where(r => (r.Name ?? r.Title).ToLower().Contains(name.ToLower()));
        else if (ctx.SelectedRecordingIds.Count > 0)
            q = q.Where(r => ctx.SelectedRecordingIds.Contains(r.Id));
        else
            return "No recording was specified — give a name or select a recording as chat context.";

        var recs = await q
            .Include(r => r.Transcriptions).ThenInclude(t => t.Summary)
            .Take(5)
            .ToListAsync(ct);

        if (recs.Count == 0) return name is not null ? $"No recording matching \"{name}\" was found." : "No matching recording.";

        var sb = new StringBuilder();
        foreach (var r in recs)
        {
            // Current = highest-version transcription (selected in memory; the in-memory provider can't
            // order inside a filtered Include).
            var current = r.Transcriptions.OrderByDescending(t => t.Version).FirstOrDefault();
            var summary = current?.Summary?.Text;
            sb.Append(ToolFormat.RecordingLink(r.Id, r.Name ?? r.Title)).Append(": ");
            sb.Append(string.IsNullOrWhiteSpace(summary) ? "(no summary yet)" : summary.Trim());
            sb.Append("\n\n");
        }
        return sb.ToString().TrimEnd();
    }
}
