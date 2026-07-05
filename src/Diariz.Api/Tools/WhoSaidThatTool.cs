using System.Text.Json;
using Diariz.Api.Services;

namespace Diariz.Api.Tools;

/// <summary>Tool: find who said a given phrase. Fuzzy-matches the phrase across the user's transcript
/// segments and returns the matching moments in the standard When / Who / What format.</summary>
public sealed class WhoSaidThatTool : IChatTool
{
    private readonly ITranscriptSearch _search;

    public WhoSaidThatTool(ITranscriptSearch search) => _search = search;

    public string Name => "who_said_that";
    public string Title => "Who said that";
    public string Description =>
        "Find who said a given phrase. Fuzzy-matches the phrase across the user's meeting transcripts and " +
        "returns the matching moments as When (date/time), Who (speaker), and What (the transcript text).";

    public object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            phrase = new { type = "string", description = "The phrase or wording to search for." },
            scope = ToolFormat.ScopeProperty(),
            limit = ToolFormat.LimitProperty(TranscriptSearch.MaxLimit),
        },
        required = new[] { "phrase" },
    };

    public async Task<string> ExecuteAsync(JsonElement args, ChatToolContext ctx, CancellationToken ct)
    {
        var phrase = ToolFormat.ReadString(args, "phrase");
        if (phrase is null) return "Provide a 'phrase' to search for.";
        var scope = ToolFormat.ResolveScope(args, ctx);
        var limit = ToolFormat.ReadLimit(args, TranscriptSearch.MaxLimit, TranscriptSearch.MaxLimit);
        var hits = await _search.SearchAsync(ctx.UserId, phrase, null, scope, limit, ct);
        return ToolFormat.FormatHits(hits);
    }
}
