using Diariz.Api.Configuration;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Diariz.Api.IntegrationTests;

/// <summary>Real-Postgres check that a screenshot's capture time - not just a note's - breaks a same-speaker
/// merge boundary. <see cref="RecordingsController.MergeSegments"/> unions note and screenshot capture times
/// before calling <see cref="TranscriptNoteAnchor.BreakBeforeIndices"/>; missing the screenshot union would
/// let two same-speaker segments either side of a screenshot collapse into one block.</summary>
[Collection(IntegrationCollection.Name)]
public class ScreenshotMergeBreakTests(ContainersFixture fx)
{
    private static RecordingsController Build(Diariz.Domain.DiarizDbContext db, Guid userId)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Transcription:DefaultModel"] = "whisperx-large-v3" })
            .Build();
        var resolver = new SummarizationSettingsResolver(
            db, Options.Create(new SummarizationOptions { ApiBase = "http://llm.test/v1" }), new FakeApiKeyProtector());
        return new RecordingsController(db, new FakeAudioStorage(), new FakeJobQueue(), new FakeHubContext(), config,
            resolver, new FakeEmailSender(), new FakeSpeakerIdentifier(), Options.Create(new UploadOptions()), new RoomScope(db))
        {
            ControllerContext = Http.Context(userId)
        };
    }

    [Fact]
    public async Task MergeSegments_KeepsSameSpeakerSegmentsSeparate_WhenAScreenshotSitsBetweenThem()
    {
        Guid userId, recordingId, transcriptionId;
        await using (var db = fx.CreateDbContext())
        {
            userId = Guid.NewGuid();
            recordingId = Guid.NewGuid();
            transcriptionId = Guid.NewGuid();

            db.Users.Add(new ApplicationUser { Id = userId, Email = $"{userId}@t.test", UserName = $"{userId}@t.test" });
            db.Recordings.Add(new Recording { Id = recordingId, UserId = userId, Title = "t", BlobKey = "k" });
            db.Transcriptions.Add(new Transcription { Id = transcriptionId, RecordingId = recordingId, Model = "m", Version = 1 });
            db.Segments.AddRange(
                new Segment { Id = Guid.NewGuid(), TranscriptionId = transcriptionId, SpeakerLabel = "SPEAKER_00", StartMs = 0, EndMs = 5_000, Original = "first", Ordinal = 0 },
                new Segment { Id = Guid.NewGuid(), TranscriptionId = transcriptionId, SpeakerLabel = "SPEAKER_00", StartMs = 6_000, EndMs = 9_000, Original = "second", Ordinal = 1 });
            db.MeetingScreenshots.Add(new MeetingScreenshot
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                RecordingId = recordingId,
                CapturedAtMs = 5_500, // between the two same-speaker segments
                BlobKey = "s.png",
                ThumbBlobKey = "s.thumb.jpg",
                SizeBytes = 10,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
            await Build(db, userId).MergeSegments(recordingId);

        await using var verify = fx.CreateDbContext();
        var detail = (await Build(verify, userId).Get(recordingId)).Value!;

        Assert.Equal(2, detail.Current!.Segments.Count); // the screenshot boundary kept them apart
        Assert.Equal("first", detail.Current.Segments[0].Text);
        Assert.Equal("second", detail.Current.Segments[1].Text);
    }
}
