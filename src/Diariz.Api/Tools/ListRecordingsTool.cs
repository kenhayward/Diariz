using System.Text.Json;
using Diariz.Api.Services;

namespace Diariz.Api.Tools;

/// <summary>Tool: list the user's recordings, optionally filtered by date range, name, speaker, and/or a
/// <c>contains</c> topic that surfaces recordings whose transcript is about that topic (trigram-matched).</summary>
public sealed class ListRecordingsTool : IChatTool
{
    private readonly ITranscriptSearch _search;

    public ListRecordingsTool(ITranscriptSearch search) => _search = search;

    public string Name => "list_recordings";
    public string Title => "List recordings";
    public string Description =>
        "List the user's recordings, optionally filtered by a date range, a name, a speaker, and/or a " +
        "'contains' topic that finds recordings whose transcript is about that topic. Returns When (date), " +
        "Name, the speakers, and a matching snippet when a topic is given.";

    public object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            from = new { type = "string", description = "Only recordings on/after this ISO-8601 date/time." },
            to = new { type = "string", description = "Only recordings on/before this ISO-8601 date/time." },
            name = new { type = "string", description = "Filter by recording name (substring)." },
            speaker = new { type = "string", description = "Only recordings that include this speaker." },
            contains = new
            {
                type = "string",
                description = "Only recordings whose transcript is about this topic; results are ranked by match.",
            },
            limit = ToolFormat.LimitProperty(TranscriptSearch.MaxLimit),
        },
    };

    public async Task<string> ExecuteAsync(JsonElement args, ChatToolContext ctx, CancellationToken ct)
    {
        var from = ToolFormat.ReadDate(args, "from");
        var to = ToolFormat.ReadDate(args, "to");
        var name = ToolFormat.ReadString(args, "name");
        var speaker = ToolFormat.ReadString(args, "speaker");
        var contains = ToolFormat.ReadString(args, "contains");
        var limit = ToolFormat.ReadLimit(args, TranscriptSearch.MaxLimit, TranscriptSearch.MaxLimit);
        var recs = await _search.ListRecordingsAsync(
            ctx.UserId, from, to, name, speaker, contains, limit, ct);
        return ToolFormat.FormatRecordings(recs);
    }
}
