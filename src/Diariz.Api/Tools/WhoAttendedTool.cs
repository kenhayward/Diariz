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

        // The matching recording ids (materialised so the distinct-people set below covers ALL of them, not
        // just the ones we list). The per-recording breakdown is capped for output length, but the distinct
        // set - the actual "who attended" answer - is always complete.
        var matchingIds = await q.Select(r => r.Id).ToListAsync(ct);
        if (matchingIds.Count == 0) return "No matching recordings were found.";

        var everyone = await _db.Speakers
            .Where(s => matchingIds.Contains(s.RecordingId) && !s.IsMultiSpeaker)
            .Select(s => s.DisplayName)
            .Distinct()
            .ToListAsync(ct);

        var recs = await q
            .OrderByDescending(r => r.CreatedAt)
            .Take(TranscriptSearch.MaxLimit)
            .Include(r => r.Speakers)
            .ToListAsync(ct);

        var sb = new StringBuilder();
        foreach (var r in recs)
        {
            var people = r.Speakers
                .Where(s => !s.IsMultiSpeaker)
                .Select(s => s.DisplayName)
                .Distinct()
                .ToList();
            sb.Append(ToolFormat.RecordingLink(r.Id, r.Name ?? r.Title)).Append(": ")
              .Append(people.Count > 0 ? string.Join(", ", people) : "(no speakers)").Append('\n');
        }
        if (matchingIds.Count > recs.Count)
            sb.Append($"(showing {recs.Count} of {matchingIds.Count} recordings; the distinct set below is complete)\n");
        sb.Append("Distinct people across these recordings: ")
          .Append(string.Join(", ", everyone.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));
        return sb.ToString();
    }
}
