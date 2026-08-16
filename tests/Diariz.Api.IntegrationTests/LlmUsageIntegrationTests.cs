using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diariz.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public class LlmUsageIntegrationTests(ContainersFixture fx)
{
    [Fact]
    public async Task LlmCall_RoundTripsThroughPostgres()
    {
        await using var db = fx.CreateDbContext();
        var started = DateTimeOffset.UtcNow.AddSeconds(-3);

        var call = new LlmCall
        {
            Id = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            Sequence = 1,
            Kind = LlmCallKind.Summarize,
            UserEmail = "owner@example.com",
            Model = "qwen3-30b",
            Endpoint = "http://lmstudio:1234/v1/chat/completions",
            StartedAt = started,
            CompletedAt = started.AddSeconds(3),
            DurationMs = 3000,
            PromptTokens = 1200,
            CompletionTokens = 340,
            TotalTokens = 1540,
            Streamed = false,
            Success = true,
            StatusCode = 200,
        };
        db.LlmCalls.Add(call);
        await db.SaveChangesAsync();

        await using var read = fx.CreateDbContext();
        var found = await read.LlmCalls.SingleAsync(c => c.Id == call.Id);
        Assert.Equal(LlmCallKind.Summarize, found.Kind);
        Assert.Equal(1540, found.TotalTokens);
        Assert.Equal(TimeSpan.Zero, found.StartedAt.Offset); // stored as UTC
    }

    [Fact]
    public async Task DeletingTheUser_NullsTheLink_ButKeepsTheRowAndTheEmailSnapshot()
    {
        // The log is an audit trail: a user's history must survive their deletion, which is why
        // UserEmail is a snapshot rather than a join. Bulk delete is the erasure escape hatch.
        await using var db = fx.CreateDbContext();
        var user = new ApplicationUser { Id = Guid.NewGuid(), Email = "leaver@example.com" };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var call = new LlmCall
        {
            Id = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            Sequence = 1,
            Kind = LlmCallKind.Tags,
            UserId = user.Id,
            UserEmail = user.Email!,
            Model = "m",
            Endpoint = "http://x/v1",
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            DurationMs = 10,
            Success = true,
        };
        db.LlmCalls.Add(call);
        await db.SaveChangesAsync();

        db.Users.Remove(await db.Users.SingleAsync(u => u.Id == user.Id));
        await db.SaveChangesAsync();

        await using var read = fx.CreateDbContext();
        var found = await read.LlmCalls.SingleAsync(c => c.Id == call.Id);
        Assert.Null(found.UserId);
        Assert.Equal("leaver@example.com", found.UserEmail);
    }

    [Fact]
    public async Task PersistAsync_NormalisesNonUtcTimestamps_BeforeSavingToPostgres()
    {
        // Npgsql THROWS at SaveChanges on a non-zero offset bound to timestamptz, and the EF in-memory
        // provider does not enforce that - so this can only be proven against real Postgres. It exercises
        // LlmUsageBatch.PersistAsync directly (the writer's actual persistence path), not a hand-rolled
        // already-UTC record inserted straight through DbContext.
        await using var db = fx.CreateDbContext();
        var nonUtcStarted = DateTimeOffset.Now.ToOffset(TimeSpan.FromHours(1));
        var call = new LlmCall
        {
            Id = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            Sequence = 1,
            Kind = LlmCallKind.Tags,
            UserEmail = "offset@example.com",
            Model = "m",
            Endpoint = "http://x/v1",
            StartedAt = nonUtcStarted,
            CompletedAt = nonUtcStarted.AddSeconds(1),
            DurationMs = 1000,
            Success = true,
        };

        var saved = await LlmUsageBatch.PersistAsync(db, [call], loggingEnabled: true, CancellationToken.None);

        Assert.Equal(1, saved);
        await using var read = fx.CreateDbContext();
        var found = await read.LlmCalls.SingleAsync(c => c.Id == call.Id);
        Assert.Equal(TimeSpan.Zero, found.StartedAt.Offset); // stored as UTC despite the +1h input
        Assert.Equal(TimeSpan.Zero, found.CompletedAt.Offset);
        // Postgres timestamptz has microsecond precision vs .NET's 100ns ticks, so an exact Equal() would
        // be flaky on the sub-microsecond remainder - a millisecond tolerance still proves the same wall-
        // clock instant survived the UTC conversion and the round trip.
        Assert.True(
            Math.Abs((found.StartedAt - nonUtcStarted.ToUniversalTime()).TotalMilliseconds) < 1,
            $"Expected ~{nonUtcStarted.ToUniversalTime():O}, got {found.StartedAt:O}");
    }

    [Fact]
    public async Task PersistAsync_DiscardsTheBatch_WhenLoggingIsDisabled()
    {
        await using var db = fx.CreateDbContext();
        var call = new LlmCall
        {
            Id = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            Sequence = 1,
            Kind = LlmCallKind.Tags,
            UserEmail = "disabled@example.com",
            Model = "m",
            Endpoint = "http://x/v1",
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            DurationMs = 10,
            Success = true,
        };

        var saved = await LlmUsageBatch.PersistAsync(db, [call], loggingEnabled: false, CancellationToken.None);

        Assert.Equal(0, saved);
        await using var read = fx.CreateDbContext();
        Assert.False(await read.LlmCalls.AnyAsync(c => c.Id == call.Id));
    }

    [Fact]
    public async Task RetentionSweep_DeletesOnlyRowsOlderThanTheWindow()
    {
        await using var db = fx.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        var marker = Guid.NewGuid();

        db.LlmCalls.AddRange(
            Row(marker, now.AddDays(-100)),
            Row(marker, now.AddDays(-91)),
            Row(marker, now.AddDays(-89)),
            Row(marker, now));
        await db.SaveChangesAsync();

        var deleted = await LlmUsageRetentionSweep.RunAsync(
            db, now, retentionDays: 90, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(2, deleted);
        Assert.Equal(2, await db.LlmCalls.CountAsync(c => c.OperationId == marker));
    }

    [Fact]
    public async Task RetentionSweep_DeletesNothing_WhenRetentionIsZero()
    {
        // 0 means keep forever. Treating it as "delete everything older than today" would silently
        // destroy the whole log the first night after an admin typed 0 meaning "no limit".
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        db.LlmCalls.Add(Row(marker, DateTimeOffset.UtcNow.AddYears(-5)));
        await db.SaveChangesAsync();

        var deleted = await LlmUsageRetentionSweep.RunAsync(
            db, DateTimeOffset.UtcNow, retentionDays: 0, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(0, deleted);
        Assert.Equal(1, await db.LlmCalls.CountAsync(c => c.OperationId == marker));
    }

    private static LlmCall Row(Guid operationId, DateTimeOffset startedAt) => new()
    {
        Id = Guid.NewGuid(), OperationId = operationId, Sequence = 1, Kind = LlmCallKind.Tags,
        Model = "m", Endpoint = "http://x/v1", StartedAt = startedAt, CompletedAt = startedAt,
        DurationMs = 1, Success = true,
    };
}
