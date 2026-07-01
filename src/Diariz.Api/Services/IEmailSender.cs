namespace Diariz.Api.Services;

/// <summary>A file to attach to an email (name + MIME type + bytes).</summary>
public record EmailAttachment(string FileName, string ContentType, byte[] Content);

/// <summary>Sends transactional email (e.g. the account-setup link). Returns true when a message was
/// actually sent; false when email isn't configured, so callers can fall back (e.g. show the link).</summary>
public interface IEmailSender
{
    Task<bool> SendAsync(string to, string subject, string htmlBody,
        IEnumerable<EmailAttachment>? attachments = null, CancellationToken ct = default);
}
