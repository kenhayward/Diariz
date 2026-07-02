using System.Text.Json;
using Diariz.Api.Services;
using Diariz.Domain;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tools;

/// <summary>
/// Tool: compose and send an email to the signed-in user's OWN registered account address. The recipient is
/// always the current user's registration email — there is deliberately no recipient parameter, and any
/// address the model might invent is ignored — so the assistant can never email a third party. The body is
/// authored by the model (plain text or Markdown) and rendered to HTML. It has a side effect (unlike the other,
/// read-only tools) but is safe to leave on by default since it can only ever reach the user's own address; a
/// user or operator can still disable it in Settings / <c>Chat:DisabledTools</c>.
/// </summary>
public sealed class SendEmailTool : IChatTool
{
    private readonly DiarizDbContext _db;
    private readonly IEmailSender _email;

    public SendEmailTool(DiarizDbContext db, IEmailSender email)
    {
        _db = db;
        _email = email;
    }

    public string Name => "send_email";
    public string Title => "Send email to me";
    public string Description =>
        "Email the signed-in user their own content (e.g. a summary, action items, or notes you have prepared). " +
        "It is ALWAYS delivered to the current user's own account address — you cannot choose or change the " +
        "recipient. Provide a subject and a body (plain text or simple Markdown). Use this only when the user " +
        "asks to be emailed something, and write the body as if it is from them. A copy of the sent email is " +
        "also saved to the transcript in context as a Markdown attachment (titled \"Email: <subject>\").";

    public object ParametersSchema => new
    {
        type = "object",
        properties = new
        {
            subject = new { type = "string", description = "The email subject line." },
            body = new { type = "string", description = "The email body — plain text or simple Markdown." },
        },
        required = new[] { "subject", "body" },
    };

    public async Task<string> ExecuteAsync(JsonElement args, ChatToolContext ctx, CancellationToken ct)
    {
        var subject = ToolFormat.ReadString(args, "subject");
        var body = ToolFormat.ReadString(args, "body");
        if (subject is null) return "Cannot send the email: a subject is required.";
        if (body is null) return "Cannot send the email: a body is required.";

        // The recipient is ALWAYS the owner's registered address — never anything from the model's arguments.
        var address = await _db.Users
            .Where(u => u.Id == ctx.UserId)
            .Select(u => u.Email)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(address))
            return "Cannot send the email: the current user has no email address on record.";

        // Render the model's Markdown to HTML with the shared (advanced) pipeline so GitHub-flavoured tables,
        // lists and bold come through as formatting rather than raw markdown, then wrap it in a simple email
        // shell (readable font) — the same approach as the meeting-minutes email.
        var html = $"<div style=\"font-family:Arial,Helvetica,sans-serif;color:#111;line-height:1.5;\">" +
            $"{MarkdownRenderer.ToHtml(body)}</div>";
        var sent = await _email.SendAsync(address, subject, html, null, ct);
        if (!sent)
            return "Email isn't configured on the server, so nothing was sent. Ask an administrator to set it up.";

        // File a copy of the sent email on the transcript in context, as a Markdown attachment — same selection
        // flow as add_as_attachment: one candidate → added there; several → the user picks. The content is the
        // model's Markdown body (not the HTML), so it stays readable/editable like any other note.
        List<DraftRecording> candidates =
            ctx.Effects is null ? [] : await AttachmentTargets.ForContextAsync(_db, ctx, ct);
        if (candidates.Count == 0)
            return $"Email sent to {address}. (No transcript is in context, so no copy was saved as an attachment.)";

        ctx.Effects!.AttachmentDrafts.Add(new AttachmentDraft($"Email: {subject}", body, candidates));
        return candidates.Count == 1
            ? $"Email sent to {address}; a copy will be saved as an attachment on {candidates[0].Title}."
            : $"Email sent to {address}; the user will choose which of the {candidates.Count} selected transcripts to save a copy to.";
    }
}
