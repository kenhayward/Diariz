using System.Text.Json;
using Diariz.Api.Auth;
using Diariz.Api.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Diariz.Api.Controllers;

/// <summary>OAuth 2.0 Dynamic Client Registration (RFC 7591) for MCP clients. OpenIddict 7.x has no native DCR
/// endpoint, so this hand-rolls it: an MCP client (claude.ai / Claude Desktop / Code) POSTs its redirect URIs
/// and gets back a <c>client_id</c> for a public, PKCE-only authorization-code client. The endpoint is
/// anonymous by necessity (the client has no credentials yet); the security boundary is
/// <see cref="RedirectUriPolicy"/> - a registration is rejected unless every <c>redirect_uri</c> host is on the
/// configured allowlist, so a client can never be registered to redirect a user's authorization code to an
/// attacker-controlled site.</summary>
[ApiController]
[AllowAnonymous]
[Route("connect/register")]
public sealed class OAuthRegistrationController : ControllerBase
{
    private readonly IOpenIddictApplicationManager _applications;
    private readonly McpOAuthOptions _options;

    public OAuthRegistrationController(IOpenIddictApplicationManager applications, IOptions<McpOAuthOptions> options)
    {
        _applications = applications;
        _options = options.Value;
    }

    [HttpPost]
    [Consumes("application/json")]
    public async Task<IActionResult> Register([FromBody] JsonElement body, CancellationToken ct)
    {
        var redirectUris = ReadStringArray(body, "redirect_uris");
        if (!RedirectUriPolicy.AllAllowed(redirectUris, _options.AllowedRedirectHosts))
            // RFC 7591 §3.2.2 error for an unacceptable redirect URI.
            return BadRequest(new { error = "invalid_redirect_uri", error_description = "A redirect_uri is missing or not permitted." });

        var clientName = ReadString(body, "client_name") ?? "MCP client";
        if (clientName.Length > 200) clientName = clientName[..200];

        var clientId = Guid.NewGuid().ToString("N");
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientType = ClientTypes.Public,   // no secret; the client authenticates the code exchange with PKCE
            ConsentType = ConsentTypes.Explicit, // always show the Diariz consent screen
            DisplayName = clientName,
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
                Permissions.Prefixes.Scope + Scopes.OpenId,
                Permissions.Prefixes.Scope + Scopes.Email,
                Permissions.Prefixes.Scope + Scopes.OfflineAccess,
                Permissions.Prefixes.Scope + McpOAuthOptions.Scope,
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange }, // PKCE is mandatory for this client
        };
        foreach (var uri in redirectUris)
            descriptor.RedirectUris.Add(new Uri(uri, UriKind.Absolute));

        await _applications.CreateAsync(descriptor, ct);

        // RFC 7591 §3.2.1 client information response. Public client -> no client_secret.
        return Created(string.Empty, new Dictionary<string, object?>
        {
            ["client_id"] = clientId,
            ["client_id_issued_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["client_name"] = clientName,
            ["redirect_uris"] = redirectUris,
            ["token_endpoint_auth_method"] = "none",
            ["grant_types"] = new[] { "authorization_code", "refresh_token" },
            ["response_types"] = new[] { "code" },
            ["scope"] = $"{Scopes.OpenId} {Scopes.Email} {Scopes.OfflineAccess} {McpOAuthOptions.Scope}",
        });
    }

    private static string[] ReadStringArray(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object
        && body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray()
            : [];

    private static string? ReadString(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object
        && body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
