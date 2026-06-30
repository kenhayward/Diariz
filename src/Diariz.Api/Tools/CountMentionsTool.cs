using System.Text;
using System.Text.Json;
using Diariz.Api.Services;

namespace Diariz.Api.Tools;

/// <summary>Tool: how often a term was mentioned, broken down by speaker. (Counts the matching segments
/// found; capped at the search limit.)</summary>
public sealed class CountMentionsTool : IChatTool
{
    private readonly ITranscriptSearch _search;

    public CountMentionsTool(ITranscriptSearch search) => _search = search;

    public string Name => "count_mentions";
    public string Title => "Count mentions";
    public string Description =>
        "Count how many times a term or phrase was mentioned across transcripts, broken down by speaker. " +
        "Optionally restrict to one speaker.";

    public object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            term = new { type = "string", description = "The term or phrase to count." },
            speaker = new { type = "string", description = "Optional: only count mentions by this speaker." },
            scope = ToolFormat.ScopeProperty(),
        },
        required = new[] { "term" },
    };

    public async Task<string> ExecuteAsync(JsonElement args, ChatToolContext ctx, CancellationToken ct)
    {
        var term = ToolFormat.ReadString(args, "term");
        if (term is null) return "Provide a 'term' to count.";
        var speaker = ToolFormat.ReadString(args, "speaker");
        var scope = ToolFormat.ResolveScope(args, ctx);
        var hits = await _search.SearchAsync(ctx.UserId, term, speaker, scope, TranscriptSearch.MaxLimit, ct);

        if (hits.Count == 0) return $"No mentions of \"{term}\" were found.";

        var atLeast = hits.Count >= TranscriptSearch.MaxLimit;
        var sb = new StringBuilder();
        sb.Append(atLeast ? "At least " : "").Append(hits.Count)
          .Append(" mention(s) of \"").Append(term).Append("\"");
        if (speaker is not null) sb.Append(" by ").Append(speaker);
        sb.Append('.');

        foreach (var g in hits.GroupBy(h => h.SpeakerName).OrderByDescending(g => g.Count()))
            sb.Append('\n').Append("- ").Append(g.Key).Append(": ").Append(g.Count());
        return sb.ToString();
    }
}
