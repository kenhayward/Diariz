using System.Security.Claims;
using System.Text.Encodings.Web;
using Diariz.Api.Configuration;
using Diariz.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;

namespace Diariz.Api.Auth;

/// <summary>Options for the MCP bearer scheme (no extra settings; present so the scheme can be registered).</summary>
public sealed class McpAuthSchemeOptions : AuthenticationSchemeOptions;

/// <summary>Authenticates the <c>/mcp</c> endpoint with a bearer token, accepting either credential:
/// <list type="bullet">
/// <item>a <b>static personal access token</b> (<c>dz_mcp_…</c>) - verified by <see cref="IMcpTokenAuthenticator"/>
/// (Claude Desktop/Code/CI);</item>
/// <item>an <b>OAuth 2.1 access token</b> - validated by the in-process OpenIddict validation scheme
/// (claude.ai web + the OAuth path on Desktop/Code).</item>
/// </list>
/// Either way it produces a principal carrying the owner's <see cref="ClaimTypes.NameIdentifier"/> - the same
/// claim every user-scoped query reads - so ownership checks work unchanged (OpenIddict exposes the id as the
/// short <c>sub</c> claim, which this bridges to <c>NameIdentifier</c>). The 401 challenge advertises the
/// protected-resource metadata (RFC 9728) so an OAuth client can discover how to authenticate.</summary>
public sealed class McpBearerAuthenticationHandler : AuthenticationHandler<McpAuthSchemeOptions>
{
    public const string SchemeName = "Mcp";
    private const string Prefix = "Bearer ";

    private readonly IMcpTokenAuthenticator _authenticator;
    private readonly IAuthenticationSchemeProvider _schemes;
    private readonly IPlatformSettingsService _platform;

    public McpBearerAuthenticationHandler(
        IOptionsMonitor<McpAuthSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
        IMcpTokenAuthenticator authenticator, IAuthenticationSchemeProvider schemes, IPlatformSettingsService platform)
        : base(options, logger, encoder)
    {
        _authenticator = authenticator;
        _schemes = schemes;
        _platform = platform;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? header = Request.Headers.Authorization;
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult(); // let the challenge produce a 401

        var token = header[Prefix.Length..].Trim();

        // Runtime platform kill-switch: no MCP credential authenticates while the feature is off. Checked
        // before both the static-token and OAuth branches below so it covers both credential paths.
        var settings = await _platform.GetAsync(Context.RequestAborted);
        if (!settings.McpAccessEnabled) return AuthenticateResult.Fail("MCP access is disabled.");

        // A static personal access token is self-identifying by its prefix.
        if (token.StartsWith(McpTokenService.TokenPrefix, StringComparison.Ordinal))
        {
            var userId = await _authenticator.AuthenticateAsync(token, Context.RequestAborted);
            return userId is null
                ? AuthenticateResult.Fail("Invalid MCP access token.")
                : Success(userId.Value);
        }

        // Otherwise treat it as an OAuth access token, validated by OpenIddict (only registered when the OAuth
        // server is enabled). Audience + issuer are checked by the validation handler.
        if (await _schemes.GetSchemeAsync(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme) is null)
            return AuthenticateResult.Fail("Invalid MCP access token.");

        var oauth = await Context.AuthenticateAsync(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        if (!oauth.Succeeded || oauth.Principal is null)
            return AuthenticateResult.Fail("Invalid OAuth access token.");
        if (!oauth.Principal.HasScope(McpOAuthOptions.Scope))
            return AuthenticateResult.Fail("The access token is missing the mcp scope.");

        var subject = oauth.Principal.FindFirst(OpenIddictConstants.Claims.Subject)?.Value
            ?? oauth.Principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(subject, out var owner)
            ? Success(owner)
            : AuthenticateResult.Fail("The access token has no valid subject.");
    }

    private AuthenticateResult Success(Guid userId)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        // RFC 9728: point OAuth clients at the protected-resource metadata for this origin so they can discover
        // the authorization server. The request scheme/host reflect the public origin (forwarded headers).
        var prm = $"{Request.Scheme}://{Request.Host}/.well-known/oauth-protected-resource";
        Response.Headers.WWWAuthenticate = $"Bearer resource_metadata=\"{prm}\"";
        return Task.CompletedTask;
    }
}
