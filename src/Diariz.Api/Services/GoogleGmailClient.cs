using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MimeKit;

namespace Diariz.Api.Services;

/// <summary>Creates Gmail drafts in the signed-in user's own account (via their connected Google token).</summary>
public interface IGoogleGmailClient
{
    /// <summary>Create a draft addressed to <paramref name="toEmail"/> (the user's own address). Returns a link
    /// to the Gmail drafts folder, or null if the user hasn't connected Gmail (token unavailable). Throws on a
    /// Gmail API error.</summary>
    Task<string?> CreateDraftAsync(Guid userId, string toEmail, string subject, string htmlBody, CancellationToken ct = default);
}

public class GoogleGmailClient : IGoogleGmailClient
{
    private const string DraftsEndpoint = "https://gmail.googleapis.com/gmail/v1/users/me/drafts";
    private const string DraftsUrl = "https://mail.google.com/mail/u/0/#drafts";

    private readonly HttpClient _http;
    private readonly IGoogleTokenProvider _tokens;

    public GoogleGmailClient(HttpClient http, IGoogleTokenProvider tokens)
    {
        _http = http;
        _tokens = tokens;
    }

    public async Task<string?> CreateDraftAsync(Guid userId, string toEmail, string subject, string htmlBody, CancellationToken ct = default)
    {
        var access = await _tokens.GetAccessTokenAsync(userId, ct);
        if (access is null) return null; // not connected / refresh failed — caller prompts to reconnect

        var raw = BuildRawMessage(toEmail, subject, htmlBody);
        using var req = new HttpRequestMessage(HttpMethod.Post, DraftsEndpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new DraftRequest(new MessagePart(raw))), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Gmail draft creation failed ({(int)resp.StatusCode}): {Truncate(body, 500)}");
        }
        return DraftsUrl;
    }

    /// <summary>Build an RFC-822 message and return it as base64url (Gmail's <c>message.raw</c>).</summary>
    internal static string BuildRawMessage(string toEmail, string subject, string htmlBody)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("", toEmail));
        message.To.Add(new MailboxAddress("", toEmail));
        message.Subject = subject;
        message.Body = new TextPart("html") { Text = htmlBody };
        using var ms = new MemoryStream();
        message.WriteTo(ms);
        return Convert.ToBase64String(ms.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";

    private record DraftRequest([property: JsonPropertyName("message")] MessagePart Message);
    private record MessagePart([property: JsonPropertyName("raw")] string Raw);
}
