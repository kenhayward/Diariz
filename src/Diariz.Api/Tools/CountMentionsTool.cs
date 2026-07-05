using System.Text;
using System.Text.Json;
using Diariz.Api.Services;

namespace Diariz.Api.Tools;

/// <summary>Tool: how often a term was mentioned, broken down by speaker. Returns an <b>exact</b> count
/// (a grouped COUNT with no cap), so the totals are truthful even for very common terms.</summary>
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
        var counts = await _search.CountMentionsAsync(ctx.UserId, term, speaker, scope, ct);

        var total = counts.Sum(c => c.Count);
        if (total == 0) return $"No mentions of \"{term}\" were found.";

        var sb = new StringBuilder();
        sb.Append(total).Append(" mention(s) of \"").Append(term).Append('"');
        if (speaker is not null) sb.Append(" by ").Append(speaker);
        sb.Append('.');

        foreach (var c in counts.OrderByDescending(c => c.Count))
            sb.Append('\n').Append("- ").Append(c.Speaker).Append(": ").Append(c.Count);
        return sb.ToString();
    }
}
