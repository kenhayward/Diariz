using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Diariz.Api.Configuration;
using Diariz.Api.Services;
using Diariz.Domain.Entities;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

public class TokenServiceTests
{
    private static TokenService Create(JwtOptions? opts = null) =>
        new(Options.Create(opts ?? new JwtOptions
        {
            Issuer = "diariz",
            Audience = "diariz",
            Key = "test-signing-key-at-least-32-bytes-long!!",
            AccessTokenMinutes = 60
        }));

    [Fact]
    public void CreateAccessToken_PutsUserIdInNameIdentifierClaim()
    {
        var user = new ApplicationUser { Id = Guid.NewGuid(), Email = "a@b.test" };

        var (token, _) = Create().CreateAccessToken(user, []);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        var nameId = jwt.Claims.First(c => c.Type == ClaimTypes.NameIdentifier).Value;
        Assert.Equal(user.Id.ToString(), nameId);
    }

    [Fact]
    public void CreateAccessToken_IncludesRoleClaimsAndFullName()
    {
        var user = new ApplicationUser { Id = Guid.NewGuid(), Email = "a@b.test", FullName = "Ada Lovelace" };

        var (token, _) = Create().CreateAccessToken(user, [Roles.Administrator, Roles.Standard]);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        var roles = jwt.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value).ToList();
        Assert.Contains(Roles.Administrator, roles);
        Assert.Contains(Roles.Standard, roles);
        Assert.Equal("Ada Lovelace", jwt.Claims.First(c => c.Type == "name").Value);
    }

    [Fact]
    public void CreateAccessToken_ExpiryHonoursConfiguredMinutes()
    {
        var before = DateTimeOffset.UtcNow;

        var (_, expiresAt) = Create(new JwtOptions { Key = new string('k', 40), AccessTokenMinutes = 60 })
            .CreateAccessToken(new ApplicationUser { Id = Guid.NewGuid() }, []);

        // ~60 minutes out, allowing a little slack for execution time.
        Assert.InRange(expiresAt, before.AddMinutes(59), before.AddMinutes(61));
    }
}
