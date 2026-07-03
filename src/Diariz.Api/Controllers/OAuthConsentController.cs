using System.Security.Claims;
using Diariz.Api.Auth;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;

namespace Diariz.Api.Controllers;

/// <summary>Backs the SPA OAuth consent screen. Authenticated with the normal JWT session (the user is signed
/// into Diariz), it looks up the requesting client's name and records the user's allow/deny decision in the
/// short-lived, encrypted consent cookie that <see cref="OAuthAuthorizeController"/> reads on the subsequent
/// top-level navigation to <c>/connect/authorize</c>. Splitting it this way lets the browser-redirect authorize
/// step - which carries no Authorization header - still know who consented.</summary>
[ApiController]
[Authorize]
[Route("api/oauth")]
public sealed class OAuthConsentController : ControllerBase
{
    /// <summary>How long the user has to complete the redirect back to /connect/authorize after consenting.</summary>
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(5);

    private readonly IOpenIddictApplicationManager _applications;
    private readonly IOAuthConsentTicketProtector _consent;
    private readonly UserManager<ApplicationUser> _users;

    public OAuthConsentController(
        IOpenIddictApplicationManager applications, IOAuthConsentTicketProtector consent,
        UserManager<ApplicationUser> users)
    {
        _applications = applications;
        _consent = consent;
        _users = users;
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>Details the consent screen shows: the registered display name of the client requesting access.
    /// 404 if the client id is unknown (e.g. a tampered authorize link).</summary>
    [HttpGet("consent-info")]
    public async Task<ActionResult<ConsentInfoDto>> ConsentInfo([FromQuery] string clientId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return BadRequest();
        var app = await _applications.FindByClientIdAsync(clientId, ct);
        if (app is null) return NotFound();
        var name = await _applications.GetDisplayNameAsync(app, ct);
        return new ConsentInfoDto(string.IsNullOrWhiteSpace(name) ? "An application" : name!);
    }

    /// <summary>Record the signed-in user's allow/deny decision for a client into the consent cookie. Gated on
    /// the same account status as login. The SPA then navigates the browser to <c>/connect/authorize</c>, which
    /// reads the cookie back.</summary>
    [HttpPost("consent")]
    public async Task<IActionResult> Consent(ConsentRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ClientId)) return BadRequest();

        var user = await _users.FindByIdAsync(UserId.ToString());
        if (user is null || user.Status != UserStatus.Active || !user.IsEnabled)
            return StatusCode(StatusCodes.Status403Forbidden, "Your account is not active.");

        // The client must be a real, registered application.
        if (await _applications.FindByClientIdAsync(req.ClientId, ct) is null) return NotFound();

        var ticket = _consent.Issue(UserId, req.ClientId, req.Allow, DateTimeOffset.UtcNow.Add(TicketLifetime));
        Response.Cookies.Append(OAuthConsentTicketProtector.CookieName, ticket, new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,       // https in production (behind the TLS-terminating proxy)
            SameSite = SameSiteMode.Lax,    // sent on the same-origin top-level nav back to /connect/authorize
            Path = "/",
            MaxAge = TicketLifetime,
            IsEssential = true,
        });
        return NoContent();
    }
}

public sealed record ConsentInfoDto(string ClientName);
public sealed record ConsentRequest(string ClientId, bool Allow);
