using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Diariz.Api.Contracts;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Diariz.Api.IntegrationTests;

/// <summary>HTTP-level coverage of the <c>ManagePlatform</c> gate on <c>LlmModelsController</c>. The
/// in-memory unit harness constructs the controller directly, so it can never evaluate an
/// <c>[Authorize(Policy=...)]</c> attribute - an assertion about authorisation there would pass whether or
/// not the attribute existed. This runs the real pipeline instead, mirroring
/// <see cref="PlatformWebhooksAuthTests"/>.
///
/// The gate matters more here than on most admin controllers: these rows hold endpoint credentials and
/// decide which model every user's calls are sent to.</summary>
[Collection(IntegrationCollection.Name)]
public class LlmModelsAuthTests(ContainersFixture fx)
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
        return id;
    }

    private static HttpClient AuthenticatedClient(DiarizWebAppFactory factory, Guid userId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokens.Issue(userId));
        return client;
    }

    [Fact]
    public async Task List_IsForbidden_ForNonAdminUser()
    {
        using var factory = NewFactory();
        using var client = AuthenticatedClient(factory, await SeedNonAdminUserAsync(factory));

        var resp = await client.GetAsync("/api/admin/llm-models");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Create_IsForbidden_ForNonAdminUser()
    {
        // The write side needs its own case: a read-only leak and a write leak are different failures, and
        // an attribute could plausibly be applied to one action and not the class.
        using var factory = NewFactory();
        using var client = AuthenticatedClient(factory, await SeedNonAdminUserAsync(factory));

        var resp = await client.PostAsJsonAsync(
            "/api/admin/llm-models",
            new LlmModelUpsert("sneaky", "http://evil/v1", "sk-x", 8192, []));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Assignments_AreForbidden_ForNonAdminUser()
    {
        // Re-pointing a call group is the highest-value write on this controller: it redirects every user's
        // calls of that type to a model of the caller's choosing.
        using var factory = NewFactory();
        using var client = AuthenticatedClient(factory, await SeedNonAdminUserAsync(factory));

        var resp = await client.PutAsJsonAsync(
            "/api/admin/llm-models/assignments", new LlmAssignmentsDto(null, []));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task List_Allows_PlatformAdministrator()
    {
        using var factory = NewFactory();
        using var client = AuthenticatedClient(factory, await SeedPlatformAdminAsync(factory));

        var resp = await client.GetAsync("/api/admin/llm-models");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}
