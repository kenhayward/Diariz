using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diariz.Api.Tests;

/// <summary>The retentionDays &lt;= 0 guard short-circuits before the ExecuteDeleteAsync query, so it is
/// safe to exercise on the EF in-memory provider here. The actual deletion behaviour (and the exact
/// boundary) needs ExecuteDeleteAsync translation, which the in-memory provider does not support - that
/// lives in LlmUsageIntegrationTests against real Postgres instead.</summary>
public class LlmUsageRetentionTests
{
    private static LlmCall Row(DateTimeOffset startedAt) => new()
    {
        Id = Guid.NewGuid(), OperationId = Guid.NewGuid(), Sequence = 1, Kind = LlmCallKind.Tags,
        Model = "m", Endpoint = "http://x/v1", StartedAt = startedAt, CompletedAt = startedAt,
        DurationMs = 1, Success = true,
    };

    [Fact]
    public async Task RunAsync_DeletesNothing_WhenRetentionIsZero()
    {
        // 0 means keep forever - never treat it as "delete everything older than now".
        var db = TestDb.Create();
        db.LlmCalls.Add(Row(DateTimeOffset.UtcNow.AddYears(-5)));
        await db.SaveChangesAsync();

        var deleted = await LlmUsageRetentionSweep.RunAsync(
            db, DateTimeOffset.UtcNow, retentionDays: 0, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(0, deleted);
        Assert.Equal(1, await db.LlmCalls.CountAsync());
    }

    [Fact]
    public async Task RunAsync_DeletesNothing_WhenRetentionIsNegative()
    {
        var db = TestDb.Create();
        db.LlmCalls.Add(Row(DateTimeOffset.UtcNow.AddYears(-5)));
        await db.SaveChangesAsync();

        var deleted = await LlmUsageRetentionSweep.RunAsync(
            db, DateTimeOffset.UtcNow, retentionDays: -1, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(0, deleted);
        Assert.Equal(1, await db.LlmCalls.CountAsync());
    }
}
