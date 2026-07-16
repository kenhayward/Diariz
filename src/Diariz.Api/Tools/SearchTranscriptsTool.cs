using System.Text.Json;
using Diariz.Api.Services;

namespace Diariz.Api.Tools;

/// <summary>Tool: general topic/keyword retrieval across the user's transcripts. Returns the matching
/// passages (When / Who / What + a deep-link), regardless of speaker.</summary>
public sealed class SearchTranscriptsTool : IChatTool
{
    private readonly ITranscriptSearch _search;

    public SearchTranscriptsTool(ITranscriptSearch search) => _search = search;

    public string Name => "search_transcripts";
    public string Title => "Search transcripts";
    public string Description =>
        "Search the user's transcripts for a topic or phrase and return the matching passages " +
        "(When, Who, What, and a link). Use this for general retrieval when the question isn't about a " +
        "specific speaker.";

    public object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            query = new { type = "string", description = "The topic, phrase, or keywords to search for." },
            scope = ToolFormat.ScopeProperty(),
            limit = ToolFormat.LimitProperty(TranscriptSearch.MaxLimit),
        },
        required = new[] { "query" },
    };

    public async Task<string> ExecuteAsync(JsonElement args, ChatToolContext ctx, CancellationToken ct)
    {
        var query = ToolFormat.ReadString(args, "query");
        if (query is null) return "Provide a 'query' to search for.";
        var scope = ToolFormat.ResolveScope(args, ctx);
        var limit = ToolFormat.ReadLimit(args, TranscriptSearch.MaxLimit, TranscriptSearch.MaxLimit);
        var hits = await _search.SearchAsync(ctx.UserId, query, null, scope, limit, ct: ct);
        return ToolFormat.FormatHits(hits);
    }
}
