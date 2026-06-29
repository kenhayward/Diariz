using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public class RecordingTranslationIntegrationTests(ContainersFixture fx)
{
    private static RecordingTranslationController Build(DiarizDbContext db, Guid userId, FakeTranslationClient client) =>
        new(db, client, new FakeSummarizationSettingsResolver()) { ControllerContext = Http.Context(userId) };

    [Fact]
    public async Task TranslateRecording_TranslatesOnlyTheHighestVersion_OverPostgres()
    {
        Guid userId, recId, v1SegId, v2SegId;
        await using (var db = fx.CreateDbContext())
        {
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
            var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = "k" };
            db.AddRange(user, rec);
            var v1 = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "m", Version = 1 };
            var v2 = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "m", Version = 2 };
            var s1 = new Segment { Id = Guid.NewGuid(), TranscriptionId = v1.Id, SpeakerLabel = "S0", StartMs = 0, EndMs = 1, Original = "old", Ordinal = 0 };
            var s2 = new Segment { Id = Guid.NewGuid(), TranscriptionId = v2.Id, SpeakerLabel = "S0", StartMs = 0, EndMs = 1, Original = "current", Ordinal = 0 };
            db.AddRange(v1, v2, s1, s2);
            await db.SaveChangesAsync();
            (userId, recId, v1SegId, v2SegId) = (user.Id, rec.Id, s1.Id, s2.Id);
        }

        await using (var db = fx.CreateDbContext())
            Assert.IsType<NoContentResult>(
                await Build(db, userId, new FakeTranslationClient()).TranslateRecording(recId, new TranslateRequest("es")));

        await using var verify = fx.CreateDbContext();
        Assert.Equal("[Spanish] current", (await verify.Segments.FindAsync(v2SegId))!.Revised); // current version translated
        Assert.Null((await verify.Segments.FindAsync(v1SegId))!.Revised);                       // old version untouched
    }
}
