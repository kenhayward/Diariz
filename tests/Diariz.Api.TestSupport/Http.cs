using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Diariz.Api.Tests.Infrastructure;

/// <summary>Builds a <see cref="ControllerContext"/> with an optional authenticated user and headers.</summary>
public static class Http
{
    public static ControllerContext Context(Guid? userId = null, params (string Key, string Value)[] headers) =>
        Context(userId, roles: null, headers);

    /// <summary>Builds a controller context with an authenticated user, optional role claims, and headers.</summary>
    public static ControllerContext Context(Guid? userId, string[]? roles, params (string Key, string Value)[] headers)
    {
        var ctx = new DefaultHttpContext();
        if (userId is { } id)
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, id.ToString()) };
            foreach (var role in roles ?? [])
                claims.Add(new Claim(ClaimTypes.Role, role));
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test"));
        }
        foreach (var (key, value) in headers)
            ctx.Request.Headers[key] = value;
        return new ControllerContext { HttpContext = ctx };
    }
}
