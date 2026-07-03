using Diariz.Api.Auth;
using Diariz.Api.Configuration;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Diariz.Api.IntegrationTests;

/// <summary>The OAuth connections API against real Postgres + OpenIddict: listing a user's granted
/// authorizations (the web-connector connections) and revoking one, owner-scoped.</summary>
[Collection(IntegrationCollection.Name)]
public class OAuthConnectionsIntegrationTests(ContainersFixture fx) : IDisposable
{
    private readonly string _keysDir = Path.Combine(Path.GetTempPath(), "diariz-oidc-conn", Guid.NewGuid().ToString("N"));

    private ServiceProvider BuildProvider()
    {
        var options = new McpOAuthOptions();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<DiarizDbContext>(o => o.UseNpgsql(fx.PostgresConnectionString, x => x.UseVector()));
        services.AddIdentityCore<ApplicationUser>().AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<DiarizDbContext>();
        services.AddSingleton<IOptions<McpOAuthOptions>>(Options.Create(options));
        services.AddSingleton<IOAuthConsentTicketProtector>(new OAuthConsentTicketProtector(new EphemeralDataProtectionProvider()));
        services.AddDiarizMcpOAuth(options, issuer: "https://diariz.example.com", keysDir: _keysDir, isDevelopment: true,
            resource: "https://diariz.example.com/mcp");
        return services.BuildServiceProvider();
    }

    /// <summary>Registers a client and a valid authorization for <paramref name="subject"/>, returning the
    /// authorization id.</summary>
    private static async Task<string> SeedConnection(IServiceScope scope, Guid subject, string clientName)
    {
        var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var auths = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        var app = await apps.CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = Guid.NewGuid().ToString("N"),
            ClientType = ClientTypes.Public,
            DisplayName = clientName,
        });
        var auth = await auths.CreateAsync(new OpenIddictAuthorizationDescriptor
        {
            Subject = subject.ToString(),
            ApplicationId = await apps.GetIdAsync(app),
            Type = AuthorizationTypes.Permanent,
            Status = Statuses.Valid,
            Scopes = { McpOAuthOptions.Scope },
        });
        return (await auths.GetIdAsync(auth))!;
    }

    private static OAuthConnectionsController Controller(IServiceScope scope, Guid userId) =>
        new(scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>(),
            scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>(),
            scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>())
        { ControllerContext = Http.Context(userId) };

    [Fact]
    public async Task List_ReturnsTheUsersConnections_OwnerScoped()
    {
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var mine = await SeedConnection(scope, me, $"Claude {me:N}");
        await SeedConnection(scope, other, $"Someone else {other:N}");

        var list = await Controller(scope, me).List(default);

        Assert.Contains(list, c => c.Id == mine);
        Assert.All(list, c => Assert.StartsWith("Claude ", c.ClientName));
        Assert.Contains(list, c => c.Scopes.Contains(McpOAuthOptions.Scope));
    }

    [Fact]
    public async Task Revoke_RemovesTheConnection()
    {
        var me = Guid.NewGuid();
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var id = await SeedConnection(scope, me, $"Claude {me:N}");

        var revoke = await Controller(scope, me).Revoke(id, default);
        Assert.IsType<NoContentResult>(revoke);

        // A revoked authorization is no longer listed.
        var list = await Controller(scope, me).List(default);
        Assert.DoesNotContain(list, c => c.Id == id);
    }

    [Fact]
    public async Task Revoke_OtherUsersConnection_Is404()
    {
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var theirs = await SeedConnection(scope, other, $"Theirs {other:N}");

        var result = await Controller(scope, me).Revoke(theirs, default);
        Assert.IsType<NotFoundResult>(result);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_keysDir)) Directory.Delete(_keysDir, recursive: true); } catch { /* best effort */ }
    }
}
