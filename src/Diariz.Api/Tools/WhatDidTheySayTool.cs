using System.Text.Json;
using Diariz.Api.Services;

namespace Diariz.Api.Tools;

/// <summary>Tool: find what a specific person said about a topic. Fuzzy-matches the topic within a named
/// speaker's transcript segments and returns the moments in the standard When / Who / What format.</summary>
public sealed class WhatDidTheySayTool : IChatTool
{
    private readonly ITranscriptSearch _search;

    public WhatDidTheySayTool(ITranscriptSearch search) => _search = search;

    public string Name => "what_did_they_say";
    public string Title => "What did they say";
    public string Description =>
        "Find what a specific person said about a topic. Fuzzy-matches the topic within the named speaker's " +
        "transcript segments and returns When (date/time), Who (speaker), and What (the transcript text).";

    public object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            speaker = new { type = "string", description = "The speaker's name (as labelled in the transcripts)." },
            topic = new { type = "string", description = "The subject or wording to look for in their speech." },
            scope = ToolFormat.ScopeProperty(),
            limit = ToolFormat.LimitProperty(TranscriptSearch.MaxLimit),
        },
        required = new[] { "speaker", "topic" },
    };

    public async Task<string> ExecuteAsync(JsonElement args, ChatToolContext ctx, CancellationToken ct)
    {
        var speaker = ToolFormat.ReadString(args, "speaker");
        var topic = ToolFormat.ReadString(args, "topic");
        if (speaker is null) return "Provide a 'speaker' name.";
        if (topic is null) return "Provide a 'topic' to look for.";
        var scope = ToolFormat.ResolveScope(args, ctx);
        var limit = ToolFormat.ReadLimit(args, TranscriptSearch.MaxLimit, TranscriptSearch.MaxLimit);
        var hits = await _search.SearchAsync(ctx.UserId, topic, speaker, scope, limit, ct);
        return ToolFormat.FormatHits(hits);
    }
}
