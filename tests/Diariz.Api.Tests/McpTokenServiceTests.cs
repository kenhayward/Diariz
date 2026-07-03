using System.Text;
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class McpTokenServiceTests
{
    [Fact]
    public void Generate_ProducesPrefixedToken_With32RandomBytes()
    {
        var g = new McpTokenService().Generate();

        Assert.StartsWith("dz_mcp_", g.Token);
        // The part after the prefix is base64url of 32 bytes (43 chars, no padding).
        var body = g.Token["dz_mcp_".Length..];
        Assert.Equal(43, body.Length);
        Assert.DoesNotContain('+', body);
        Assert.DoesNotContain('/', body);
        Assert.DoesNotContain('=', body);
    }

    [Fact]
    public void Generate_DisplayPrefix_IsShortAndNonSecret()
    {
        var g = new McpTokenService().Generate();

        Assert.Equal(13, g.Prefix.Length);               // "dz_mcp_" + 6 chars
        Assert.StartsWith("dz_mcp_", g.Prefix);
        Assert.StartsWith(g.Prefix, g.Token);
        Assert.NotEqual(g.Token, g.Prefix);              // never the whole secret
    }

    [Fact]
    public void Generate_HashMatchesTheToken_AndIsLowercaseHex64()
    {
        var g = new McpTokenService().Generate();

        Assert.Equal(64, g.Hash.Length);
        Assert.All(g.Hash, c => Assert.Contains(c, "0123456789abcdef"));
        Assert.Equal(McpTokenService.Hash(g.Token), g.Hash);
    }

    [Fact]
    public void Generate_ProducesDistinctTokens()
    {
        var svc = new McpTokenService();
        var a = svc.Generate();
        var b = svc.Generate();

        Assert.NotEqual(a.Token, b.Token);
        Assert.NotEqual(a.Hash, b.Hash);
    }

    [Fact]
    public void Hash_IsDeterministic_AndMatchesKnownSha256Vector()
    {
        // SHA-256 of the empty string.
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            McpTokenService.Hash(""));
        Assert.Equal(McpTokenService.Hash("dz_mcp_abc"), McpTokenService.Hash("dz_mcp_abc"));
    }
}
