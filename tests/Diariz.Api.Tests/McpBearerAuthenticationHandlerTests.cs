using System.Security.Claims;
using System.Text.Encodings.Web;
using Diariz.Api.Auth;
using Diariz.Api.Configuration;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;

namespace Diariz.Api.Tests;

/// <summary>The <c>/mcp</c> bearer scheme driven directly: which principals it accepts, and what its 401
/// challenge advertises. The OAuth branch delegates to OpenIddict's validation scheme, which is stood in for by a
/// fake <see cref="IAuthenticationService"/> - the handler only ever sees its result.</summary>
public class McpBearerAuthenticationHandlerTests
{
    private sealed class StaticMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class FixedMcpTokens(Guid? owner) : IMcpTokenAuthenticator
    {
        public Task<Guid?> AuthenticateAsync(string? token, CancellationToken ct) => Task.FromResult(owner);
    }

    /// <summary>Stands in for OpenIddict validation: always "validates" and returns the given principal.</summary>
    private sealed class FakeOAuthValidation(ClaimsPrincipal principal) : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, scheme!)));
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    }

    private sealed class NoopHandler : IAuthenticationHandler
    {
        public Task InitializeAsync(AuthenticationScheme scheme, HttpContext context) => Task.CompletedTask;
        public Task<AuthenticateResult> AuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
        public Task ChallengeAsync(AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(AuthenticationProperties? properties) => Task.CompletedTask;
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public CapturingLogger<McpBearerAuthenticationHandler> Logger { get; } = new();
        public ILogger CreateLogger(string categoryName) => Logger;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }

    private static ClaimsPrincipal OAuthPrincipal(Guid subject, string scope = McpOAuthOptions.Scope)
    {
        var identity = new ClaimsIdentity("oauth");
        identity.SetClaim(OpenIddictConstants.Claims.Subject, subject.ToString());
        identity.SetScopes(scope);
        return new ClaimsPrincipal(identity);
    }

    private static async Task<(McpBearerAuthenticationHandler handler, HttpContext http)> Build(
        string bearer, Guid? staticTokenOwner = null, ClaimsPrincipal? oauth = null,
        bool active = true, string publicUrl = "https://diariz.example.com", bool seedUsers = true,
        ILoggerFactory? loggers = null)
    {
        var db = TestDb.Create();
        var ownerId = staticTokenOwner ?? Guid.NewGuid();
        var subject = oauth?.FindFirst(OpenIddictConstants.Claims.Subject)?.Value;
        foreach (var id in new[] { ownerId.ToString(), subject }.Where(v => seedUsers && v is not null).Distinct())
            db.Users.Add(new ApplicationUser
            {
                Id = Guid.Parse(id!), UserName = $"{id}@x.test", Email = $"{id}@x.test",
                IsEnabled = active, Status = UserStatus.Active,
            });
        await db.SaveChangesAsync();

        var schemes = new AuthenticationSchemeProvider(Options.Create(new AuthenticationOptions()));
        schemes.AddScheme(new AuthenticationScheme(
            OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, null, typeof(NoopHandler)));

        var services = new ServiceCollection();
        services.AddSingleton<IAuthenticationService>(new FakeOAuthValidation(oauth ?? new ClaimsPrincipal()));
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        http.Request.Headers.Authorization = $"Bearer {bearer}";

        var handler = new McpBearerAuthenticationHandler(
            new StaticMonitor<McpAuthSchemeOptions>(new McpAuthSchemeOptions()), loggers ?? NullLoggerFactory.Instance,
            UrlEncoder.Default, new FixedMcpTokens(staticTokenOwner), schemes,
            new FixedPlatformSettings(db, new PlatformSettings { McpAccessEnabled = true }),
            new ActiveAccounts(db), Options.Create(new AppPublicOptions { PublicUrl = publicUrl }));
        await handler.InitializeAsync(
            new AuthenticationScheme(McpBearerAuthenticationHandler.SchemeName, null, typeof(McpBearerAuthenticationHandler)), http);
        return (handler, http);
    }

    [Fact]
    public async Task AnOAuthToken_ForAnActiveAccount_Authenticates()
    {
        var userId = Guid.NewGuid();
        var (handler, _) = await Build("eyJ.oauth.token", oauth: OAuthPrincipal(userId));

        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(userId.ToString(), result.Principal!.FindFirstValue(ClaimTypes.NameIdentifier));
    }

    [Fact]
    public async Task AnOAuthToken_ForADisabledAccount_IsRejected()
    {
        var (handler, _) = await Build("eyJ.oauth.token", oauth: OAuthPrincipal(Guid.NewGuid()), active: false);

        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task AnOAuthToken_ForAnAccountThatNoLongerExists_IsRejected()
    {
        var (handler, _) = await Build("eyJ.oauth.token", oauth: OAuthPrincipal(Guid.NewGuid()), seedUsers: false);

        Assert.False((await handler.AuthenticateAsync()).Succeeded);
    }

    [Fact]
    public async Task AStaticToken_VerifiedByTheAuthenticator_Authenticates()
    {
        var owner = Guid.NewGuid();
        var (handler, _) = await Build("dz_mcp_whatever", staticTokenOwner: owner);

        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(owner.ToString(), result.Principal!.FindFirstValue(ClaimTypes.NameIdentifier));
    }

    [Fact]
    public async Task TheChallenge_PointsAtTheConfiguredPublicOrigin_NotTheRequestHost()
    {
        var (handler, http) = await Build("dz_mcp_unknown", publicUrl: "https://diariz.example.com/");
        http.Request.Scheme = "http";
        http.Request.Host = new HostString("attacker.example");

        await handler.ChallengeAsync(null);

        Assert.Equal(StatusCodes.Status401Unauthorized, http.Response.StatusCode);
        Assert.Equal(
            "Bearer resource_metadata=\"https://diariz.example.com/.well-known/oauth-protected-resource\"",
            http.Response.Headers.WWWAuthenticate.ToString());
    }

    [Fact]
    public async Task TheChallenge_FallsBackToTheRequestOrigin_OnlyWhenNoPublicUrlIsConfigured()
    {
        var (handler, http) = await Build("dz_mcp_unknown", publicUrl: "");
        http.Request.Scheme = "http";
        http.Request.Host = new HostString("localhost:8080");

        await handler.ChallengeAsync(null);

        Assert.Equal(
            "Bearer resource_metadata=\"http://localhost:8080/.well-known/oauth-protected-resource\"",
            http.Response.Headers.WWWAuthenticate.ToString());
    }
    [Fact]
    public async Task ARejectedCredential_IsLoggedAsAWarning_WithoutTheTokenItself()
    {
        var loggers = new CapturingLoggerFactory();
        var (handler, _) = await Build("eyJ.secret-token-value", oauth: OAuthPrincipal(Guid.NewGuid()), active: false, loggers: loggers);

        await handler.AuthenticateAsync();

        var warning = Assert.Single(loggers.Logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("not active", warning.Message);
        Assert.DoesNotContain("secret-token-value", warning.Message);
    }

    [Fact]
    public async Task ARequestWithNoCredential_IsNotLogged()
    {
        var loggers = new CapturingLoggerFactory();
        var (handler, http) = await Build("x", loggers: loggers);
        http.Request.Headers.Authorization = "";

        await handler.AuthenticateAsync();

        Assert.DoesNotContain(loggers.Logger.Entries, e => e.Level >= LogLevel.Warning);
    }
}
