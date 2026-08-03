using System.Net;
using System.Net.Http.Headers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Diariz.Api.IntegrationTests;

/// <summary>
/// Covers what the EF in-memory provider used by <c>FeedbackControllerTests</c> cannot prove: real foreign-key
/// cascade behaviour and non-zero-offset <c>timestamptz</c> rejection (row-level), plus HTTP-level coverage of
/// the <c>ManagePlatform</c> policy gate on <c>GET</c>/<c>DELETE</c> (constructing the controller directly, as
/// the unit tests do, bypasses <c>[Authorize]</c> entirely) - mirrors the pattern in
/// <see cref="WorkflowSignalsAuthTests"/> / <see cref="PlatformWebhooksAuthTests"/> for the auth half.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class FeedbackIntegrationTests(ContainersFixture fx)
{
    [Fact]
    public async Task DeletingAUser_CascadesTheirFeedback()
    {
        // The in-memory provider does not enforce foreign keys, so this is the only place the cascade
        // is actually proven.
        await using var db = fx.CreateDbContext();
        var user = new ApplicationUser { Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@e.com" };
        db.Users.Add(user);
        db.Feedback.Add(new Feedback { UserId = user.Id, Description = "x" });
        await db.SaveChangesAsync();

        db.Users.Remove(user);
        await db.SaveChangesAsync();

        Assert.Empty(await db.Feedback.Where(f => f.UserId == user.Id).ToListAsync());
    }

    [Fact]
    public async Task CreatedAt_RoundTripsThroughPostgres()
    {
        // Npgsql throws at SaveChanges on a non-zero-offset DateTimeOffset written to timestamptz.
        await using var db = fx.CreateDbContext();
        var user = new ApplicationUser { Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@e.com" };
        db.Users.Add(user);
        var row = new Feedback { UserId = user.Id, Description = "x", CreatedAt = DateTimeOffset.UtcNow };
        db.Feedback.Add(row);
        await db.SaveChangesAsync();

        Assert.Equal(TimeSpan.Zero, (await db.Feedback.FindAsync(row.Id))!.CreatedAt.Offset);
    }

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

        var resp = await client.GetAsync("/api/feedback");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Delete_IsForbidden_ForNonAdminUser()
    {
        using var factory = NewFactory();
        var userId = await SeedNonAdminUserAsync(factory);
        var token = TestTokens.Issue(userId);
        using var client = AuthenticatedClient(factory, token);

        var resp = await client.DeleteAsync($"/api/feedback/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task List_Allows_PlatformAdministrator()
    {
        using var factory = NewFactory();
        var adminId = await SeedPlatformAdminAsync(factory);
        var token = TestTokens.Issue(adminId);
        using var client = AuthenticatedClient(factory, token);

        var resp = await client.GetAsync("/api/feedback");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Delete_Allows_PlatformAdministrator()
    {
        using var factory = NewFactory();
        var adminId = await SeedPlatformAdminAsync(factory);
        var token = TestTokens.Issue(adminId);
        using var client = AuthenticatedClient(factory, token);

        var resp = await client.DeleteAsync($"/api/feedback/{Guid.NewGuid()}");

        // Not forbidden - the policy let it through; unknown id resolves to NotFound.
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
