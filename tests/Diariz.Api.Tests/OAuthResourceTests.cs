using System.Text.Json;
using Diariz.Api.Auth;

namespace Diariz.Api.Tests;

public class OAuthResourceTests
{
    [Fact]
    public void Resolve_DerivesTheCanonicalMcpUrlFromTheIssuer()
    {
        // The MCP resource identifier / token audience is the canonical /mcp endpoint URL (RFC 8707).
        Assert.Equal("https://diariz.example.com/mcp", OAuthResource.Resolve("https://diariz.example.com", null));
        Assert.Equal("https://diariz.example.com/mcp", OAuthResource.Resolve("https://diariz.example.com/", ""));
    }

    [Fact]
    public void Resolve_PrefersAnExplicitOverride()
    {
        Assert.Equal("urn:diariz:mcp", OAuthResource.Resolve("https://diariz.example.com", "urn:diariz:mcp"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_FallsBackWhenNoIssuer(string? issuer) =>
        Assert.Equal("diariz-mcp", OAuthResource.Resolve(issuer, null));

    [Fact]
    public void ProtectedResourceMetadata_HasTheRfc9728Fields()
    {
        var doc = OAuthResource.ProtectedResourceMetadata("https://d.example/mcp", "https://d.example");
        var json = JsonSerializer.SerializeToElement(doc);

        Assert.Equal("https://d.example/mcp", json.GetProperty("resource").GetString());
        Assert.Equal("https://d.example", json.GetProperty("authorization_servers")[0].GetString());
        Assert.Equal("mcp", json.GetProperty("scopes_supported")[0].GetString());
        Assert.Equal("header", json.GetProperty("bearer_methods_supported")[0].GetString());
    }
}
