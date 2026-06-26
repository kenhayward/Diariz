using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Diariz.Api.Tests.Infrastructure;

/// <summary>Builds a <see cref="ControllerContext"/> with an optional authenticated user and headers.</summary>
public static class Http
{
    public static ControllerContext Context(Guid? userId = null, params (string Key, string Value)[] headers)
    {
        var ctx = new DefaultHttpContext();
        if (userId is { } id)
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, id.ToString())], authenticationType: "test"));
        foreach (var (key, value) in headers)
            ctx.Request.Headers[key] = value;
        return new ControllerContext { HttpContext = ctx };
    }
}
