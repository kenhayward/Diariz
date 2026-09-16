using System.Security.Claims;
using System.Text.Encodings.Web;
using Diariz.Api.Services;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Auth;

/// <summary>Options for the ApiKey scheme (no extra settings; present so the scheme can be registered).</summary>
public sealed class ApiKeyAuthSchemeOptions : AuthenticationSchemeOptions;

/// <summary>Authenticates a request bearing a personal REST-API token (<c>dz_api_…</c>) as the owning user,
/// carrying the user's id, name and email plus the token's scope. That is session parity for everything the API
/// authorizes on: ownership checks read the id, and platform permissions are resolved from the database by that
/// same id (<see cref="PermissionAuthorizationHandler"/>) - no role claims are emitted or needed. The owner's
/// account must still be enabled and active (checked in <see cref="IApiTokenAuthenticator"/>). Only invoked for
/// <c>dz_api_</c> bearers (routed here by the forwarding default scheme in <c>Program.cs</c>).</summary>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthSchemeOptions>
{
    public const string SchemeName = "ApiKey";

    /// <summary>Claim carrying the token's <see cref="ApiTokenScope"/> (value = enum name). Only ApiKey-authed
    /// principals carry it; the write-block middleware reads it.</summary>
    public const string ScopeClaimType = "diariz:api_scope";

    private const string Prefix = "Bearer ";

    private readonly IApiTokenAuthenticator _authenticator;
    private readonly UserManager<ApplicationUser> _users;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
        IApiTokenAuthenticator authenticator, UserManager<ApplicationUser> users)
        : base(options, logger, encoder)
    {
        _authenticator = authenticator;
        _users = users;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var result = await AuthenticateTokenAsync();
        // A presented credential that was refused is worth a trail: the reason and where it came from, never the
        // token. (The framework's own line for this is Information, below the Microsoft.AspNetCore level we keep.)
        if (result.Failure is { } failure)
            Logger.LogWarning("{Scheme} credential rejected for a request from {RemoteIp}: {Reason}",
                Scheme.Name, Context.Connection.RemoteIpAddress, failure.Message);
        return result;
    }

    private async Task<AuthenticateResult> AuthenticateTokenAsync()
    {
        string? header = Request.Headers.Authorization;
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var token = header[Prefix.Length..].Trim();
        if (!token.StartsWith(ApiTokenService.TokenPrefix, StringComparison.Ordinal))
            return AuthenticateResult.NoResult(); // not our credential; let the challenge produce a 401

        var auth = await _authenticator.AuthenticateAsync(token, Context.RequestAborted);
        if (auth is null) return AuthenticateResult.Fail("Invalid API token or API access is disabled.");

        var user = await _users.FindByIdAsync(auth.UserId.ToString());
        if (user is null || !user.IsEnabled || user.Status != UserStatus.Active)
            return AuthenticateResult.Fail("Token owner no longer exists or is not active.");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, auth.UserId.ToString()),
            new(ClaimTypes.Name, user.FullName ?? user.Email ?? ""),
            new(ClaimTypes.Email, user.Email ?? ""),
            new(ScopeClaimType, auth.Scope.ToString()),
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
