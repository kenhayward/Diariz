using Diariz.Api.Auth;
using Diariz.Api.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Controllers;

/// <summary>Serves the OAuth 2.0 Protected Resource Metadata (RFC 9728) for the MCP endpoint at
/// <c>/.well-known/oauth-protected-resource</c>. An OAuth client that gets a 401 from <c>/mcp</c> (with the
/// <c>WWW-Authenticate: Bearer resource_metadata="…"</c> header) fetches this to learn which authorization
/// server protects the resource, then runs discovery + registration against it. Anonymous; only present when
/// the OAuth server is enabled.</summary>
[ApiController]
[AllowAnonymous]
public sealed class WellKnownController : ControllerBase
{
    private readonly McpOAuthOptions _oauth;
    private readonly AppPublicOptions _app;
    private readonly McpResourceIdentifier? _resource;

    public WellKnownController(
        IOptions<McpOAuthOptions> oauth, IOptions<AppPublicOptions> app, McpResourceIdentifier? resource = null)
    {
        _oauth = oauth.Value;
        _app = app.Value;
        _resource = resource;
    }

    [HttpGet("~/.well-known/oauth-protected-resource")]
    public IActionResult ProtectedResource()
    {
        if (!_oauth.Enabled || _resource is null) return NotFound();

        // The authorization server is this same origin. Prefer the configured public URL (the OpenIddict issuer);
        // fall back to the request origin (reverse-proxy forwarded headers make this the public https origin).
        var issuer = !string.IsNullOrWhiteSpace(_app.PublicUrl)
            ? _app.PublicUrl.TrimEnd('/')
            : $"{Request.Scheme}://{Request.Host}";

        return Ok(OAuthResource.ProtectedResourceMetadata(_resource.Value, issuer));
    }
}
