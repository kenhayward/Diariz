using Diariz.Api.Configuration;
using Diariz.Api.Services;
using Diariz.Domain.Entities;
using Microsoft.Extensions.Options;

namespace Diariz.Api.IntegrationTests.Infrastructure;

/// <summary>Mints a JWT access token the same way <see cref="TokenService"/> does at login, signed with
/// <see cref="DiarizWebAppFactory"/>'s test signing key so it validates against a <see cref="DiarizWebAppFactory"/>
/// app-host. The token only needs the user id to exist as a row for anything ownership/room-scoped to work
/// (see <c>Diariz.Api.Tests.Infrastructure.Users.Ensure</c>) - it does not need to be issued through a real
/// login flow.</summary>
public static class TestTokens
{
    public static string Issue(Guid userId, string email = "u@x.test") =>
        new TokenService(Options.Create(new JwtOptions
        {
            Issuer = DiarizWebAppFactory.JwtIssuer,
            Audience = DiarizWebAppFactory.JwtAudience,
            Key = DiarizWebAppFactory.JwtKey,
            AccessTokenMinutes = 60,
        })).CreateAccessToken(new ApplicationUser { Id = userId, Email = email }).token;
}
