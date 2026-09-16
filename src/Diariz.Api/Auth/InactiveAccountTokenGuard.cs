using Diariz.Api.Services;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace Diariz.Api.Auth;

/// <summary>Refuses to issue tokens at <c>/connect/token</c> for an account that is no longer active.
///
/// <para>The token endpoint is deliberately not passed through to a controller (see <see cref="OpenIddictSetup"/>):
/// OpenIddict reissues access and refresh tokens itself from the principal stored inside the authorization code or
/// refresh token. That principal is a snapshot from consent time, so without this guard a connector whose user was
/// later disabled would keep refreshing indefinitely. This runs on every sign-in the token endpoint makes - both
/// the code exchange and every refresh - and asks the database about the account now.</para>
///
/// <para>The authorization endpoint is left alone: <c>OAuthAuthorizeController</c> already checks the account
/// before it signs in.</para></summary>
public sealed class InactiveAccountTokenGuard(IActiveAccounts accounts) : IOpenIddictServerHandler<ProcessSignInContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; }
        = OpenIddictServerHandlerDescriptor.CreateBuilder<ProcessSignInContext>()
            .UseScopedHandler<InactiveAccountTokenGuard>()
            // Ahead of OpenIddict's own sign-in handlers, so nothing is generated or rolled for a refused account.
            .SetOrder(int.MinValue + 50_000)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    public async ValueTask HandleAsync(ProcessSignInContext context)
    {
        if (context.EndpointType != OpenIddictServerEndpointType.Token) return;

        var subject = context.Principal?.GetClaim(Claims.Subject);
        if (Guid.TryParse(subject, out var userId) && await accounts.IsActiveAsync(userId, context.CancellationToken))
            return;

        context.Reject(
            error: Errors.InvalidGrant,
            description: "The account this grant belongs to is no longer active.");
    }
}
