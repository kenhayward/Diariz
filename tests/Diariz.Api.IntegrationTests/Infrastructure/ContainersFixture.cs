using System.Text;
using Diariz.Domain;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Diariz.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Starts real Postgres (with pgvector), Redis and SeaweedFS (S3) once for the whole test assembly and
/// applies EF migrations against the database. Containers are torn down when the assembly finishes.
/// Tests share these containers (they run sequentially via the collection) and isolate themselves
/// with unique ids / keys rather than per-test databases.
/// </summary>
public sealed class ContainersFixture : IAsyncLifetime
{
    // Same images as deploy/docker-compose.yml. The image is passed to the builder constructor
    // (the parameterless ctor + WithImage is obsolete in Testcontainers 4.x).
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg16").Build();
    private readonly RedisContainer _redis = new RedisBuilder("redis:8-alpine").Build();
    // SeaweedFS replaced MinIO (issue #769): MinIO's community edition is archived and its images can no
    // longer be pulled. Pinned to the tag deploy/docker-compose.yml uses. Testcontainers has no SeaweedFS
    // module, so this is a generic container with its own readiness check (the S3 gateway's /healthz).
    //
    // -volume.max=0 sizes the volume count from free disk. SeaweedFS makes each bucket a collection with its
    // own volumes, and the default cap (8) runs out after a couple of buckets - the suite creates one bucket
    // per test. -master.volumeSizeLimitMB keeps each volume small for the same reason.
    private const int S3Port = 8333;
    private readonly IContainer _s3 = new ContainerBuilder("chrislusf/seaweedfs:4.48")
        .WithResourceMapping(Encoding.UTF8.GetBytes(SeaweedFsS3Config.Json), "/etc/seaweedfs/s3.json")
        .WithCommand("server", "-dir=/data", "-ip.bind=0.0.0.0", "-s3", $"-s3.port={S3Port}",
            "-s3.config=/etc/seaweedfs/s3.json", "-volume.max=0", "-master.volumeSizeLimitMB=64")
        .WithPortBinding(S3Port, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(r => r.ForPort(S3Port).ForPath("/healthz")))
        .Build();

    public string PostgresConnectionString => _postgres.GetConnectionString();
    public string RedisConnectionString => _redis.GetConnectionString();
    public string S3Endpoint => $"http://{_s3.Hostname}:{_s3.GetMappedPublicPort(S3Port)}";
    public string S3AccessKey => SeaweedFsS3Config.AccessKey;
    public string S3SecretKey => SeaweedFsS3Config.SecretKey;

    public DiarizDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<DiarizDbContext>()
            .UseNpgsql(PostgresConnectionString, o => o.UseVector())
            .Options;
        return new DiarizDbContext(options);
    }

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync(), _s3.StartAsync());

        // Exercise the real migration pipeline (CREATE EXTENSION vector, vector(768) column, indexes).
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
        await _s3.DisposeAsync();
    }
}

/// <summary>All integration tests share one fixture, so they run sequentially against one set of containers.</summary>
[CollectionDefinition(Name)]
public sealed class IntegrationCollection : ICollectionFixture<ContainersFixture>
{
    public const string Name = "integration";
}
