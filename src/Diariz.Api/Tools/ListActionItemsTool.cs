using System.Text;
using System.Text.Json;
using Diariz.Domain;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tools;

/// <summary>Tool: list extracted action items, optionally restricted to the selected recordings or to an
/// assignee. Each recording links back to its transcript.</summary>
public sealed class ListActionItemsTool : IChatTool
{
    private readonly DiarizDbContext _db;

    public ListActionItemsTool(DiarizDbContext db) => _db = db;

    public string Name => "list_action_items";
    public string Title => "List action items";
    public string Description =>
        "List action items extracted from transcripts (the task, who it's assigned to, and any deadline). " +
        "Optionally restrict to the selected recordings or to a particular assignee.";

    public object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            actor = new { type = "string", description = "Optional: only items assigned to this person." },
            scope = ToolFormat.ScopeProperty(),
        },
    };

    public async Task<string> ExecuteAsync(JsonElement args, ChatToolContext ctx, CancellationToken ct)
    {
        var actor = ToolFormat.ReadString(args, "actor");
        var current = ToolFormat.ResolveScope(args, ctx) is not null;

        var q = _db.RecordingActions.Where(a => a.Recording!.UserId == ctx.UserId);
        if (current) q = q.Where(a => ctx.SelectedRecordingIds.Contains(a.RecordingId));
        if (actor is not null) q = q.Where(a => a.Actor.ToLower().Contains(actor.ToLower()));

        var items = await q
            .OrderBy(a => a.RecordingId).ThenBy(a => a.Ordinal)
            .Select(a => new
            {
                a.Text, a.Actor, a.Deadline, a.RecordingId,
                RecName = a.Recording!.Name ?? a.Recording.Title,
            })
            .ToListAsync(ct);

        if (items.Count == 0) return "No action items were found.";

        var sb = new StringBuilder();
        foreach (var grp in items.GroupBy(i => new { i.RecordingId, i.RecName }))
        {
            sb.Append("From ").Append(ToolFormat.RecordingLink(grp.Key.RecordingId, grp.Key.RecName)).Append(":\n");
            foreach (var a in grp)
            {
                sb.Append("- ").Append(a.Text);
                if (!string.IsNullOrWhiteSpace(a.Actor)) sb.Append(" (Actor: ").Append(a.Actor).Append(')');
                if (!string.IsNullOrWhiteSpace(a.Deadline)) sb.Append(" (Due: ").Append(a.Deadline).Append(')');
                sb.Append('\n');
            }
        }
        return sb.ToString().TrimEnd();
    }
}
