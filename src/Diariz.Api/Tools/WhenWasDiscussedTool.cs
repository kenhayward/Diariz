using System.Text;
using System.Text.Json;
using Diariz.Api.Services;

namespace Diariz.Api.Tools;

/// <summary>Tool: when a topic was discussed — reports the earliest and latest matching moments (with
/// deep-links) and how many matches were found.</summary>
public sealed class WhenWasDiscussedTool : IChatTool
{
    private readonly ITranscriptSearch _search;

    public WhenWasDiscussedTool(ITranscriptSearch search) => _search = search;

    public string Name => "when_was_discussed";
    public string Title => "When was it discussed";
    public string Description =>
        "Find when a topic was discussed: returns the earliest and latest matching moments (date/time " +
        "and a link to that point in the transcript) and the number of matches found.";

    public object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            topic = new { type = "string", description = "The topic or phrase to locate in time." },
            scope = ToolFormat.ScopeProperty(),
        },
        required = new[] { "topic" },
    };

    public async Task<string> ExecuteAsync(JsonElement args, ChatToolContext ctx, CancellationToken ct)
    {
        var topic = ToolFormat.ReadString(args, "topic");
        if (topic is null) return "Provide a 'topic' to locate.";
        var scope = ToolFormat.ResolveScope(args, ctx);
        var hits = await _search.SearchAsync(ctx.UserId, topic, null, scope, TranscriptSearch.MaxLimit, ct);
        if (hits.Count == 0) return $"No mentions of \"{topic}\" were found.";

        // Order the matches chronologically (recording date, then offset within it).
        var ordered = hits.OrderBy(h => h.RecordingCreatedAt).ThenBy(h => h.StartMs).ToList();
        var first = ordered[0];
        var last = ordered[^1];

        var sb = new StringBuilder();
        sb.Append($"\"{topic}\" — {hits.Count} match(es) found");
        if (hits.Count >= TranscriptSearch.MaxLimit) sb.Append(" (showing the most relevant)");
        sb.Append('\n');
        sb.Append("Earliest: ").Append(Line(first)).Append('\n');
        if (last != first) sb.Append("Latest: ").Append(Line(last));
        return sb.ToString().TrimEnd();
    }

    private static string Line(TranscriptHit h) =>
        $"When: {ToolFormat.When(h.RecordingCreatedAt, h.StartMs)} · Who: {h.SpeakerName} · " +
        $"What: \"{ToolFormat.Snippet(h.Text)}\" · Link: {ToolFormat.SegmentLink(h.RecordingId, h.RecordingName, h.StartMs)}";
}
