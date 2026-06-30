using System.Text;
using System.Text.Json;
using Diariz.Api.Services;
using Diariz.Domain;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tools;

/// <summary>Tool: per-speaker talking time for the selected recording(s) (or the whole library), computed
/// from the current transcription's segment durations.</summary>
public sealed class SpeakerTalkTimeTool : IChatTool
{
    private readonly DiarizDbContext _db;

    public SpeakerTalkTimeTool(DiarizDbContext db) => _db = db;

    public string Name => "speaker_talk_time";
    public string Title => "Speaker talk time";
    public string Description =>
        "Measure how long each speaker talked, as time and a percentage, for the selected recordings (scope " +
        "'current') or across the library (scope 'all'). Computed from segment durations.";

    public object ParametersSchema => new
    {
        type = "object",
        properties = new { scope = ToolFormat.ScopeProperty() },
    };

    public async Task<string> ExecuteAsync(JsonElement args, ChatToolContext ctx, CancellationToken ct)
    {
        var current = ToolFormat.ResolveScope(args, ctx) is not null;

        var q = _db.Recordings.Where(r => r.UserId == ctx.UserId);
        if (current) q = q.Where(r => ctx.SelectedRecordingIds.Contains(r.Id));

        var recs = await q
            .OrderByDescending(r => r.CreatedAt)
            .Take(TranscriptSearch.MaxLimit)
            .Include(r => r.Speakers)
            .Include(r => r.Transcriptions).ThenInclude(t => t.Segments)
            .ToListAsync(ct);

        var totals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in recs)
        {
            var names = r.Speakers.ToDictionary(s => s.Label, s => s.DisplayName);
            var currentTr = r.Transcriptions.OrderByDescending(t => t.Version).FirstOrDefault();
            foreach (var seg in currentTr?.Segments ?? [])
            {
                var who = names.TryGetValue(seg.SpeakerLabel, out var dn) ? dn : seg.SpeakerLabel;
                totals[who] = totals.GetValueOrDefault(who) + Math.Max(0, seg.EndMs - seg.StartMs);
            }
        }

        var grand = totals.Values.Sum();
        if (grand == 0) return "No transcript segments were found to measure talk time.";

        var sb = new StringBuilder();
        sb.Append("Talk time:\n");
        foreach (var kv in totals.OrderByDescending(kv => kv.Value))
        {
            var pct = (int)Math.Round(100.0 * kv.Value / grand);
            sb.Append("- ").Append(kv.Key).Append(": ").Append(ToolFormat.Offset(kv.Value))
              .Append(" (").Append(pct).Append("%)\n");
        }
        return sb.ToString().TrimEnd();
    }
}
