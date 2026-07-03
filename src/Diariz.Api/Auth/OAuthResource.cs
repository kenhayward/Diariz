using Diariz.Api.Configuration;

namespace Diariz.Api.Auth;

/// <summary>The OAuth resource identifier for the MCP endpoint - the value bound as the access-token
/// <c>aud</c> (RFC 8707) and advertised in the protected-resource metadata (RFC 9728). Per the MCP auth spec,
/// the resource is the canonical <c>/mcp</c> endpoint URL, so both the authorization server (when it issues a
/// token) and the resource server (when it validates one) agree on the same audience. Distinct from
/// <c>Diariz.Api.Mcp.McpResources</c>, which are the in-app <c>diariz://</c> resource URIs.</summary>
public static class OAuthResource
{
    /// <summary>The canonical MCP resource identifier: an explicit override if configured, else
    /// <c>{issuer}/mcp</c>, else the literal fallback (dev without a public origin).</summary>
    public static string Resolve(string? issuer, string? overrideValue)
    {
        if (!string.IsNullOrWhiteSpace(overrideValue)) return overrideValue.Trim();
        if (!string.IsNullOrWhiteSpace(issuer)) return $"{issuer.TrimEnd('/')}/mcp";
        return McpOAuthOptions.Resource;
    }

    /// <summary>The RFC 9728 protected-resource metadata document served at
    /// <c>/.well-known/oauth-protected-resource</c>, telling a client which authorization server protects the
    /// MCP endpoint.</summary>
    public static IDictionary<string, object?> ProtectedResourceMetadata(string resource, string issuer) =>
        new Dictionary<string, object?>
        {
            ["resource"] = resource,
            ["authorization_servers"] = new[] { issuer },
            ["scopes_supported"] = new[] { McpOAuthOptions.Scope },
            ["bearer_methods_supported"] = new[] { "header" },
        };
}

/// <summary>The resolved MCP resource identifier, shared across the app (token issuance, validation audience,
/// and the protected-resource metadata) via DI so all three agree.</summary>
public sealed record McpResourceIdentifier(string Value);
