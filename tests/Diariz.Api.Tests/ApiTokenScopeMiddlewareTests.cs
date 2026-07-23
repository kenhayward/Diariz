using System.Security.Claims;
using Diariz.Api.Auth;
using Microsoft.AspNetCore.Http;

namespace Diariz.Api.Tests;

/// <summary>Pipeline-level coverage for <see cref="ApiTokenScopeMiddleware"/>: this repo's integration project
/// has no WebApplicationFactory/HTTP harness (see ApiAccessIntegrationTests), so real block/pass-through
/// behaviour is exercised here directly against a <see cref="DefaultHttpContext"/> instead.</summary>
public class ApiTokenScopeMiddlewareTests
{
    private static DefaultHttpContext ContextWithScopeClaim(string? scope, string method)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        var claims = new List<Claim>();
        if (scope is not null) claims.Add(new Claim(ApiKeyAuthenticationHandler.ScopeClaimType, scope));
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, scope is not null ? "ApiKey" : null));
        return context;
    }

    [Fact]
    public async Task ReadOnlyScope_Post_Returns403_AndDoesNotCallNext()
    {
        var context = ContextWithScopeClaim("ReadOnly", "POST");
        var nextCalled = false;
        var middleware = new ApiTokenScopeMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task ReadOnlyScope_Get_CallsNext()
    {
        var context = ContextWithScopeClaim("ReadOnly", "GET");
        var nextCalled = false;
        var middleware = new ApiTokenScopeMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task NoScopeClaim_Post_CallsNext()
    {
        var context = ContextWithScopeClaim(null, "POST");
        var nextCalled = false;
        var middleware = new ApiTokenScopeMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }
}
