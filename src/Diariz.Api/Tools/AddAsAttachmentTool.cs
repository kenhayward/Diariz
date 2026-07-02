using System.Text.Json;
using Diariz.Domain;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tools;

/// <summary>
/// Tool: save content the model has prepared (a summary, notes, a table…) to a transcript as a Markdown
/// attachment. It does not write anything itself — it queues an <see cref="AttachmentDraft"/> on the tool
/// effects sink; the client then creates the attachment, choosing the destination: exactly one selected
/// transcript → add it there; several → the user picks one from a list. The candidates are the recordings the
/// chat has in context (owner-scoped), so the note can only ever land on the user's own transcripts.
/// </summary>
public sealed class AddAsAttachmentTool : IChatTool
{
    private readonly DiarizDbContext _db;

    public AddAsAttachmentTool(DiarizDbContext db) => _db = db;

    public string Name => "add_as_attachment";
    public string Title => "Add as attachment";
    public string Description =>
        "Save content you have prepared (e.g. a summary, notes, or a table) onto a transcript as a Markdown " +
        "attachment. Provide a short name and the content as Markdown. It is attached to the transcript in " +
        "context; if several transcripts are selected the user will choose which one. Use this when the user " +
        "asks you to attach or save your output to the transcript.";

    public object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            name = new { type = "string", description = "A short name for the note (a .md extension is added)." },
            content = new { type = "string", description = "The note content, as Markdown." },
        },
        required = new[] { "name", "content" },
    };

    public async Task<string> ExecuteAsync(JsonElement args, ChatToolContext ctx, CancellationToken ct)
    {
        var name = ToolFormat.ReadString(args, "name");
        var content = ToolFormat.ReadString(args, "content");
        if (content is null) return "Cannot add the attachment: some content is required.";
        var displayName = string.IsNullOrWhiteSpace(name) ? "note" : name!;

        // Candidates are the in-context recordings the user actually owns.
        var candidates = await AttachmentTargets.ForContextAsync(_db, ctx, ct);
        if (candidates.Count == 0)
            return "There is no transcript in context to attach to. Ask the user to select a transcript, then try again.";

        ctx.Effects?.AttachmentDrafts.Add(new AttachmentDraft(displayName, content, candidates));

        return candidates.Count == 1
            ? $"Prepared the note \"{displayName}\"; it will be added as an attachment to {candidates[0].Title}."
            : $"Prepared the note \"{displayName}\"; the user will choose which of the {candidates.Count} selected transcripts to attach it to.";
    }
}
