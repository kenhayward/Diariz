using Microsoft.AspNetCore.Mvc.Testing;

namespace Diariz.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Boots the real ASP.NET Core pipeline (the actual <c>Program.cs</c> - auth middleware, routing,
/// <c>[Authorize]</c>, SignalR) against the containers already started by <see cref="ContainersFixture"/>,
/// rather than starting infrastructure of its own. This is the piece the rest of the integration suite is
/// missing: every other class in this project constructs a controller directly, which never runs
/// <c>Program.cs</c>'s JWT <c>OnMessageReceived</c> handler (the <c>access_token</c> query-string allowlist) at
/// all. A <see cref="DiarizWebAppFactory"/> per test gives each test its own isolated MinIO bucket and app-host
/// instance while all tests still share (and run sequentially against) the one set of Postgres/Redis/MinIO
/// containers, per the "integration" collection's contract.
///
/// <para><b>Why environment variables, not <c>ConfigureWebHost</c>/<c>ConfigureAppConfiguration</c>:</b>
/// <c>Program.cs</c> reads several config sections into local variables (<c>jwt</c>, <c>storage</c>,
/// <c>queue</c>, ...) near the top of its top-level statements, before <c>builder.Build()</c> is ever called.
/// <see cref="WebApplicationFactory{TEntryPoint}"/>'s <c>ConfigureWebHost</c> hook only gets to mutate the
/// builder at the moment <c>Build()</c> runs (that is the interception point the testing package uses for a
/// minimal-hosting <c>Program</c>) - by then those local variables already hold whatever appsettings.json said,
/// and configuring the builder further has no effect on them. Process environment variables, in contrast, are
/// visible from the very first line of <c>Program.cs</c> (<c>WebApplication.CreateBuilder(args)</c> wires up
/// <c>AddEnvironmentVariables()</c> immediately), so they are the only override this app's startup shape
/// actually honours. This only works safely because every test class in this assembly shares the
/// <c>ContainersFixture</c>/"integration" collection and therefore runs sequentially - see
/// <see cref="IntegrationCollection"/>; do not use this pattern from a class outside that collection.</para>
///
/// <para>Deliberately minimal: the MCP endpoint and its OpenIddict OAuth authorization server are switched off
/// (<c>Mcp__Enabled</c> / <c>McpOAuth__Enabled</c> = <c>false</c>). Neither is part of the surface this harness
/// targets (the query-string bearer-token allowlist), and leaving them on pulls in OpenIddict signing-key setup
/// on every test's app-host boot for no benefit here - do not switch them on to test something else without
/// reconsidering that cost. Everything else uses the real startup path, unaltered.</para>
/// </summary>
public sealed class DiarizWebAppFactory : WebApplicationFactory<Program>
{
    /// <summary>Signing key for JWTs this factory's app-host will accept. Tests mint tokens with
    /// <see cref="TestTokens.Issue"/> using the same key/issuer/audience.</summary>
    public const string JwtKey = "integration-http-harness-signing-key-32-bytes!!";
    public const string JwtIssuer = "diariz";
    public const string JwtAudience = "diariz";

    /// <summary>A MinIO bucket unique to this factory instance, so tests never see another test's (or another
    /// factory's) blobs.</summary>
    public string Bucket { get; } = $"it-http-{Guid.NewGuid():N}";

    public DiarizWebAppFactory(ContainersFixture fx)
    {
        // Set *before* anything triggers the host to boot (WebApplicationFactory builds the host lazily, on
        // first access of Server/Services/CreateClient). Safe only under the "integration" collection's
        // sequential-execution guarantee - see the class remarks.
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", fx.PostgresConnectionString);
        Environment.SetEnvironmentVariable("Jwt__Issuer", JwtIssuer);
        Environment.SetEnvironmentVariable("Jwt__Audience", JwtAudience);
        Environment.SetEnvironmentVariable("Jwt__Key", JwtKey);
        Environment.SetEnvironmentVariable("Storage__Endpoint", fx.MinioEndpoint);
        Environment.SetEnvironmentVariable("Storage__AccessKey", fx.MinioAccessKey);
        Environment.SetEnvironmentVariable("Storage__SecretKey", fx.MinioSecretKey);
        Environment.SetEnvironmentVariable("Storage__Bucket", Bucket);
        Environment.SetEnvironmentVariable("Storage__ForcePathStyle", "true");
        Environment.SetEnvironmentVariable("JobQueue__RedisConnection", fx.RedisConnectionString);
        // See class remarks: out of scope for this harness.
        Environment.SetEnvironmentVariable("Mcp__Enabled", "false");
        Environment.SetEnvironmentVariable("McpOAuth__Enabled", "false");
        // A random seed email per factory instance - several factories boot against the same shared Postgres
        // database (per the "integration" collection), and the seeded user has a unique-email constraint.
        Environment.SetEnvironmentVariable("Seed__Email", $"seed-{Guid.NewGuid():N}@x.test");
        Environment.SetEnvironmentVariable("Seed__Password", "Sup3rSecretSeed1!");
    }
}
