using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Diariz.Api.Contracts;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Diariz.Api.IntegrationTests;

/// <summary>
/// HTTP-level coverage of the <c>ManagePlatform</c> policy gate on <c>PlatformWebhooksController</c> - the
/// in-memory unit harness constructs the controller directly and so can never evaluate an
/// <c>[Authorize(Policy=...)]</c> attribute (see <c>PlatformWebhooksControllerTests</c> for the CRUD/validation
/// behaviour). This class runs the real ASP.NET Core pipeline via <see cref="DiarizWebAppFactory"/> to prove a
/// non-admin user is rejected by <c>ManagePlatform</c> on both reads and writes - mirrors
/// <see cref="WorkflowSignalsAuthTests"/>'s pattern for the sibling signal-vocabulary controller.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class PlatformWebhooksAuthTests(ContainersFixture fx)
{
    private DiarizWebAppFactory NewFactory() => new(fx);

    private static async Task<Guid> SeedNonAdminUserAsync(DiarizWebAppFactory factory)
    {
        var id = Guid.NewGuid();
        await using var scope = factory.Services.CreateAsyncScope();
        Users.Ensure(scope.ServiceProvider.GetRequiredService<DiarizDbContext>(), id);
        return id;
    }

    private static async Task<Guid> SeedPlatformAdminAsync(DiarizWebAppFactory factory)
    {
        var id = Guid.NewGuid();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DiarizDbContext>();
        Users.Ensure(db, id);
        Perms.Grant(db, id, Perms.PlatformAdministrator); // authority is group membership, not a role claim

        // The gate under test is ManagePlatform, not the WebhooksEnabled toggle - but List still checks
        // EnabledAsync() after the policy passes, so turn it on for the positive (200) assertion.
        var settings = await db.PlatformSettings.FirstOrDefaultAsync(p => p.Id == PlatformSettings.SingletonId);
        if (settings is null) db.PlatformSettings.Add(new PlatformSettings { Id = PlatformSettings.SingletonId, WebhooksEnabled = true });
        else settings.WebhooksEnabled = true;
        await db.SaveChangesAsync();

        return id;
    }

    private static HttpClient AuthenticatedClient(DiarizWebAppFactory factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task List_IsForbidden_ForNonAdminUser()
    {
        using var factory = NewFactory();
        var userId = await SeedNonAdminUserAsync(factory);
        var token = TestTokens.Issue(userId);
        using var client = AuthenticatedClient(factory, token);

        var resp = await client.GetAsync("/api/admin/webhooks");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Create_IsForbidden_ForNonAdminUser()
    {
        using var factory = NewFactory();
        var userId = await SeedNonAdminUserAsync(factory);
        var token = TestTokens.Issue(userId);
        using var client = AuthenticatedClient(factory, token);

        var resp = await client.PostAsJsonAsync(
            "/api/admin/webhooks",
            new CreatePlatformWebhookRequest("z", "https://x/y", new[] { "recording.transcribed" }, new[] { "meeting-overrun" }));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task List_Allows_PlatformAdministrator()
    {
        using var factory = NewFactory();
        var adminId = await SeedPlatformAdminAsync(factory);
        var token = TestTokens.Issue(adminId);
        using var client = AuthenticatedClient(factory, token);

        var resp = await client.GetAsync("/api/admin/webhooks");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}
