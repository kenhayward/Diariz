using Diariz.Api.Auth;

namespace Diariz.Api.Tests;

public class RedirectUriPolicyTests
{
    private static readonly string[] Allowed = ["claude.ai", "claude.com", "localhost", "127.0.0.1"];

    [Theory]
    [InlineData("https://claude.ai/api/mcp/auth_callback")]
    [InlineData("https://claude.com/oauth/callback")]
    [InlineData("https://CLAUDE.AI/cb")]                 // host match is case-insensitive
    [InlineData("http://localhost:51000/callback")]      // loopback may use http (OAuth 2.1)
    [InlineData("http://127.0.0.1:8976/oauth")]          // loopback IPv4 http
    public void IsAllowed_AcceptsHttpsAllowedHosts_AndLoopbackHttp(string uri) =>
        Assert.True(RedirectUriPolicy.IsAllowed(uri, Allowed));

    [Theory]
    [InlineData("https://evil.example.com/cb")]          // host not on the allowlist
    [InlineData("http://claude.ai/cb")]                  // http on a non-loopback host is rejected
    [InlineData("https://claude.ai/cb#fragment")]        // redirect URIs must not carry a fragment
    [InlineData("/relative/callback")]                   // must be absolute
    [InlineData("not a uri")]
    [InlineData("ftp://claude.ai/cb")]                   // only http(s) schemes
    [InlineData("")]
    [InlineData(null)]
    public void IsAllowed_RejectsEverythingElse(string? uri) =>
        Assert.False(RedirectUriPolicy.IsAllowed(uri, Allowed));

    [Fact]
    public void AllAllowed_RequiresEveryUriToPass()
    {
        Assert.True(RedirectUriPolicy.AllAllowed(["https://claude.ai/a", "http://localhost:9/b"], Allowed));
        // One bad URI fails the whole registration.
        Assert.False(RedirectUriPolicy.AllAllowed(["https://claude.ai/a", "https://evil.com/b"], Allowed));
        // An empty set is not a valid registration.
        Assert.False(RedirectUriPolicy.AllAllowed([], Allowed));
    }
}
