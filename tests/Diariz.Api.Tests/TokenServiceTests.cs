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

        var (token, _) = Create().CreateAccessToken(user);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        var nameId = jwt.Claims.First(c => c.Type == ClaimTypes.NameIdentifier).Value;
        Assert.Equal(user.Id.ToString(), nameId);
    }

    /// <summary>Authority must never ride in the token: a role or permission claim keeps working until the token
    /// expires, so removing a user from a group would not take effect. Permissions are read from the database on
    /// every request instead, and surfaced to the web on the profile.</summary>
    [Fact]
    public void CreateAccessToken_CarriesNoAuthorityClaims()
    {
        var user = new ApplicationUser { Id = Guid.NewGuid(), Email = "a@b.test", FullName = "Ada Lovelace" };

        var (token, _) = Create().CreateAccessToken(user);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Empty(jwt.Claims.Where(c => c.Type == ClaimTypes.Role));
        Assert.Equal("Ada Lovelace", jwt.Claims.First(c => c.Type == "name").Value);
    }

    [Fact]
    public void CreateAccessToken_IncludesPictureClaim_OnlyWhenSet()
    {
        var withPic = new ApplicationUser { Id = Guid.NewGuid(), Email = "a@b.test", PictureUrl = "https://pic/1.png" };
        var noPic = new ApplicationUser { Id = Guid.NewGuid(), Email = "a@b.test" };

        var jwtWith = new JwtSecurityTokenHandler().ReadJwtToken(Create().CreateAccessToken(withPic).token);
        var jwtNo = new JwtSecurityTokenHandler().ReadJwtToken(Create().CreateAccessToken(noPic).token);

        Assert.Equal("https://pic/1.png", jwtWith.Claims.First(c => c.Type == "picture").Value);
        Assert.DoesNotContain(jwtNo.Claims, c => c.Type == "picture");
    }

    [Fact]
    public void CreateAccessToken_ExpiryHonoursConfiguredMinutes()
    {
        var before = DateTimeOffset.UtcNow;

        var (_, expiresAt) = Create(new JwtOptions { Key = new string('k', 40), AccessTokenMinutes = 60 })
            .CreateAccessToken(new ApplicationUser { Id = Guid.NewGuid() });

        // ~60 minutes out, allowing a little slack for execution time.
        Assert.InRange(expiresAt, before.AddMinutes(59), before.AddMinutes(61));
    }
}
