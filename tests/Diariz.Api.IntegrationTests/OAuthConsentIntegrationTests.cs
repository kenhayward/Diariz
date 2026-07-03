using Diariz.Api.Auth;
using Diariz.Api.Configuration;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Diariz.Api.IntegrationTests;

/// <summary>The OAuth consent API against real Postgres + Identity + OpenIddict: the consent screen's
/// client-name lookup, and that recording a decision is gated on account status and emits the bridge cookie.</summary>
[Collection(IntegrationCollection.Name)]
public class OAuthConsentIntegrationTests(ContainersFixture fx) : IDisposable
{
    private readonly string _keysDir = Path.Combine(Path.GetTempPath(), "diariz-oidc-consent", Guid.NewGuid().ToString("N"));

    private ServiceProvider BuildProvider()
    {
        var options = new McpOAuthOptions();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<DiarizDbContext>(o => o.UseNpgsql(fx.PostgresConnectionString, x => x.UseVector()));
        services.AddIdentityCore<ApplicationUser>().AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<DiarizDbContext>();
        services.AddSingleton<IOptions<McpOAuthOptions>>(Options.Create(options));
        services.AddSingleton<IOAuthConsentTicketProtector>(new OAuthConsentTicketProtector(new EphemeralDataProtectionProvider()));
        services.AddDiarizMcpOAuth(options, issuer: "https://diariz.example.com", keysDir: _keysDir, isDevelopment: true, resource: "https://diariz.example.com/mcp");
        return services.BuildServiceProvider();
    }

    private async Task<Guid> SeedUser(bool active = true, bool enabled = true)
    {
        await using var db = fx.CreateDbContext();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"{Guid.NewGuid()}@x.test",
            Email = "u@x.test",
            Status = active ? UserStatus.Active : UserStatus.Invited,
            IsEnabled = enabled,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<string> RegisterClient(IServiceScope scope, string displayName)
    {
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var clientId = Guid.NewGuid().ToString("N");
        await manager.CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientType = ClientTypes.Public,
            DisplayName = displayName,
        });
        return clientId;
    }

    private static OAuthConsentController Controller(IServiceScope scope, Guid userId) =>
        new(scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>(),
            scope.ServiceProvider.GetRequiredService<IOAuthConsentTicketProtector>(),
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>())
        { ControllerContext = Http.Context(userId) };

    [Fact]
    public async Task ConsentInfo_ReturnsTheClientDisplayName_And404sUnknownClients()
    {
        var userId = await SeedUser();
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var clientId = await RegisterClient(scope, "Claude");
        var controller = Controller(scope, userId);

        var ok = await controller.ConsentInfo(clientId, default);
        Assert.Equal("Claude", ok.Value!.ClientName);

        var missing = await controller.ConsentInfo(Guid.NewGuid().ToString("N"), default);
        Assert.IsType<NotFoundResult>(missing.Result);
    }

    [Fact]
    public async Task Consent_ActiveUser_SetsTheBridgeCookie()
    {
        var userId = await SeedUser();
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var clientId = await RegisterClient(scope, "Claude");
        var controller = Controller(scope, userId);

        var result = await controller.Consent(new ConsentRequest(clientId, Allow: true), default);

        Assert.IsType<NoContentResult>(result);
        var setCookie = controller.Response.Headers.SetCookie.ToString();
        Assert.Contains(OAuthConsentTicketProtector.CookieName, setCookie);
    }

    [Fact]
    public async Task Consent_InactiveUser_IsForbidden_AndSetsNoCookie()
    {
        var userId = await SeedUser(active: false);
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var clientId = await RegisterClient(scope, "Claude");
        var controller = Controller(scope, userId);

        var result = await controller.Consent(new ConsentRequest(clientId, Allow: true), default);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, obj.StatusCode);
        Assert.DoesNotContain(OAuthConsentTicketProtector.CookieName, controller.Response.Headers.SetCookie.ToString());
    }

    [Fact]
    public async Task Consent_UnknownClient_Is404()
    {
        var userId = await SeedUser();
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var controller = Controller(scope, userId);

        var result = await controller.Consent(new ConsentRequest(Guid.NewGuid().ToString("N"), Allow: true), default);
        Assert.IsType<NotFoundResult>(result);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_keysDir)) Directory.Delete(_keysDir, recursive: true); } catch { /* best effort */ }
    }
}
