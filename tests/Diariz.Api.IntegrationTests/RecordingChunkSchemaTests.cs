using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

/// <summary>
/// The unique constraint and the cascade are the whole point of <see cref="RecordingChunk"/>, and the
/// in-memory provider enforces neither - so these live here from the start rather than as unit tests
/// that would pass for the wrong reason.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class RecordingChunkSchemaTests(ContainersFixture fx)
{
    private async Task<Guid> SeedLiveRecordingAsync()
    {
        await using var db = fx.CreateDbContext();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"{Guid.NewGuid()}@x.test",
            Email = $"{Guid.NewGuid()}@x.test",
        };
        db.Users.Add(user);
        var rec = new Recording
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Title = "Live take",
            Status = RecordingStatus.Live,
            BlobKey = "",
        };
        db.Recordings.Add(rec);
        await db.SaveChangesAsync();
        return rec.Id;
    }

    private static RecordingChunk NewChunk(Guid recordingId, int sequence) => new()
    {
        Id = Guid.NewGuid(),
        RecordingId = recordingId,
        Sequence = sequence,
        BlobKey = $"u/{recordingId}/chunks/{sequence:D5}.webm",
        StartMs = sequence * 30_000,
        EndMs = (sequence + 1) * 30_000,
        SizeBytes = 32_000,
        ReceivedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task DuplicateSequenceForOneRecording_IsRejected()
    {
        var recordingId = await SeedLiveRecordingAsync();

        await using var db = fx.CreateDbContext();
        db.RecordingChunks.Add(NewChunk(recordingId, 0));
        await db.SaveChangesAsync();

        db.RecordingChunks.Add(NewChunk(recordingId, 0));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task SameSequenceOnADifferentRecording_IsAllowed()
    {
        // The constraint is (RecordingId, Sequence), not Sequence - two concurrent live recordings
        // both start at 0, and a constraint on Sequence alone would make the second one impossible.
        var first = await SeedLiveRecordingAsync();
        var second = await SeedLiveRecordingAsync();

        await using var db = fx.CreateDbContext();
        db.RecordingChunks.Add(NewChunk(first, 0));
        db.RecordingChunks.Add(NewChunk(second, 0));
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.RecordingChunks.CountAsync(c => c.Sequence == 0
            && (c.RecordingId == first || c.RecordingId == second)));
    }

    [Fact]
    public async Task DeletingTheRecording_CascadesTheChunks()
    {
        var recordingId = await SeedLiveRecordingAsync();

        await using (var seed = fx.CreateDbContext())
        {
            seed.RecordingChunks.AddRange(NewChunk(recordingId, 0), NewChunk(recordingId, 1));
            await seed.SaveChangesAsync();
        }

        await using var db = fx.CreateDbContext();
        db.Recordings.Remove(await db.Recordings.SingleAsync(r => r.Id == recordingId));
        await db.SaveChangesAsync();

        Assert.Equal(0, await db.RecordingChunks.CountAsync(c => c.RecordingId == recordingId));
    }

    [Fact]
    public async Task LiveStatus_RoundTripsAsEight()
    {
        // RecordingStatus is persisted as an int and is append-only. If someone renumbers the enum,
        // every existing row silently changes meaning - so pin the wire value, not just the name.
        var recordingId = await SeedLiveRecordingAsync();

        await using var db = fx.CreateDbContext();
        // The alias is required, not cosmetic: EF's scalar SqlQuery<T> projects a column named "Value".
        var raw = await db.Database
            .SqlQuery<int>($"SELECT \"Status\" AS \"Value\" FROM \"Recordings\" WHERE \"Id\" = {recordingId}")
            .SingleAsync();

        Assert.Equal(8, raw);
        Assert.Equal(RecordingStatus.Live, (RecordingStatus)raw);
    }
}
