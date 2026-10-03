using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

/// <summary>Switching live transcription off settles the chunks still in flight - a correlated subquery the
/// in-memory provider would accept whatever it translated to, so it is checked against real Postgres.</summary>
[Collection(IntegrationCollection.Name)]
public class LiveTranscriptionSwitchIntegrationTests(ContainersFixture fx)
{
    [Fact]
    public async Task SwitchingOff_SettlesOutstandingChunksOfLiveRecordingsOnly()
    {
        await using var db = fx.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        var live = await Seed(db, RecordingStatus.Live);
        var finished = await Seed(db, RecordingStatus.Transcribing);

        // Not the platform's row: the switch is applied to a detached copy, so the shared singleton the
        // other integration tests read is left as it was.
        var settings = new PlatformSettings();
        await LiveTranscriptionSwitch.ApplyAsync(db, new FakeJobQueue(), settings, enabled: false, now);
        await db.SaveChangesAsync();

        await using var read = fx.CreateDbContext();
        Assert.All(await read.RecordingChunks.Where(c => c.RecordingId == live).ToListAsync(),
            c => Assert.NotNull(c.SettledAt));
        Assert.All(await read.RecordingChunks.Where(c => c.RecordingId == finished).ToListAsync(),
            c => Assert.Null(c.SettledAt));
    }

    private static async Task<Guid> Seed(DiarizDbContext db, RecordingStatus status)
    {
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = $"{userId}@x.test", Email = $"{userId}@x.test" });
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, Title = "Take", BlobKey = "", Status = status };
        db.Recordings.Add(rec);
        for (var i = 0; i < 2; i++)
            db.RecordingChunks.Add(new RecordingChunk
            {
                Id = Guid.NewGuid(), RecordingId = rec.Id, Sequence = i, BlobKey = $"{rec.Id}/{i}",
                StartMs = i * 10_000, EndMs = (i + 1) * 10_000, SizeBytes = 1,
                ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            });
        await db.SaveChangesAsync();
        return rec.Id;
    }
}
