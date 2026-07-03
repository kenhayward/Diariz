using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Diariz.Api.Controllers;

/// <summary>Lists and revokes the signed-in user's OAuth connections - the granted authorizations behind the
/// claude.ai web MCP connector (and any other OAuth client). Each OpenIddict authorization represents one
/// client's standing consent for this user; revoking it deletes the authorization and its tokens so the
/// connection stops working (refresh dies immediately; any issued access token lapses at its short lifetime).
/// Authenticated with the normal JWT session and owner-scoped by subject. Complements
/// <see cref="McpTokensController"/> (the static personal tokens).</summary>
[ApiController]
[Authorize]
[Route("api/oauth/connections")]
public sealed class OAuthConnectionsController : ControllerBase
{
    private readonly IOpenIddictAuthorizationManager _authorizations;
    private readonly IOpenIddictApplicationManager _applications;
    private readonly IOpenIddictTokenManager _tokens;

    public OAuthConnectionsController(
        IOpenIddictAuthorizationManager authorizations, IOpenIddictApplicationManager applications,
        IOpenIddictTokenManager tokens)
    {
        _authorizations = authorizations;
        _applications = applications;
        _tokens = tokens;
    }

    private string Subject => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    [HttpGet]
    public async Task<IReadOnlyList<OAuthConnectionDto>> List(CancellationToken ct)
    {
        var connections = new List<OAuthConnectionDto>();
        await foreach (var auth in _authorizations.FindBySubjectAsync(Subject, ct))
        {
            if (await _authorizations.GetStatusAsync(auth, ct) != Statuses.Valid) continue; // hide revoked

            var appId = await _authorizations.GetApplicationIdAsync(auth, ct);
            string? name = null;
            if (appId is not null && await _applications.FindByIdAsync(appId, ct) is { } app)
                name = await _applications.GetDisplayNameAsync(app, ct);

            connections.Add(new OAuthConnectionDto(
                Id: (await _authorizations.GetIdAsync(auth, ct))!,
                ClientName: string.IsNullOrWhiteSpace(name) ? "Application" : name!,
                ConnectedAt: await _authorizations.GetCreationDateAsync(auth, ct),
                Scopes: [.. await _authorizations.GetScopesAsync(auth, ct)]));
        }
        return connections.OrderByDescending(c => c.ConnectedAt).ToList();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Revoke(string id, CancellationToken ct)
    {
        var auth = await _authorizations.FindByIdAsync(id, ct);
        // 404 (not 403) when it isn't the caller's, so a connection's existence isn't disclosed.
        if (auth is null || await _authorizations.GetSubjectAsync(auth, ct) != Subject) return NotFound();

        await foreach (var token in _tokens.FindByAuthorizationIdAsync(id, ct))
            await _tokens.TryRevokeAsync(token, ct);
        await _authorizations.TryRevokeAsync(auth, ct);
        return NoContent();
    }
}

public sealed record OAuthConnectionDto(string Id, string ClientName, DateTimeOffset? ConnectedAt, string[] Scopes);
