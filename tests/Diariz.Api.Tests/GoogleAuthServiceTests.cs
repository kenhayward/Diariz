using System.Net;
using Diariz.Api.Configuration;
using Diariz.Api.Services;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

public class GoogleAuthServiceTests
{
    private static GoogleAuthService Build(GoogleAuthOptions? opts = null, HttpMessageHandler? handler = null) =>
        new(handler is null ? new HttpClient() : new HttpClient(handler), Options.Create(opts ?? new GoogleAuthOptions
        {
            ClientId = "cid.apps.googleusercontent.com",
            ClientSecret = "secret",
        }));

    // ---- PKCE (RFC 7636) ----

    [Fact]
    public void Challenge_MatchesRfc7636Vector()
    {
        // RFC 7636, Appendix B.
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", OAuthPkce.Challenge(verifier));
    }

    [Fact]
    public void NewCodeVerifier_IsUrlSafeAndUnique()
    {
        var a = OAuthPkce.NewCodeVerifier();
        var b = OAuthPkce.NewCodeVerifier();

        Assert.NotEqual(a, b);
        Assert.DoesNotContain('+', a);
        Assert.DoesNotContain('/', a);
        Assert.DoesNotContain('=', a);
        Assert.True(a.Length >= 43);
    }

    // ---- Authorization URL ----

    [Fact]
    public void BuildAuthorizationUrl_SignIn_CarriesClientRedirectStateChallengeAndScopes()
    {
        var url = Build().BuildAuthorizationUrl(
            "https://diariz.example/api/auth/google/callback", "st4te", "chall3nge", GoogleAuthService.SignInScope, offline: false);

        Assert.StartsWith("https://accounts.google.com/o/oauth2/v2/auth?", url);
        Assert.Contains("client_id=cid.apps.googleusercontent.com", url);
        Assert.Contains("response_type=code", url);
        Assert.Contains("code_challenge=chall3nge", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("state=st4te", url);
        Assert.Contains("scope=openid", url);
        Assert.Contains("access_type=online", url);
        Assert.DoesNotContain("include_granted_scopes", url);
    }

    [Fact]
    public void BuildAuthorizationUrl_Offline_RequestsRefreshTokenAndIncrementalConsent()
    {
        var scope = $"openid email {GoogleAuthService.CalendarReadScope}";
        var url = Build().BuildAuthorizationUrl("https://x/callback", "s", "c", scope, offline: true);

        Assert.Contains("access_type=offline", url);
        Assert.Contains("prompt=consent", url);
        Assert.Contains("include_granted_scopes=true", url);
        Assert.Contains("calendar.readonly", url);
    }

    [Fact]
    public void Enabled_TrueOnlyWhenClientIdAndSecretPresent()
    {
        Assert.True(Build().Enabled);
        Assert.False(Build(new GoogleAuthOptions { ClientId = "cid" }).Enabled); // no secret
        Assert.False(Build(new GoogleAuthOptions()).Enabled);
    }

    // ---- Token exchange error surfacing ----

    [Fact]
    public async Task ExchangeCodeAsync_OnGoogleError_SurfacesTheErrorBody()
    {
        // Google returns e.g. 401 {"error":"invalid_client"} — the exception must carry that so it's logged.
        var svc = Build(handler: new StubHandler(HttpStatusCode.Unauthorized, "{\"error\":\"invalid_client\"}"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ExchangeCodeAsync("code", "verifier", "https://x/callback"));

        Assert.Contains("401", ex.Message);
        Assert.Contains("invalid_client", ex.Message);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}
