using Diariz.Api.Configuration;
using Diariz.Domain;
using OpenIddict.Abstractions;

namespace Diariz.Api.Auth;

/// <summary>Registers Diariz's OpenIddict OAuth 2.1 authorization server (for the MCP web connector) on a
/// service collection. Extracted from <c>Program.cs</c> so the exact server configuration - flows, PKCE, scope,
/// endpoints, and persistent keys - is defined once and can be exercised in integration tests against a real
/// database. The interactive authorize/consent + token controllers and the <c>/mcp</c> resource-server
/// validation are wired separately (later PRs).</summary>
public static class OpenIddictSetup
{
    /// <summary>Adds the OpenIddict core (EF Core stores on <see cref="DiarizDbContext"/>) + server. When
    /// <paramref name="keysDir"/> is provided, signing/encryption certificates are persisted there (surviving a
    /// container recreate); otherwise ephemeral development certificates are used. <paramref name="issuer"/>, if
    /// set, pins the token issuer to the public origin. Transport security (HTTPS) is relaxed only when
    /// <paramref name="isDevelopment"/> is true (a plain http://localhost dev run).</summary>
    public static IServiceCollection AddDiarizMcpOAuth(
        this IServiceCollection services, McpOAuthOptions options, string? issuer, string? keysDir, bool isDevelopment)
    {
        services.AddOpenIddict()
            .AddCore(o => o.UseEntityFrameworkCore().UseDbContext<DiarizDbContext>())
            .AddServer(o =>
            {
                o.SetAuthorizationEndpointUris("connect/authorize")
                 .SetTokenEndpointUris("connect/token");

                o.AllowAuthorizationCodeFlow()
                 .AllowRefreshTokenFlow()
                 .RequireProofKeyForCodeExchange(); // PKCE mandatory; OpenIddict accepts S256 only (never plain)

                // Scopes advertised in metadata; the mcp resource is bound as the token audience at sign-in.
                o.RegisterScopes(McpOAuthOptions.Scope,
                    OpenIddictConstants.Scopes.OpenId, OpenIddictConstants.Scopes.OfflineAccess, OpenIddictConstants.Scopes.Email);

                if (!string.IsNullOrWhiteSpace(issuer))
                    o.SetIssuer(new Uri(issuer, UriKind.Absolute));

                if (!string.IsNullOrWhiteSpace(keysDir))
                {
                    o.AddSigningCertificate(OpenIddictKeys.LoadOrCreateSigning(keysDir));
                    o.AddEncryptionCertificate(OpenIddictKeys.LoadOrCreateEncryption(keysDir));
                }
                else
                {
                    o.AddDevelopmentSigningCertificate();
                    o.AddDevelopmentEncryptionCertificate();
                }

                var aspNet = o.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough() // our own controllers handle authorize/token (later PR)
                    .EnableTokenEndpointPassthrough();
                // The reverse proxy terminates TLS (X-Forwarded-Proto=https), so production sees https. On a plain
                // http://localhost dev run there is no TLS, so relax OpenIddict's HTTPS requirement there only.
                if (isDevelopment)
                    aspNet.DisableTransportSecurityRequirement();
            });

        return services;
    }
}
