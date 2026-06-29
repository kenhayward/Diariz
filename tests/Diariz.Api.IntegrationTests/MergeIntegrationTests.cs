using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diariz.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public class MergeIntegrationTests(ContainersFixture fx)
{
    private static WorkerMergeCallbackController BuildCallback(Diariz.Domain.DiarizDbContext db) =>
        new(db, new FakeHubContext(), new FakeAudioStorage(),
            Options.Create(new WorkerOptions { CallbackSecret = "s" }))
        {
            ControllerContext = WithSecret(Http.Context(Guid.NewGuid()), "s"),
        };

    private static Microsoft.AspNetCore.Mvc.ControllerContext WithSecret(
        Microsoft.AspNetCore.Mvc.ControllerContext ctx, string secret)
    {
        ctx.HttpContext.Request.Headers["X-Worker-Secret"] = secret;
        return ctx;
    }

    [Fact]
    public async Task MergeResult_DeletesSources_CascadesTheirData_AndReconcilesQuota()
    {
        Guid userId, survivorId, otherId, otherTrId;
        await using (var db = fx.CreateDbContext())
        {
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
            var survivor = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = "u/old.webm", SizeBytes = 100, Status = RecordingStatus.Merging };
            var other = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = "u/other.webm", SizeBytes = 200 };
            var otherTr = new Transcription { Id = Guid.NewGuid(), RecordingId = other.Id, Model = "m", Version = 1 };
            db.AddRange(user, survivor, other, otherTr,
                new Segment { Id = Guid.NewGuid(), TranscriptionId = otherTr.Id, SpeakerLabel = "SPEAKER_00", StartMs = 0, EndMs = 1, Original = "x", Ordinal = 0 },
                new Speaker { Id = Guid.NewGuid(), RecordingId = other.Id, Label = "SPEAKER_00", DisplayName = "A" });
            await db.SaveChangesAsync();
            (userId, survivorId, otherId, otherTrId) = (user.Id, survivor.Id, other.Id, otherTr.Id);
        }

        await using (var db = fx.CreateDbContext())
        {
            Assert.Equal(300, await new StorageUsage(db).UsedBytesAsync(userId)); // both count before merge
            Assert.IsType<OkResult>(await BuildCallback(db).Result(
                new AudioMergeResult(survivorId, "u/merged.webm", "audio/webm", SizeBytes: 250, DurationMs: 5000, [otherId])));
        }

        await using var verify = fx.CreateDbContext();
        var survivor2 = (await verify.Recordings.FindAsync(survivorId))!;
        Assert.Equal("u/merged.webm", survivor2.BlobKey);
        Assert.Equal(250, survivor2.SizeBytes);
        Assert.Equal(RecordingStatus.Transcribed, survivor2.Status);
        Assert.Null(await verify.Recordings.FindAsync(otherId));                       // source gone
        Assert.False(await verify.Transcriptions.AnyAsync(t => t.Id == otherTrId));     // cascade
        Assert.False(await verify.Segments.AnyAsync(s => s.TranscriptionId == otherTrId));
        Assert.False(await verify.Speakers.AnyAsync(s => s.RecordingId == otherId));
        Assert.Equal(250, await new StorageUsage(verify).UsedBytesAsync(userId));       // only the combined blob counts
    }
}
