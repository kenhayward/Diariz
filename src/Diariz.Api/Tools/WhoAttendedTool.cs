using System.Text;
using System.Text.Json;
using Diariz.Api.Services;
using Diariz.Domain;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tools;

/// <summary>Tool: who appears (as a speaker) in recordings, optionally within a date range or matching a
/// name. Lists each recording's speakers (with a link) and the distinct set across them.</summary>
public sealed class WhoAttendedTool : IChatTool
{
    private readonly DiarizDbContext _db;

    public WhoAttendedTool(DiarizDbContext db) => _db = db;

    public string Name => "who_attended";
    public string Title => "Who attended";
    public string Description =>
        "List who took part (the speakers) in recordings, optionally within a date range or matching a " +
        "recording name. Returns each recording's speakers plus the distinct set of people across them.";

    public object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            from = new { type = "string", description = "Only recordings on/after this ISO-8601 date/time." },
            to = new { type = "string", description = "Only recordings on/before this ISO-8601 date/time." },
            name = new { type = "string", description = "Filter by recording name (substring)." },
        },
    };

    public async Task<string> ExecuteAsync(JsonElement args, ChatToolContext ctx, CancellationToken ct)
    {
        var from = ToolFormat.ReadDate(args, "from");
        var to = ToolFormat.ReadDate(args, "to");
        var name = ToolFormat.ReadString(args, "name");

        var q = _db.Recordings.Where(r => r.UserId == ctx.UserId);
        if (from is not null) q = q.Where(r => r.CreatedAt >= from);
        if (to is not null) q = q.Where(r => r.CreatedAt <= to);
        if (name is not null) q = q.Where(r => (r.Name ?? r.Title).ToLower().Contains(name.ToLower()));

        var recs = await q
            .OrderByDescending(r => r.CreatedAt)
            .Take(TranscriptSearch.MaxLimit)
            .Include(r => r.Speakers)
            .ToListAsync(ct);

        if (recs.Count == 0) return "No matching recordings were found.";

        var sb = new StringBuilder();
        var everyone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in recs)
        {
            var people = r.Speakers
                .Where(s => !s.IsMultiSpeaker)
                .Select(s => s.DisplayName)
                .Distinct()
                .ToList();
            foreach (var p in people) everyone.Add(p);
            sb.Append(ToolFormat.RecordingLink(r.Id, r.Name ?? r.Title)).Append(": ")
              .Append(people.Count > 0 ? string.Join(", ", people) : "(no speakers)").Append('\n');
        }
        sb.Append("Distinct people across these recordings: ").Append(string.Join(", ", everyone.OrderBy(x => x)));
        return sb.ToString();
    }
}
