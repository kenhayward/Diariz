using System.Net;
using System.Text;
using System.Text.Json;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;

namespace Diariz.Api.Tests;

public class GoogleGmailClientTests
{
    private sealed class StubTokenProvider(string? token) : IGoogleTokenProvider
    {
        public Guid? LastUserId { get; private set; }
        public Task<string?> GetAccessTokenAsync(Guid userId, CancellationToken ct = default)
        {
            LastUserId = userId;
            return Task.FromResult(token);
        }
    }

    [Fact]
    public async Task CreateDraftAsync_ReturnsNull_WhenNoAccessToken()
    {
        var handler = new FakeHttpMessageHandler("{}");
        var client = new GoogleGmailClient(new HttpClient(handler), new StubTokenProvider(null));

        var url = await client.CreateDraftAsync(Guid.NewGuid(), "me@x.test", "Sub", "<p>hi</p>");

        Assert.Null(url);
        Assert.Null(handler.LastRequest); // never called Gmail
    }

    [Fact]
    public async Task CreateDraftAsync_PostsBase64UrlRawMimeWithBearerToken()
    {
        var handler = new FakeHttpMessageHandler("{\"id\":\"draft123\"}");
        var tokens = new StubTokenProvider("access-tok");
        var client = new GoogleGmailClient(new HttpClient(handler), tokens);
        var userId = Guid.NewGuid();

        var url = await client.CreateDraftAsync(userId, "me@x.test", "Minutes: Board", "<h1>Notes</h1>");

        Assert.Equal(userId, tokens.LastUserId);
        Assert.Contains("mail.google.com", url); // link back to drafts
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("https://gmail.googleapis.com/gmail/v1/users/me/drafts", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("access-tok", handler.LastRequest.Headers.Authorization.Parameter);

        // Body is { "message": { "raw": "<base64url>" } }; decode and confirm it's the MIME we built.
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var raw = doc.RootElement.GetProperty("message").GetProperty("raw").GetString()!;
        Assert.DoesNotContain('+', raw);
        Assert.DoesNotContain('/', raw);
        Assert.DoesNotContain('=', raw);
        var mime = Encoding.UTF8.GetString(FromBase64Url(raw));
        Assert.Contains("Subject: Minutes: Board", mime);
        Assert.Contains("me@x.test", mime);
        Assert.Contains("<h1>Notes</h1>", mime);
    }

    [Fact]
    public async Task CreateDraftAsync_Throws_OnGmailError()
    {
        var handler = new FakeHttpMessageHandler("{\"error\":\"insufficientPermissions\"}", HttpStatusCode.Forbidden);
        var client = new GoogleGmailClient(new HttpClient(handler), new StubTokenProvider("tok"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CreateDraftAsync(Guid.NewGuid(), "me@x.test", "Sub", "<p>hi</p>"));

        Assert.Contains("403", ex.Message);
        Assert.Contains("insufficientPermissions", ex.Message);
    }

    private static byte[] FromBase64Url(string s)
    {
        var b = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b.PadRight(b.Length + (4 - b.Length % 4) % 4, '='));
    }
}
