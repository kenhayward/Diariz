using System.Security.Claims;
using Diariz.Api.Auth;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace Diariz.Api.Tests;

/// <summary>The token endpoint is not passed through to our code - OpenIddict reissues tokens itself from the
/// principal stored in the code or refresh token - so this guard is the only place an account's current state is
/// consulted when a connector refreshes.</summary>
public class InactiveAccountTokenGuardTests
{
    private static async Task<(InactiveAccountTokenGuard guard, Guid userId)> Build(bool enabled, UserStatus status = UserStatus.Active)
    {
        var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser
        {
            Id = userId, UserName = $"{userId:N}@x.test", Email = $"{userId:N}@x.test", IsEnabled = enabled, Status = status,
        });
        await db.SaveChangesAsync();
        return (new InactiveAccountTokenGuard(new ActiveAccounts(db)), userId);
    }

    private static ProcessSignInContext SignIn(OpenIddictServerEndpointType endpoint, string? grantType, Guid subject)
    {
        var transaction = new OpenIddictServerTransaction
        {
            EndpointType = endpoint,
            Options = new OpenIddictServerOptions(),
            Logger = NullLogger.Instance,
            Request = new OpenIddictRequest { GrantType = grantType },
        };
        var identity = new ClaimsIdentity("test");
        identity.SetClaim(Claims.Subject, subject.ToString());
        return new ProcessSignInContext(transaction) { Principal = new ClaimsPrincipal(identity) };
    }

    [Theory]
    [InlineData(GrantTypes.RefreshToken)]
    [InlineData(GrantTypes.AuthorizationCode)]
    public async Task ATokenExchange_ForADisabledAccount_IsRejected(string grantType)
    {
        var (guard, userId) = await Build(enabled: false);
        var context = SignIn(OpenIddictServerEndpointType.Token, grantType, userId);

        await guard.HandleAsync(context);

        Assert.True(context.IsRejected);
        Assert.Equal(Errors.InvalidGrant, context.Error);
    }

    [Theory]
    [InlineData(UserStatus.Requested)]
    [InlineData(UserStatus.Invited)]
    public async Task ARefresh_ForAnAccountThatIsNotActive_IsRejected(UserStatus status)
    {
        var (guard, userId) = await Build(enabled: true, status);
        var context = SignIn(OpenIddictServerEndpointType.Token, GrantTypes.RefreshToken, userId);

        await guard.HandleAsync(context);

        Assert.True(context.IsRejected);
    }

    [Fact]
    public async Task ARefresh_ForAnAccountThatNoLongerExists_IsRejected()
    {
        var (guard, _) = await Build(enabled: true);
        var context = SignIn(OpenIddictServerEndpointType.Token, GrantTypes.RefreshToken, Guid.NewGuid());

        await guard.HandleAsync(context);

        Assert.True(context.IsRejected);
    }

    [Fact]
    public async Task ARefresh_ForAnActiveAccount_Proceeds()
    {
        var (guard, userId) = await Build(enabled: true);
        var context = SignIn(OpenIddictServerEndpointType.Token, GrantTypes.RefreshToken, userId);

        await guard.HandleAsync(context);

        Assert.False(context.IsRejected);
    }

    [Fact]
    public async Task TheAuthorizationEndpoint_IsLeftToItsOwnController()
    {
        // OAuthAuthorizeController already checks the account; the guard only covers the token endpoint.
        var (guard, userId) = await Build(enabled: false);
        var context = SignIn(OpenIddictServerEndpointType.Authorization, grantType: null, userId);

        await guard.HandleAsync(context);

        Assert.False(context.IsRejected);
    }
}
