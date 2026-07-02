using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Diariz.Api.Configuration;
using Diariz.Domain.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Diariz.Api.Services;

public interface ITokenService
{
    (string token, DateTimeOffset expiresAt) CreateAccessToken(ApplicationUser user, IEnumerable<string> roles);
}

public class TokenService : ITokenService
{
    private readonly JwtOptions _opts;
    public TokenService(IOptions<JwtOptions> opts) => _opts = opts.Value;

    public (string token, DateTimeOffset expiresAt) CreateAccessToken(ApplicationUser user, IEnumerable<string> roles)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(_opts.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("name", user.FullName ?? string.Empty),
        };
        // Profile picture (from a linked Google account) — lets the SPA render the avatar straight from the
        // token. Only present when set, so password-only accounts fall back to initials.
        if (!string.IsNullOrEmpty(user.PictureUrl))
            claims.Add(new Claim("picture", user.PictureUrl));
        foreach (var role in roles)
            claims.Add(new Claim(ClaimTypes.Role, role));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opts.Key));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var jwt = new JwtSecurityToken(
            issuer: _opts.Issuer,
            audience: _opts.Audience,
            claims: claims,
            expires: expiresAt.UtcDateTime,
            signingCredentials: creds);

        return (new JwtSecurityTokenHandler().WriteToken(jwt), expiresAt);
    }
}
