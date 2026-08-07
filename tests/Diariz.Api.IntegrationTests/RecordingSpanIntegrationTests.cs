using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

/// <summary>Real-Postgres coverage for a recording's wall-clock span (<c>StartedAt</c>/<c>EndedAt</c>).
/// <para>Two things can only be verified here. The columns are <c>timestamptz</c>, and Npgsql <b>rejects a
/// non-zero-offset <see cref="DateTimeOffset"/></b> for that type - a constraint the in-memory provider does
/// not model at all, so a controller that forgot to normalise to UTC would pass every unit test and throw in
/// production. And the <c>AddRecordingStartedAt</c> backfill is raw SQL that only ever runs against
/// Postgres.</para></summary>
[Collection(IntegrationCollection.Name)]
public class RecordingSpanIntegrationTests(ContainersFixture fx)
{
    private async Task<Guid> SeedUser()
    {
        await using var db = fx.CreateDbContext();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>A UTC-normalised span round-trips through the real timestamptz columns.</summary>
    [Fact]
    public async Task Span_RoundTripsThroughTimestamptz()
    {
        var userId = await SeedUser();
        var id = Guid.NewGuid();
        var started = DateTimeOffset.Parse("2026-07-02T09:00:00Z");
        var ended = DateTimeOffset.Parse("2026-07-02T10:00:00Z");

        await using (var db = fx.CreateDbContext())
        {
            db.Recordings.Add(new Recording
            {
                Id = id, UserId = userId, BlobKey = $"{userId}/{id}.webm", DurationMs = 3_600_000,
                StartedAt = started, EndedAt = ended,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
        {
            var rec = await db.Recordings.SingleAsync(r => r.Id == id);
            Assert.Equal(started, rec.StartedAt);
            Assert.Equal(ended, rec.EndedAt);
        }
    }

    /// <summary>The columns are nullable, so an uploaded file (which has no knowable capture time) stores
    /// nothing rather than a placeholder - callers fall back to <c>CreatedAt</c>.</summary>
    [Fact]
    public async Task Span_MayBeNull()
    {
        var userId = await SeedUser();
        var id = Guid.NewGuid();

        await using (var db = fx.CreateDbContext())
        {
            db.Recordings.Add(new Recording
            {
                Id = id, UserId = userId, BlobKey = $"{userId}/{id}.m4a", Source = RecordingSource.Upload,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
        {
            var rec = await db.Recordings.SingleAsync(r => r.Id == id);
            Assert.Null(rec.StartedAt);
            Assert.Null(rec.EndedAt);
        }
    }

    /// <summary>The backfill's own expression, run against real Postgres. Pins the semantics the migration
    /// applied once to existing rows: subtract the recorded duration from the upload time, but only for rows
    /// that were actually recorded (an upload's <c>CreatedAt</c> says nothing about when its audio was made)
    /// and only where there is a duration to subtract.</summary>
    [Fact]
    public async Task BackfillExpression_EstimatesOnlyRecordedRowsWithADuration()
    {
        var userId = await SeedUser();
        var createdAt = DateTimeOffset.Parse("2026-07-02T10:00:00Z");
        var recorded = Guid.NewGuid();
        var uploaded = Guid.NewGuid();
        var zeroLength = Guid.NewGuid();

        await using (var db = fx.CreateDbContext())
        {
            db.Recordings.AddRange(
                new Recording
                {
                    Id = recorded, UserId = userId, BlobKey = $"{userId}/{recorded}.webm",
                    Source = RecordingSource.Microphone, CreatedAt = createdAt, DurationMs = 3_600_000,
                },
                new Recording
                {
                    Id = uploaded, UserId = userId, BlobKey = $"{userId}/{uploaded}.m4a",
                    Source = RecordingSource.Upload, CreatedAt = createdAt, DurationMs = 3_600_000,
                },
                new Recording
                {
                    Id = zeroLength, UserId = userId, BlobKey = $"{userId}/{zeroLength}.webm",
                    Source = RecordingSource.Microphone, CreatedAt = createdAt, DurationMs = 0,
                });
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                UPDATE "Recordings"
                   SET "StartedAt" = "CreatedAt" - ("DurationMs" * INTERVAL '1 millisecond')
                 WHERE "Source" <> 2 AND "DurationMs" > 0 AND "UserId" = {0};
                """.Replace("{0}", $"'{userId}'"));
        }

        await using (var db = fx.CreateDbContext())
        {
            // An hour of audio uploaded at 10:00 is estimated to have started at 09:00.
            Assert.Equal(
                DateTimeOffset.Parse("2026-07-02T09:00:00Z"),
                (await db.Recordings.SingleAsync(r => r.Id == recorded)).StartedAt);
            Assert.Null((await db.Recordings.SingleAsync(r => r.Id == uploaded)).StartedAt);
            Assert.Null((await db.Recordings.SingleAsync(r => r.Id == zeroLength)).StartedAt);
        }
    }
}
