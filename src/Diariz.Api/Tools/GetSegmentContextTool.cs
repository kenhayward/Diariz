using System.Text.Json;
using Diariz.Api.Services;
using Diariz.Domain;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tools;

/// <summary>Tool: the transcript around a given moment — returns the segments surrounding a time position in
/// a recording (drill-down after a search hit), each as When / Who / What with a deep-link.</summary>
public sealed class GetSegmentContextTool : IChatTool
{
    private readonly DiarizDbContext _db;

    public GetSegmentContextTool(DiarizDbContext db) => _db = db;

    public string Name => "get_segment_context";
    public string Title => "Get segment context";
    public string Description =>
        "Show the transcript around a specific moment in a recording — the segments just before and after a " +
        "time position. Useful to expand on a search result. Give the recording name and a time (mm:ss).";

    public object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            recording = new { type = "string", description = "Recording name (substring). Omit to use the selected recording." },
            time = new { type = "string", description = "The moment, as mm:ss or seconds." },
            window = new { type = "integer", description = "How many segments to show on each side (default 2)." },
        },
        required = new[] { "time" },
    };

    public async Task<string> ExecuteAsync(JsonElement args, ChatToolContext ctx, CancellationToken ct)
    {
        var ms = ToolFormat.ParseTimeMs(ToolFormat.ReadString(args, "time"));
        if (ms is null) return "Provide a 'time' (mm:ss or seconds).";
        var name = ToolFormat.ReadString(args, "recording");
        var window = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("window", out var w)
                     && w.ValueKind == JsonValueKind.Number ? Math.Clamp(w.GetInt32(), 0, 10) : 2;

        var q = _db.Recordings.Where(r => r.UserId == ctx.UserId);
        if (name is not null)
            q = q.Where(r => (r.Name ?? r.Title).ToLower().Contains(name.ToLower()));
        else if (ctx.SelectedRecordingIds.Count > 0)
            q = q.Where(r => ctx.SelectedRecordingIds.Contains(r.Id));
        else
            return "Specify a 'recording' name or select a recording as chat context.";

        var rec = await q
            .Include(r => r.Speakers)
            .Include(r => r.Transcriptions).ThenInclude(t => t.Segments)
            .FirstOrDefaultAsync(ct);
        if (rec is null) return name is not null ? $"No recording matching \"{name}\" was found." : "No matching recording.";

        var current = rec.Transcriptions.OrderByDescending(t => t.Version).FirstOrDefault();
        var segs = current?.Segments.OrderBy(s => s.Ordinal).ToList() ?? [];
        if (segs.Count == 0) return "That recording has no transcript yet.";

        // The segment containing the time, else the nearest by start.
        var idx = segs.FindIndex(s => ms >= s.StartMs && ms < s.EndMs);
        if (idx < 0)
        {
            idx = 0;
            var best = long.MaxValue;
            for (var i = 0; i < segs.Count; i++)
            {
                var d = Math.Abs(segs[i].StartMs - ms.Value);
                if (d < best) { best = d; idx = i; }
            }
        }

        var names = rec.Speakers.ToDictionary(s => s.Label, s => s.DisplayName);
        var from = Math.Max(0, idx - window);
        var to = Math.Min(segs.Count - 1, idx + window);
        var hits = new List<TranscriptHit>();
        for (var i = from; i <= to; i++)
        {
            var s = segs[i];
            var who = names.TryGetValue(s.SpeakerLabel, out var dn) ? dn : s.SpeakerLabel;
            hits.Add(new TranscriptHit(rec.Id, rec.Name ?? rec.Title, rec.CreatedAt, s.StartMs, who,
                s.Revised ?? s.Original, 1));
        }
        return ToolFormat.FormatHits(hits);
    }
}
