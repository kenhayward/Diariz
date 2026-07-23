using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Diariz.Api.IntegrationTests;

/// <summary>
/// HTTP-level coverage of the runtime <see cref="Diariz.Domain.Entities.PlatformSettings.McpAccessEnabled"/>
/// kill-switch enforced in <see cref="Diariz.Api.Auth.McpBearerAuthenticationHandler"/>: with the toggle off,
/// a request bearing a valid <c>dz_mcp_</c> token is rejected with 401 before it ever reaches the MCP endpoint;
/// with it on (the default), the same credential is not rejected by the gate. Uses its own
/// <see cref="DiarizWebAppFactory"/> with <c>Mcp__Enabled</c> forced on - the factory defaults it off (see the
/// factory's class remarks) because most of this suite doesn't exercise <c>/mcp</c>; this class specifically
/// targets it, so it opts back in for its own instances only.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class McpToggleTests(ContainersFixture fx)
{
    private static McpTokensController TokenController(DiarizDbContext db, Guid userId) =>
        new(db, new McpTokenService()) { ControllerContext = Http.Context(userId) };

    private DiarizWebAppFactory NewFactoryWithMcpEnabled()
    {
        var factory = new DiarizWebAppFactory(fx);
        // Safe under the "integration" collection's sequential-execution guarantee (see DiarizWebAppFactory's
        // remarks): env vars are only read at first Build(), and every other factory resets this one to false
        // in its own constructor before it matters.
        Environment.SetEnvironmentVariable("Mcp__Enabled", "true");
        return factory;
    }

    private static async Task<Guid> SeedUserAsync(DiarizWebAppFactory factory)
    {
        var id = Guid.NewGuid();
        await using var scope = factory.Services.CreateAsyncScope();
        Users.Ensure(scope.ServiceProvider.GetRequiredService<DiarizDbContext>(), id);
        return id;
    }

    private static async Task<string> IssueMcpTokenAsync(DiarizWebAppFactory factory, Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DiarizDbContext>();
        var created = (await TokenController(db, userId).Create(new CreateMcpTokenRequest("it"))).Value!;
        return created.Token;
    }

    private static async Task SetMcpAccessAsync(DiarizWebAppFactory factory, bool enabled)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DiarizDbContext>();
        var settings = await new PlatformSettingsService(db).GetAsync();
        settings.McpAccessEnabled = enabled;
        await db.SaveChangesAsync();
    }

    private static HttpRequestMessage McpInitializeRequest(string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"McpToggleTests","version":"1.0.0"}}}""",
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return req;
    }

    [Fact]
    public async Task McpRequest_WithValidToken_Is401_WhenPlatformToggleIsOff()
    {
        using var factory = NewFactoryWithMcpEnabled();
        var userId = await SeedUserAsync(factory);
        var token = await IssueMcpTokenAsync(factory, userId);
        await SetMcpAccessAsync(factory, false);
        try
        {
            using var client = factory.CreateClient();
            var resp = await client.SendAsync(McpInitializeRequest(token));

            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }
        finally { await SetMcpAccessAsync(factory, true); }
    }

    [Fact]
    public async Task McpRequest_WithValidToken_IsNotRejectedByTheToggle_WhenPlatformToggleIsOn()
    {
        using var factory = NewFactoryWithMcpEnabled();
        var userId = await SeedUserAsync(factory);
        var token = await IssueMcpTokenAsync(factory, userId);
        await SetMcpAccessAsync(factory, true); // the default, set explicitly for clarity

        using var client = factory.CreateClient();
        var resp = await client.SendAsync(McpInitializeRequest(token));

        Assert.NotEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
