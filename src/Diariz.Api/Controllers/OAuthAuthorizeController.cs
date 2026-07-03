using System.Security.Claims;
using Diariz.Api.Auth;
using Diariz.Api.Configuration;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore; // OpenIddictServerAspNetCoreHelpers.GetOpenIddictServerRequest(HttpContext)
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Diariz.Api.Controllers;

/// <summary>The OAuth 2.1 authorization endpoint (<c>/connect/authorize</c>). Reached by a top-level browser
/// navigation from the OAuth client (e.g. claude.ai), which carries no Diariz session, so the user's identity
/// and consent are supplied by a short-lived, encrypted cookie set by the SPA consent screen
/// (<see cref="IOAuthConsentTicketProtector"/>):
/// <list type="bullet">
/// <item>no/invalid ticket → redirect the browser to the SPA <c>/oauth/consent</c> route, preserving the
/// original authorize query, so the user (signed in with the normal JWT) can approve;</item>
/// <item>an <em>allow</em> ticket for this client + an active user → issue the authorization code (SignIn);</item>
/// <item>a <em>deny</em> ticket → return <c>access_denied</c> to the client.</item>
/// </list>
/// OpenIddict has already validated the request (client, redirect URI, PKCE) before this runs.</summary>
[ApiController]
[AllowAnonymous]
public sealed class OAuthAuthorizeController : ControllerBase
{
    private readonly IOAuthConsentTicketProtector _consent;
    private readonly UserManager<ApplicationUser> _users;
    private readonly McpResourceIdentifier _resource;

    public OAuthAuthorizeController(
        IOAuthConsentTicketProtector consent, UserManager<ApplicationUser> users, McpResourceIdentifier resource)
    {
        _consent = consent;
        _users = users;
        _resource = resource;
    }

    [HttpGet("~/connect/authorize")]
    [HttpPost("~/connect/authorize")]
    public async Task<IActionResult> Authorize()
    {
        var request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenIddict request cannot be retrieved.");
        var clientId = request.ClientId ?? "";

        var decision = _consent.Verify(Request.Cookies[OAuthConsentTicketProtector.CookieName], clientId, Now);
        if (decision is null)
        {
            // No decision yet. A silent (prompt=none) request can't show UI, so it must fail per OIDC.
            if (request.HasPromptValue(PromptValues.None))
                return Deny(Errors.ConsentRequired, "Interactive consent is required.");

            // Send the browser to the SPA consent screen, preserving the exact authorize request so it can be
            // replayed verbatim once the user approves. The SPA is served same-origin, so a relative path works.
            return Redirect($"/oauth/consent{Request.QueryString}");
        }

        // The ticket is single-use: clear it so a back-navigation can't silently re-authorize.
        Response.Cookies.Delete(OAuthConsentTicketProtector.CookieName);

        if (!decision.Allow)
            return Deny(Errors.AccessDenied, "The user denied the authorization request.");

        // Re-check the user is still allowed to sign in (the ticket is short-lived, but be defensive).
        var user = await _users.FindByIdAsync(decision.UserId.ToString());
        if (user is null || user.Status != UserStatus.Active || !user.IsEnabled)
            return Deny(Errors.AccessDenied, "The account is not active.");

        var identity = new ClaimsIdentity(
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, user.Id.ToString());
        identity.SetClaim(Claims.Email, user.Email);
        identity.SetClaim(Claims.Name, user.FullName ?? user.Email);

        // Grant only the scopes the client asked for (OpenIddict already checked they're permitted), and bind
        // the MCP resource as the access-token audience so the /mcp resource server accepts it.
        identity.SetScopes(request.GetScopes());
        identity.SetResources(_resource.Value);
        identity.SetDestinations(GetDestinations);

        return SignIn(new ClaimsPrincipal(identity), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private IActionResult Deny(string error, string description) =>
        Forbid(
            new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
                [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
            }),
            [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);

    /// <summary>Which token(s) each claim is emitted into. The subject is in both tokens; email/name follow
    /// their scopes; everything else stays in the access token only.</summary>
    private static IEnumerable<string> GetDestinations(Claim claim)
    {
        switch (claim.Type)
        {
            case Claims.Subject:
                yield return Destinations.AccessToken;
                yield return Destinations.IdentityToken;
                yield break;

            case Claims.Email:
                yield return Destinations.AccessToken;
                if (claim.Subject!.HasScope(Scopes.Email)) yield return Destinations.IdentityToken;
                yield break;

            case Claims.Name:
                yield return Destinations.AccessToken;
                if (claim.Subject!.HasScope(Scopes.Profile)) yield return Destinations.IdentityToken;
                yield break;

            default:
                yield return Destinations.AccessToken;
                yield break;
        }
    }

    // Overridable seam is unnecessary; a plain property keeps the handler testable via the cookie protector.
    private static DateTimeOffset Now => DateTimeOffset.UtcNow;
}
