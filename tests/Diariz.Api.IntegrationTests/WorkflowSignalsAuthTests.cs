using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Diariz.Api.Contracts;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Diariz.Api.IntegrationTests;

/// <summary>
/// HTTP-level coverage of the <c>ManagePlatform</c> policy gate on <c>WorkflowSignalsController</c> - the
/// in-memory unit harness constructs the controller directly and so can never evaluate an
/// <c>[Authorize(Policy=...)]</c> attribute (see <c>WorkflowSignalsControllerTests</c> for the CRUD/validation
/// behaviour). This class runs the real ASP.NET Core pipeline via <see cref="DiarizWebAppFactory"/> to prove a
/// non-admin user is let through the plain <c>[Authorize]</c> list (any authenticated user) but rejected by
/// <c>ManagePlatform</c> on the write side.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class WorkflowSignalsAuthTests(ContainersFixture fx)
{
    private DiarizWebAppFactory NewFactory() => new(fx);

    private static async Task<Guid> SeedNonAdminUserAsync(DiarizWebAppFactory factory)
    {
        var id = Guid.NewGuid();
        await using var scope = factory.Services.CreateAsyncScope();
        Users.Ensure(scope.ServiceProvider.GetRequiredService<DiarizDbContext>(), id);
        return id;
    }

    private static HttpClient AuthenticatedClient(DiarizWebAppFactory factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task ListActive_AllowsAnyAuthenticatedUser()
    {
        using var factory = NewFactory();
        var userId = await SeedNonAdminUserAsync(factory);
        var token = TestTokens.Issue(userId);
        using var client = AuthenticatedClient(factory, token);

        var resp = await client.GetAsync("/api/workflow-signals");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Create_IsForbidden_ForNonAdminUser()
    {
        using var factory = NewFactory();
        var userId = await SeedNonAdminUserAsync(factory);
        var token = TestTokens.Issue(userId);
        using var client = AuthenticatedClient(factory, token);

        var resp = await client.PostAsJsonAsync(
            "/api/workflow-signals", new CreateWorkflowSignalRequest("post-to-slack", "Send to Slack", null));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task ListManage_IsForbidden_ForNonAdminUser()
    {
        using var factory = NewFactory();
        var userId = await SeedNonAdminUserAsync(factory);
        var token = TestTokens.Issue(userId);
        using var client = AuthenticatedClient(factory, token);

        var resp = await client.GetAsync("/api/workflow-signals/manage");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
}
