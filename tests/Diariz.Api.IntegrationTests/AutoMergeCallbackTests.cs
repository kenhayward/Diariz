using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Services.Llm;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Diariz.Api.IntegrationTests;

/// <summary>Real-Postgres check of the auto-merge hook in <see cref="WorkerCallbackController.Result"/>.
/// The unit tests for it run on the EF in-memory provider, which does not faithfully translate relational
/// queries; the merge depends on reading segments back in <c>Ordinal</c> order, so it is pinned here too.</summary>
[Collection(IntegrationCollection.Name)]
public class AutoMergeCallbackTests(ContainersFixture fx)
{
    private const string Secret = "shared-secret";

    private static WorkerCallbackController Build(Diariz.Domain.DiarizDbContext db)
    {
        var resolver = new LlmSettingsResolver(
            db, Options.Create(new LlmDefaultsOptions()),
            Options.Create(new SummarizationOptions { ApiBase = "" }), new FakeApiKeyProtector());
        var embedding = new EmbeddingSettingsResolver(db, Options.Create(new EmbeddingOptions()), resolver);
        return new WorkerCallbackController(
            db, new FakeHubContext(), new FakeJobQueue(), resolver, embedding, new FakeSpeakerIdentifier(),
            Options.Create(new WorkerOptions { CallbackSecret = Secret }),
            new CapturingWebhookPublisher(), Options.Create(new AppPublicOptions()),
            NullLogger<WorkerCallbackController>.Instance)
        {
            ControllerContext = Http.Context(headers: ("X-Worker-Secret", Secret))
        };
    }

    [Fact]
    public async Task Result_WithAutoMergeOn_CollapsesSameSpeakerRunsInOrdinalOrder()
    {
        Guid userId = Guid.NewGuid(), recordingId = Guid.NewGuid(), transcriptionId = Guid.NewGuid();
        await using (var db = fx.CreateDbContext())
        {
            db.Users.Add(new ApplicationUser { Id = userId, Email = $"{userId}@t.test", UserName = $"{userId}@t.test" });
            db.Recordings.Add(new Recording { Id = recordingId, UserId = userId, Title = "t", BlobKey = "k", Status = RecordingStatus.Queued });
            db.Transcriptions.Add(new Transcription { Id = transcriptionId, RecordingId = recordingId, Model = "m", Version = 1 });
            db.UserSettings.Add(new UserSettings { UserId = userId, AutoMergeSpeakerSegments = true });
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
            await Build(db).Result(new TranscriptionResult(transcriptionId, "en",
            [
                new SegmentResult("SPEAKER_00", 0, 1000, "one"),
                new SegmentResult("SPEAKER_00", 1000, 2000, "two"),
                new SegmentResult("SPEAKER_01", 2000, 3000, "three"),
                new SegmentResult("SPEAKER_01", 3000, 4000, "four"),
                new SegmentResult("SPEAKER_00", 4000, 5000, "five"),
            ]));

        await using var verify = fx.CreateDbContext();
        var segs = await verify.Segments.Where(s => s.TranscriptionId == transcriptionId)
            .OrderBy(s => s.Ordinal).ToListAsync();

        Assert.Equal(3, segs.Count);
        Assert.Equal("one\ntwo", segs[0].EffectiveText);
        Assert.Equal("three\nfour", segs[1].EffectiveText);
        Assert.Equal("five", segs[2].EffectiveText);
        Assert.Equal([0, 1, 2], segs.Select(s => s.Ordinal).ToArray());
        Assert.Equal(0, segs[0].StartMs);
        Assert.Equal(2000, segs[0].EndMs);
    }
}
