using System.Text.Json;
using Diariz.Domain;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tools;

/// <summary>Shared resolution of "which recording" for the single-recording read tools. Accepts an exact
/// <c>recording_id</c> (a GUID — how an MCP client passes the id it saw in a link/resource), a <c>recording</c>
/// (or <c>name</c>) substring, or falls back to the in-chat selection. Always owner-scoped.</summary>
public static class RecordingArg
{
    /// <summary>Returns the user's recordings filtered per the args, or an error message when nothing was
    /// specified (no id, no name, and no selection).</summary>
    public static (IQueryable<Recording>? Query, string? Error) Resolve(
        DiarizDbContext db, ChatToolContext ctx, JsonElement args)
    {
        var q = db.Recordings.Where(r => r.UserId == ctx.UserId);

        var idText = ToolFormat.ReadString(args, "recording_id");
        if (idText is not null)
            return Guid.TryParse(idText, out var id)
                ? (q.Where(r => r.Id == id), null)
                : (null, "The 'recording_id' is not a valid id.");

        var name = ToolFormat.ReadString(args, "recording") ?? ToolFormat.ReadString(args, "name");
        if (name is not null)
        {
            var lower = name.ToLower();
            return (q.Where(r => (r.Name ?? r.Title).ToLower().Contains(lower)), null);
        }

        if (ctx.SelectedRecordingIds.Count > 0)
            return (q.Where(r => ctx.SelectedRecordingIds.Contains(r.Id)), null);

        return (null, "Specify a recording by 'recording_id' or 'recording' name (or select one as chat context).");
    }

    /// <summary>The shared <c>recording</c> + <c>recording_id</c> schema properties for single-recording tools.</summary>
    public static object RecordingProperty() => new
    {
        type = "string",
        description = "Recording name to match (substring). Omit if giving 'recording_id' or using the selection.",
    };

    public static object RecordingIdProperty() => new
    {
        type = "string",
        description = "Exact recording id (as seen in a transcript link or resource). Takes precedence over 'recording'.",
    };
}
