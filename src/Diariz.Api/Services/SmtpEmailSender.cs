using Diariz.Api.Configuration;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Diariz.Api.Services;

/// <summary>Sends email via SMTP (MailKit). When no SMTP host is configured it sends nothing and
/// returns false so callers can fall back to showing the link to the admin.</summary>
public class SmtpEmailSender : IEmailSender
{
    private readonly EmailOptions _opts;
    private readonly ILogger<SmtpEmailSender> _log;

    public SmtpEmailSender(IOptions<EmailOptions> opts, ILogger<SmtpEmailSender> log)
    {
        _opts = opts.Value;
        _log = log;
    }

    public async Task<bool> SendAsync(string to, string subject, string htmlBody,
        IEnumerable<EmailAttachment>? attachments = null, CancellationToken ct = default)
    {
        if (!_opts.Enabled)
            return false; // not configured → caller falls back to the displayed link

        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(string.IsNullOrWhiteSpace(_opts.From) ? _opts.User : _opts.From));
        msg.To.Add(MailboxAddress.Parse(to));
        msg.Subject = subject;
        var builder = new BodyBuilder { HtmlBody = htmlBody };
        if (attachments is not null)
            foreach (var a in attachments)
                builder.Attachments.Add(a.FileName, a.Content, ContentType.Parse(a.ContentType));
        msg.Body = builder.ToMessageBody();

        try
        {
            using var client = new SmtpClient();
            await client.ConnectAsync(
                _opts.SmtpHost, _opts.SmtpPort,
                _opts.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto, ct);
            if (!string.IsNullOrEmpty(_opts.User))
                await client.AuthenticateAsync(_opts.User, _opts.Password, ct);
            await client.SendAsync(msg, ct);
            await client.DisconnectAsync(true, ct);
            return true;
        }
        catch (Exception ex)
        {
            // Don't fail the grant on a mail error — log and fall back to the displayed link.
            _log.LogError(ex, "Failed to send email to {To}", to);
            return false;
        }
    }
}
