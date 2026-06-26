using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

public class WorkerCallbackControllerTests
{
    private const string Secret = "shared-secret";

    // Default: summarisation NOT configured, so Result leaves the recording at Transcribed.
    private static (WorkerCallbackController controller, DiarizDbContext db, FakeHubContext hub) Build(string presentedSecret)
    {
        var (controller, db, hub, _) = BuildEx(presentedSecret, summarizationEnabled: false);
        return (controller, db, hub);
    }

    private static (WorkerCallbackController controller, DiarizDbContext db, FakeHubContext hub, FakeJobQueue queue)
        BuildEx(string presentedSecret, bool summarizationEnabled)
    {
        var db = TestDb.Create();
        var hub = new FakeHubContext();
        var queue = new FakeJobQueue();
        var resolver = new SummarizationSettingsResolver(
            db,
            Options.Create(new SummarizationOptions { ApiBase = summarizationEnabled ? "http://llm.test/v1" : "" }),
            new FakeApiKeyProtector());
        var controller = new WorkerCallbackController(
            db, hub, queue, resolver, Options.Create(new WorkerOptions { CallbackSecret = Secret }))
        {
            ControllerContext = Http.Context(headers: ("X-Worker-Secret", presentedSecret))
        };
        return (controller, db, hub, queue);
    }

    private static async Task<(Guid recordingId, Guid transcriptionId)> SeedQueuedRecording(DiarizDbContext db, Guid userId)
    {
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, Status = RecordingStatus.Queued };
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "whisperx-large-v3", Version = 1 };
        db.Recordings.Add(rec);
        db.Transcriptions.Add(tr);
        await db.SaveChangesAsync();
        return (rec.Id, tr.Id);
    }

    [Fact]
    public async Task Result_WithWrongSecret_ReturnsUnauthorized()
    {
        var (controller, db, _) = Build(presentedSecret: "not-the-secret");
        var (_, transcriptionId) = await SeedQueuedRecording(db, Guid.NewGuid());

        var result = await controller.Result(new TranscriptionResult(transcriptionId, "en", []));

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task Result_PersistsSegments_SeedsSpeakers_AndMarksTranscribed()
    {
        var (controller, db, hub) = Build(presentedSecret: Secret);
        var userId = Guid.NewGuid();
        var (recordingId, transcriptionId) = await SeedQueuedRecording(db, userId);

        var body = new TranscriptionResult(transcriptionId, "en",
        [
            new SegmentResult("SPEAKER_00", 0, 1000, "Hello"),
            new SegmentResult("SPEAKER_01", 1000, 2000, "Hi there"),
            new SegmentResult("SPEAKER_00", 2000, 3000, "How are you"),
        ]);

        var result = await controller.Result(body);

        Assert.IsType<OkResult>(result);

        var segments = await db.Segments.Where(s => s.TranscriptionId == transcriptionId)
            .OrderBy(s => s.Ordinal).ToListAsync();
        Assert.Equal(3, segments.Count);
        Assert.Equal([0, 1, 2], segments.Select(s => s.Ordinal));
        Assert.Equal("Hello", segments[0].Text);

        // One Speaker row per distinct label, default display name = label.
        var speakers = await db.Speakers.Where(s => s.RecordingId == recordingId).ToListAsync();
        Assert.Equal(2, speakers.Count);
        Assert.All(speakers, s => Assert.Equal(s.Label, s.DisplayName));

        var rec = await db.Recordings.FindAsync(recordingId);
        Assert.Equal(RecordingStatus.Transcribed, rec!.Status);

        // The owning user was notified over SignalR.
        var msg = Assert.Single(hub.Sent);
        Assert.Equal(userId.ToString(), msg.Group);
        Assert.Equal("RecordingStatusChanged", msg.Method);
    }

    [Fact]
    public async Task Result_ClearsStaleErrorFromPreviousFailure()
    {
        var (controller, db, _) = Build(presentedSecret: Secret);
        var userId = Guid.NewGuid();
        var (recordingId, transcriptionId) = await SeedQueuedRecording(db, userId);

        // A prior attempt failed and left an error; a successful re-transcribe must clear it
        // so the recording does not show a stale error banner after it actually succeeds.
        var rec = await db.Recordings.FindAsync(recordingId);
        rec!.Error = "previous attempt blew up";
        await db.SaveChangesAsync();

        await controller.Result(new TranscriptionResult(transcriptionId, "en",
            [new SegmentResult("SPEAKER_00", 0, 1000, "Hello")]));

        var refreshed = await db.Recordings.FindAsync(recordingId);
        Assert.Equal(RecordingStatus.Transcribed, refreshed!.Status);
        Assert.Null(refreshed.Error);
    }

    [Fact]
    public async Task Result_BlankSpeakerLabel_StoredAsUnknown()
    {
        var (controller, db, _) = Build(presentedSecret: Secret);
        var (_, transcriptionId) = await SeedQueuedRecording(db, Guid.NewGuid());

        await controller.Result(new TranscriptionResult(transcriptionId, "en",
            [new SegmentResult("", 0, 500, "mumble")]));

        var seg = await db.Segments.SingleAsync(s => s.TranscriptionId == transcriptionId);
        Assert.Equal("UNKNOWN", seg.SpeakerLabel);
    }

    [Fact]
    public async Task Result_WhenSummarisationConfigured_MarksSummarizing_AndEnqueuesJob()
    {
        var (controller, db, hub, queue) = BuildEx(Secret, summarizationEnabled: true);
        var (recordingId, transcriptionId) = await SeedQueuedRecording(db, Guid.NewGuid());

        await controller.Result(new TranscriptionResult(transcriptionId, "en",
            [new SegmentResult("SPEAKER_00", 0, 1000, "Hello")]));

        // Transcript still persisted, but the pipeline auto-continues into summarisation.
        var rec = await db.Recordings.FindAsync(recordingId);
        Assert.Equal(RecordingStatus.Summarizing, rec!.Status);
        var job = Assert.Single(queue.SummarizationEnqueued);
        Assert.Equal(recordingId, job.RecordingId);
        Assert.Equal(transcriptionId, job.TranscriptionId);
        // The owner is notified with the new status.
        Assert.Contains(hub.Sent, m => m.Method == "RecordingStatusChanged");
    }

    [Fact]
    public async Task Result_WhenSummarisationNotConfigured_StaysTranscribed_AndDoesNotEnqueue()
    {
        var (controller, db, _, queue) = BuildEx(Secret, summarizationEnabled: false);
        var (recordingId, transcriptionId) = await SeedQueuedRecording(db, Guid.NewGuid());

        await controller.Result(new TranscriptionResult(transcriptionId, "en",
            [new SegmentResult("SPEAKER_00", 0, 1000, "Hello")]));

        Assert.Equal(RecordingStatus.Transcribed, (await db.Recordings.FindAsync(recordingId))!.Status);
        Assert.Empty(queue.SummarizationEnqueued);
    }

    [Fact]
    public async Task Failure_RecordsErrorAndMarksFailed()
    {
        var (controller, db, hub) = Build(presentedSecret: Secret);
        var (recordingId, transcriptionId) = await SeedQueuedRecording(db, Guid.NewGuid());

        var result = await controller.Failure(new TranscriptionFailure(transcriptionId, "model exploded"));

        Assert.IsType<OkResult>(result);
        var rec = await db.Recordings.FindAsync(recordingId);
        Assert.Equal(RecordingStatus.Failed, rec!.Status);
        Assert.Equal("model exploded", rec.Error);
        Assert.Single(hub.Sent);
    }
}
