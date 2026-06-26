using Diariz.Domain;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests.Infrastructure;

/// <summary>
/// Creates a fresh, isolated <see cref="DiarizDbContext"/> backed by the EF Core in-memory
/// provider. Each call gets a unique database name so tests never share state. The in-memory
/// provider ignores Postgres-specific config (pgvector column types, unique indexes), which is
/// fine for testing query/orchestration logic — use the integration harness for constraint fidelity.
/// </summary>
public static class TestDb
{
    public static DiarizDbContext Create()
    {
        var name = $"diariz-tests-{Guid.NewGuid()}";
        var options = new DbContextOptionsBuilder<DiarizDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new DiarizDbContext(options);
    }
}
