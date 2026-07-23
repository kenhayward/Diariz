using System.Security.Claims;
using Diariz.Domain.Entities;

namespace Diariz.Api.Auth;

/// <summary>Pure rule: a read-only token may not perform an unsafe (state-changing) HTTP verb.</summary>
public static class ApiTokenScopePolicy
{
    public static bool BlocksWrite(string method, ApiTokenScope scope) =>
        scope == ApiTokenScope.ReadOnly
        && (HttpMethods.IsPost(method) || HttpMethods.IsPut(method)
            || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method));
}

/// <summary>Rejects unsafe verbs from a read-only <c>dz_api_</c> token with 403. JWT/browser sessions carry no
/// scope claim and are unaffected. Runs after authentication so <c>context.User</c> is populated.</summary>
public sealed class ApiTokenScopeMiddleware
{
    private readonly RequestDelegate _next;
    public ApiTokenScopeMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var scopeClaim = context.User.FindFirstValue(ApiKeyAuthenticationHandler.ScopeClaimType);
        if (scopeClaim is not null
            && Enum.TryParse<ApiTokenScope>(scopeClaim, out var scope)
            && ApiTokenScopePolicy.BlocksWrite(context.Request.Method, scope))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "This API token is read-only." });
            return;
        }
        await _next(context);
    }
}
