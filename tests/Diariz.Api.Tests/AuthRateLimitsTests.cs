using System.Net;
using Diariz.Api.Auth;
using Microsoft.AspNetCore.Http;

namespace Diariz.Api.Tests;

/// <summary>Per-address request budgets on the anonymous sign-in and OAuth routes. Everything else is unlimited.</summary>
public class AuthRateLimitsTests
{
    private static HttpContext Request(string method, string path, string ip = "203.0.113.7")
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.Request.Path = path;
        http.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        return http;
    }

    [Theory]
    [InlineData("POST", "/api/auth/login", AuthRateLimits.SignIn)]
    [InlineData("POST", "/api/auth/request-access", AuthRateLimits.SignIn)]
    [InlineData("GET", "/api/auth/setup/validate", AuthRateLimits.SignIn)]
    [InlineData("POST", "/api/auth/setup", AuthRateLimits.SignIn)]
    [InlineData("GET", "/api/auth/google/start", AuthRateLimits.SignIn)]
    [InlineData("GET", "/api/auth/google/callback", AuthRateLimits.SignIn)]
    [InlineData("POST", "/api/auth/google/exchange", AuthRateLimits.SignIn)]
    [InlineData("POST", "/api/auth/desktop/exchange", AuthRateLimits.SignIn)]
    [InlineData("POST", "/API/Auth/Login", AuthRateLimits.SignIn)]          // routing is case-insensitive, so is this
    [InlineData("GET", "/connect/authorize", AuthRateLimits.OAuth)]
    [InlineData("POST", "/connect/token", AuthRateLimits.OAuth)]
    [InlineData("POST", "/connect/register", AuthRateLimits.Registration)]
    public void TheAnonymousAuthRoutes_EachHaveABudget(string method, string path, string expected) =>
        Assert.Equal(expected, AuthRateLimits.BudgetFor(Request(method, path)));

    [Theory]
    [InlineData("POST", "/api/auth/refresh")]       // authenticated: a signed-in client refreshes on a timer
    [InlineData("GET", "/api/auth/providers")]      // the login page reads it on every render
    [InlineData("GET", "/api/recordings")]
    [InlineData("POST", "/mcp")]
    [InlineData("GET", "/health")]
    [InlineData("GET", "/api/auth/loginx")]         // a prefix of a limited route is not that route
    public void EverythingElse_IsUnlimited(string method, string path) =>
        Assert.Null(AuthRateLimits.BudgetFor(Request(method, path)));

    [Fact]
    public void AnAddressThatSpendsItsBudget_IsRefused_UntilTheWindowResets()
    {
        using var limiter = AuthRateLimits.CreateLimiter(new AuthRateLimitOptions { SignInPerMinute = 3 });

        for (var i = 0; i < 3; i++)
            Assert.True(limiter.AttemptAcquire(Request("POST", "/api/auth/login")).IsAcquired);

        Assert.False(limiter.AttemptAcquire(Request("POST", "/api/auth/login")).IsAcquired);
    }

    [Fact]
    public void BudgetsAreKeptPerAddress()
    {
        using var limiter = AuthRateLimits.CreateLimiter(new AuthRateLimitOptions { SignInPerMinute = 1 });

        Assert.True(limiter.AttemptAcquire(Request("POST", "/api/auth/login", "203.0.113.7")).IsAcquired);
        Assert.False(limiter.AttemptAcquire(Request("POST", "/api/auth/login", "203.0.113.7")).IsAcquired);
        Assert.True(limiter.AttemptAcquire(Request("POST", "/api/auth/login", "198.51.100.4")).IsAcquired);
    }

    [Fact]
    public void BudgetsAreKeptPerRouteGroup()
    {
        using var limiter = AuthRateLimits.CreateLimiter(new AuthRateLimitOptions { SignInPerMinute = 1, OAuthPerMinute = 1 });

        Assert.True(limiter.AttemptAcquire(Request("POST", "/api/auth/login")).IsAcquired);
        Assert.True(limiter.AttemptAcquire(Request("POST", "/connect/token")).IsAcquired);
    }

    [Fact]
    public void AnUnlimitedRoute_IsNeverRefused()
    {
        using var limiter = AuthRateLimits.CreateLimiter(new AuthRateLimitOptions { SignInPerMinute = 1 });

        for (var i = 0; i < 100; i++)
            Assert.True(limiter.AttemptAcquire(Request("GET", "/api/recordings")).IsAcquired);
    }

    [Fact]
    public void RegistrationHasItsOwnHourlyBudget()
    {
        using var limiter = AuthRateLimits.CreateLimiter(new AuthRateLimitOptions { RegistrationsPerHour = 2 });

        Assert.True(limiter.AttemptAcquire(Request("POST", "/connect/register")).IsAcquired);
        Assert.True(limiter.AttemptAcquire(Request("POST", "/connect/register")).IsAcquired);
        Assert.False(limiter.AttemptAcquire(Request("POST", "/connect/register")).IsAcquired);
    }

    [Fact]
    public void Disabled_LimitsNothing()
    {
        using var limiter = AuthRateLimits.CreateLimiter(new AuthRateLimitOptions { Enabled = false, SignInPerMinute = 1 });

        for (var i = 0; i < 10; i++)
            Assert.True(limiter.AttemptAcquire(Request("POST", "/api/auth/login")).IsAcquired);
    }
}
