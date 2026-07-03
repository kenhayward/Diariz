using Diariz.Api.Configuration;
using Diariz.Domain;
using OpenIddict.Abstractions;
using OpenIddict.Server;

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
        this IServiceCollection services, McpOAuthOptions options, string? issuer, string? keysDir,
        bool isDevelopment, string resource)
    {
        services.AddOpenIddict()
            .AddCore(o => o.UseEntityFrameworkCore().UseDbContext<DiarizDbContext>())
            .AddServer(o =>
            {
                o.SetAuthorizationEndpointUris("connect/authorize")
                 .SetTokenEndpointUris("connect/token");
                // Serve discovery at both the OIDC and the RFC 8414 OAuth well-known paths (MCP clients probe
                // either). OpenIddict has no native DCR endpoint, so advertise our hand-rolled one so a client
                // knows where to register.
                o.SetConfigurationEndpointUris(".well-known/openid-configuration", ".well-known/oauth-authorization-server");
                // Advertise the registration endpoint from the configured issuer (known here), NOT ctx.Issuer -
                // this inline handler can run before OpenIddict has populated ctx.Issuer, which previously left
                // the field silently absent (clients then report "automatic client registration isn't supported").
                var registrationEndpoint = string.IsNullOrWhiteSpace(issuer) ? null : $"{issuer.TrimEnd('/')}/connect/register";
                o.AddEventHandler<OpenIddictServerEvents.HandleConfigurationRequestContext>(b => b.UseInlineHandler(ctx =>
                {
                    var endpoint = registrationEndpoint
                        ?? (ctx.Issuer is not null ? new Uri(ctx.Issuer, "connect/register").AbsoluteUri : null);
                    if (endpoint is not null)
                        ctx.Metadata["registration_endpoint"] = endpoint;
                    return default;
                }));

                o.AllowAuthorizationCodeFlow()
                 .AllowRefreshTokenFlow()
                 .RequireProofKeyForCodeExchange(); // PKCE mandatory; OpenIddict accepts S256 only (never plain)

                // Scopes advertised in metadata; the mcp resource is bound as the token audience at sign-in.
                o.RegisterScopes(McpOAuthOptions.Scope,
                    OpenIddictConstants.Scopes.OpenId, OpenIddictConstants.Scopes.OfflineAccess, OpenIddictConstants.Scopes.Email);

                // Register the MCP resource as a known target so OpenIddict accepts the client's RFC 8707
                // `resource={origin}/mcp` request parameter (it validates requested resources before our
                // authorize handler runs; an unregistered one is rejected as invalid_target / ID2190).
                o.RegisterResources(resource);
                // Don't enforce PER-CLIENT resource permissions. DCR-registered public clients aren't granted an
                // explicit resource permission, so OpenIddict would otherwise reject them (ID2192). There is a
                // single MCP resource and DCR is already gated by the redirect-host allowlist + PKCE + consent,
                // so per-client resource granularity adds no security value here.
                o.IgnoreResourcePermissions();

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

                // Only the AUTHORIZE endpoint is passed through to our own controller (it renders consent + signs
                // the user in). The TOKEN endpoint is NOT passed through: OpenIddict issues the tokens itself from
                // the principal embedded in the authorization code (and refresh token). Enabling token passthrough
                // without a token controller would leave valid code->token exchanges unhandled, so Claude never
                // receives a token ("authorization with the MCP server failed").
                var aspNet = o.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough();
                // The reverse proxy terminates TLS (X-Forwarded-Proto=https), so production sees https. On a plain
                // http://localhost dev run there is no TLS, so relax OpenIddict's HTTPS requirement there only.
                if (isDevelopment)
                    aspNet.DisableTransportSecurityRequirement();
            })
            // Resource server: validate our own access tokens in-process (imports the server's keys), requiring
            // the MCP resource as the audience so a token minted for anything else is rejected. Used by the
            // /mcp bearer handler to accept an OAuth token alongside the static dz_mcp_ token.
            .AddValidation(o =>
            {
                o.UseLocalServer();
                o.AddAudiences(resource);
                o.UseAspNetCore();
            });

        return services;
    }
}
