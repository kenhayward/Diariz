using Diariz.Api.Configuration;
using Diariz.Api.Services;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

public class GoogleAuthServiceTests
{
    private static GoogleAuthService Build(GoogleAuthOptions? opts = null) =>
        new(new HttpClient(), Options.Create(opts ?? new GoogleAuthOptions
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
    public void BuildAuthorizationUrl_CarriesClientRedirectStateChallengeAndScopes()
    {
        var url = Build().BuildAuthorizationUrl(
            "https://diariz.example/api/auth/google/callback", "st4te", "chall3nge");

        Assert.StartsWith("https://accounts.google.com/o/oauth2/v2/auth?", url);
        Assert.Contains("client_id=cid.apps.googleusercontent.com", url);
        Assert.Contains("response_type=code", url);
        Assert.Contains("code_challenge=chall3nge", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("state=st4te", url);
        // scope "openid email profile" url-encoded (spaces → %20 or +).
        Assert.Contains("scope=openid", url);
        Assert.Contains("redirect_uri=https", url);
    }

    [Fact]
    public void Enabled_TrueOnlyWhenClientIdAndSecretPresent()
    {
        Assert.True(Build().Enabled);
        Assert.False(Build(new GoogleAuthOptions { ClientId = "cid" }).Enabled); // no secret
        Assert.False(Build(new GoogleAuthOptions()).Enabled);
    }
}
