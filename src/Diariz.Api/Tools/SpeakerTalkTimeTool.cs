using System.Text;
using System.Text.Json;
using Diariz.Api.Services;

namespace Diariz.Api.Tools;

/// <summary>Tool: per-speaker talking time for the selected recording(s) (or the whole library), computed
/// from the current transcription's segment durations. Aggregated in SQL over <b>all</b> in-scope
/// recordings (no cap), so the totals and percentages are correct regardless of library size.</summary>
public sealed class SpeakerTalkTimeTool : IChatTool
{
    private readonly ITranscriptSearch _search;

    public SpeakerTalkTimeTool(ITranscriptSearch search) => _search = search;

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
        var scope = ToolFormat.ResolveScope(args, ctx);
        var totals = await _search.SpeakerTalkTimeAsync(ctx.UserId, scope, ct);

        var grand = totals.Sum(t => t.Ms);
        if (grand == 0) return "No transcript segments were found to measure talk time.";

        var sb = new StringBuilder();
        sb.Append("Talk time:\n");
        foreach (var t in totals.OrderByDescending(t => t.Ms))
        {
            var pct = (int)Math.Round(100.0 * t.Ms / grand);
            sb.Append("- ").Append(t.Speaker).Append(": ").Append(ToolFormat.Offset(t.Ms))
              .Append(" (").Append(pct).Append("%)\n");
        }
        return sb.ToString().TrimEnd();
    }
}
