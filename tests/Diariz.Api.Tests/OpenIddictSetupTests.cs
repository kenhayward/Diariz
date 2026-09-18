using Diariz.Api.Auth;
using Diariz.Api.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenIddict.Server;

namespace Diariz.Api.Tests;

public class OpenIddictSetupTests : IDisposable
{
    private readonly string _keysDir = Path.Combine(Path.GetTempPath(), "diariz-oidc-setup", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_keysDir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private OpenIddictServerOptions ServerOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDiarizMcpOAuth(new McpOAuthOptions(), "https://diariz.example.com", _keysDir, isDevelopment: false,
            resource: "https://diariz.example.com/mcp");
        return services.BuildServiceProvider().GetRequiredService<IOptions<OpenIddictServerOptions>>().Value;
    }

    /// <summary><c>IgnoreResourcePermissions()</c> lets every registered client ask for every registered resource. That
    /// is harmless with exactly one resource and a confused-deputy problem with two, so adding a second one must fail
    /// here and force that decision to be revisited (grant resources per client at registration instead).</summary>
    [Fact]
    public void ExactlyOneResourceIsRegistered_BecauseResourcePermissionsAreNotEnforced()
    {
        var options = ServerOptions();

        Assert.True(options.IgnoreResourcePermissions);
        Assert.Equal(["https://diariz.example.com/mcp"], options.Resources.Select(r => r.ToString()).ToArray());
    }

    [Fact]
    public void TokenLifetimes_AreStatedExplicitly()
    {
        var options = ServerOptions();

        Assert.Equal(TimeSpan.FromHours(1), options.AccessTokenLifetime);
        Assert.Equal(TimeSpan.FromDays(14), options.RefreshTokenLifetime);
    }
}
