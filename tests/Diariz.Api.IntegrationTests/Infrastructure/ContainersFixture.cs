using Diariz.Domain;
using Microsoft.EntityFrameworkCore;
using Testcontainers.Minio;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Diariz.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Starts real Postgres (with pgvector), Redis, and MinIO once for the whole test assembly and
/// applies EF migrations against the database. Containers are torn down when the assembly finishes.
/// Tests share these containers (they run sequentially via the collection) and isolate themselves
/// with unique ids / keys rather than per-test databases.
/// </summary>
public sealed class ContainersFixture : IAsyncLifetime
{
    // Same images as deploy/docker-compose.yml. The image is passed to the builder constructor
    // (the parameterless ctor + WithImage is obsolete in Testcontainers 4.x).
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg16").Build();
    private readonly RedisContainer _redis = new RedisBuilder("redis:7-alpine").Build();
    private readonly MinioContainer _minio = new MinioBuilder("minio/minio:latest").Build();

    public string PostgresConnectionString => _postgres.GetConnectionString();
    public string RedisConnectionString => _redis.GetConnectionString();
    public string MinioEndpoint => _minio.GetConnectionString();
    public string MinioAccessKey => _minio.GetAccessKey();
    public string MinioSecretKey => _minio.GetSecretKey();

    public DiarizDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<DiarizDbContext>()
            .UseNpgsql(PostgresConnectionString, o => o.UseVector())
            .Options;
        return new DiarizDbContext(options);
    }

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync(), _minio.StartAsync());

        // Exercise the real migration pipeline (CREATE EXTENSION vector, vector(768) column, indexes).
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
        await _minio.DisposeAsync();
    }
}

/// <summary>All integration tests share one fixture, so they run sequentially against one set of containers.</summary>
[CollectionDefinition(Name)]
public sealed class IntegrationCollection : ICollectionFixture<ContainersFixture>
{
    public const string Name = "integration";
}
