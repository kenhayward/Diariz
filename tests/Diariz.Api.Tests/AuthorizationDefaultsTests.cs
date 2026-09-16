using Diariz.Api.Auth;
using Diariz.Api.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Diariz.Api.Tests;

/// <summary>Authorization is deny-by-default: an endpoint that forgets its attribute requires a signed-in user rather
/// than silently becoming public. The anonymous surface is therefore exactly the endpoints marked
/// <see cref="AllowAnonymousAttribute"/>, and that set is pinned here - making an endpoint public is a deliberate
/// edit to this list, never a side effect.</summary>
public class AuthorizationDefaultsTests
{
    /// <summary>Every controller action that deliberately accepts an unauthenticated caller.</summary>
    private static readonly string[] PinnedAnonymous =
    [
        // Sign-in, access requests and account setup: the caller has no session yet.
        "POST api/auth/login",
        "POST api/auth/request-access",
        "GET api/auth/setup/validate",
        "POST api/auth/setup",
        "GET api/auth/providers",
        "GET api/auth/google/start",
        "GET api/auth/google/callback",
        "POST api/auth/google/exchange",
        "POST api/auth/desktop/exchange",
        // Runtime config the SPA reads before sign-in, and the language list the login page offers.
        "GET api/config",
        "GET api/languages",
        // OAuth authorization server: discovery, registration and the browser-redirect authorize step.
        "GET .well-known/oauth-protected-resource",
        "POST connect/register",
        "GET connect/authorize",
        "POST connect/authorize",
        // Worker callbacks: authenticated by the shared X-Worker-Secret header, not a user session.
        "POST internal/transcriptions/result",
        "POST internal/transcriptions/failure",
        "POST internal/recordings/merge-result",
        "POST internal/recordings/merge-failure",
        "POST internal/people/voiceprint-result",
        "POST internal/people/voiceprint-failure",
        "POST internal/transcriptions/live-chunk",
        "POST internal/transcriptions/live-chunk-failure",
    ];

    private static async Task<List<RouteEndpoint>> ControllerEndpoints()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddApplicationPart(typeof(RecordingsController).Assembly);
        var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().ToList();
        await app.DisposeAsync();
        return endpoints;
    }

    private static IEnumerable<string> Describe(RouteEndpoint e) =>
        (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["ANY"])
        .Select(m => $"{m} {e.RoutePattern.RawText!.TrimStart('/')}");

    [Fact]
    public void TheFallbackPolicy_RequiresAnAuthenticatedUser()
    {
        var options = new AuthorizationOptions();
        AuthorizationDefaults.Configure(options);

        Assert.NotNull(options.FallbackPolicy);
        Assert.Contains(options.FallbackPolicy!.Requirements,
            r => r is Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement);
    }

    [Fact]
    public async Task EveryControllerEndpoint_StatesItsAccessExplicitly()
    {
        var implicitAccess = (await ControllerEndpoints())
            .Where(e => e.Metadata.GetMetadata<IAuthorizeData>() is null && e.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .SelectMany(Describe)
            .Order()
            .ToList();

        Assert.Empty(implicitAccess);
    }

    [Fact]
    public async Task TheAnonymousSurface_IsExactlyThePinnedList()
    {
        var anonymous = (await ControllerEndpoints())
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .SelectMany(Describe)
            .Order()
            .ToList();

        Assert.Equal(PinnedAnonymous.Order().ToList(), anonymous);
    }
}
