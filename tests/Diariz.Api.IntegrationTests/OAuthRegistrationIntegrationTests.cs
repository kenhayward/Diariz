using System.Text.Json;
using Diariz.Api.Auth;
using Diariz.Api.Configuration;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server;

namespace Diariz.Api.IntegrationTests;

/// <summary>Exercises the OpenIddict OAuth server foundation against real Postgres: that the server
/// configuration validates and its persistent keys load, and that Dynamic Client Registration creates a client
/// for an allowed redirect host but rejects a disallowed one. Uses the shared containers' database (which has
/// the OpenIddict migration applied by the fixture).</summary>
[Collection(IntegrationCollection.Name)]
public class OAuthRegistrationIntegrationTests(ContainersFixture fx) : IDisposable
{
    private readonly string _keysDir = Path.Combine(Path.GetTempPath(), "diariz-oidc-test", Guid.NewGuid().ToString("N"));

    private ServiceProvider BuildProvider(McpOAuthOptions? options = null)
    {
        options ??= new McpOAuthOptions();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<DiarizDbContext>(o => o.UseNpgsql(fx.PostgresConnectionString, x => x.UseVector()));
        services.AddSingleton<IOptions<McpOAuthOptions>>(Options.Create(options));
        services.AddDiarizMcpOAuth(options, issuer: "https://diariz.example.com", keysDir: _keysDir, isDevelopment: true);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void ServerConfiguration_Validates_AndLoadsPersistentKeys()
    {
        using var provider = BuildProvider();
        // Accessing the resolved server options runs OpenIddict's post-configuration + validation: it throws if
        // the flows/endpoints are incoherent, and requires signing + encryption credentials to be present.
        var server = provider.GetRequiredService<IOptionsMonitor<OpenIddictServerOptions>>().CurrentValue;
        Assert.NotEmpty(server.SigningCredentials);
        Assert.NotEmpty(server.EncryptionCredentials);

        // The certificates were persisted to the keys directory so they survive a restart.
        Assert.True(File.Exists(Path.Combine(_keysDir, "oidc-signing.pfx")));
        Assert.True(File.Exists(Path.Combine(_keysDir, "oidc-encryption.pfx")));
    }

    [Fact]
    public async Task Register_WithAllowedRedirectHost_CreatesAPublicPkceClient()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var controller = Controller(scope);

        var body = Json("""{"redirect_uris":["https://claude.ai/api/mcp/auth_callback"],"client_name":"Claude"}""");
        var result = await controller.Register(body, default);

        var created = Assert.IsType<CreatedResult>(result);
        var payload = Assert.IsType<Dictionary<string, object?>>(created.Value);
        var clientId = Assert.IsType<string>(payload["client_id"]);
        Assert.Equal("none", payload["token_endpoint_auth_method"]);

        // The client is persisted and public, with PKCE required and the mcp scope granted.
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var app = await manager.FindByClientIdAsync(clientId);
        Assert.NotNull(app);
        Assert.Equal(OpenIddictConstants.ClientTypes.Public, await manager.GetClientTypeAsync(app!));
        var permissions = await manager.GetPermissionsAsync(app!);
        Assert.Contains(OpenIddictConstants.Permissions.Prefixes.Scope + McpOAuthOptions.Scope, permissions);
        var requirements = await manager.GetRequirementsAsync(app!);
        Assert.Contains(OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange, requirements);
    }

    [Fact]
    public async Task Register_WithDisallowedRedirectHost_IsRejected_AndCreatesNoClient()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var before = await manager.CountAsync(default);

        var controller = Controller(scope);
        var body = Json("""{"redirect_uris":["https://evil.example.org/cb"],"client_name":"Evil"}""");
        var result = await controller.Register(body, default);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("invalid_redirect_uri", System.Text.Json.JsonSerializer.Serialize(bad.Value));
        Assert.Equal(before, await manager.CountAsync(default)); // nothing was created
    }

    private static OAuthRegistrationController Controller(IServiceScope scope) =>
        new(scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>(),
            scope.ServiceProvider.GetRequiredService<IOptions<McpOAuthOptions>>());

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    public void Dispose()
    {
        try { if (Directory.Exists(_keysDir)) Directory.Delete(_keysDir, recursive: true); } catch { /* best effort */ }
    }
}
