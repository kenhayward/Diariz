using System.Security.Claims;
using System.Text.Encodings.Web;
using Diariz.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Auth;

/// <summary>Options for the MCP bearer scheme (no extra settings; present so the scheme can be registered).</summary>
public sealed class McpAuthSchemeOptions : AuthenticationSchemeOptions;

/// <summary>Authenticates the <c>/mcp</c> endpoint with a personal access token presented as
/// <c>Authorization: Bearer dz_mcp_…</c>. Delegates verification to <see cref="IMcpTokenAuthenticator"/> and,
/// on success, produces a principal with the owner's <see cref="ClaimTypes.NameIdentifier"/> — the same claim
/// every user-scoped query already reads, so ownership checks work unchanged. This scheme is separate from the
/// JWT session used by the browser.</summary>
public sealed class McpBearerAuthenticationHandler : AuthenticationHandler<McpAuthSchemeOptions>
{
    public const string SchemeName = "Mcp";
    private const string Prefix = "Bearer ";

    private readonly IMcpTokenAuthenticator _authenticator;

    public McpBearerAuthenticationHandler(
        IOptionsMonitor<McpAuthSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
        IMcpTokenAuthenticator authenticator)
        : base(options, logger, encoder)
    {
        _authenticator = authenticator;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? header = Request.Headers.Authorization;
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult(); // let the challenge produce a 401

        var token = header[Prefix.Length..].Trim();
        var userId = await _authenticator.AuthenticateAsync(token, Context.RequestAborted);
        if (userId is null)
            return AuthenticateResult.Fail("Invalid MCP access token.");

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString())], SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return AuthenticateResult.Success(ticket);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}
