using System.Text.Json;
using Diariz.Api.Services;
using Diariz.Domain;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tools;

/// <summary>
/// Tool: compose and send an email to the signed-in user's OWN registered account address. The recipient is
/// always the current user's registration email — there is deliberately no recipient parameter, and any
/// address the model might invent is ignored — so the assistant can never email a third party. The body is
/// authored by the model (plain text or Markdown) and rendered to HTML. Unlike the other (read-only) tools,
/// this one has a side effect, so it is <b>off by default</b> and the user opts in under Settings → AI.
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
        "asks to be emailed something, and write the body as if it is from them.";

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

        var html = Markdig.Markdown.ToHtml(body);
        var sent = await _email.SendAsync(address, subject, html, null, ct);
        return sent
            ? $"Email sent to {address}."
            : "Email isn't configured on the server, so nothing was sent. Ask an administrator to set it up.";
    }
}
