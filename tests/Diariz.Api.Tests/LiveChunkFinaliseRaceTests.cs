using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

/// <summary>
/// What a live-chunk failure means once the meeting is over (issue #759).
///
/// <para>Shorter chunks put more jobs in flight, so several are still queued when Stop is pressed.
/// Finalise merges the chunk blobs into the canonical recording and deletes the individual ones, and
/// the jobs behind it then fail. The worker no longer reports the missing-blob case at all, but a
/// chunk that fails for any other reason in that same window still arrives here - and telling a page
/// its live transcript has a gap is meaningless once the live transcript has been replaced by the
/// full pass.</para>
/// </summary>
public class LiveChunkFinaliseRaceTests
{
    private const string Secret = "s3cret";

    private static (LiveChunkCallbackController Controller, FakeHubContext Hub) Callback(DiarizDbContext db)
    {
        var hub = new FakeHubContext();
        return (new LiveChunkCallbackController(db, hub,
            Options.Create(new WorkerOptions { CallbackSecret = Secret }))
        {
            ControllerContext = Http.Context(Guid.NewGuid(), ("X-Worker-Secret", Secret)),
        }, hub);
    }

    private static async Task<(Recording Rec, Transcription Tr)> Seed(
        DiarizDbContext db, RecordingStatus status)
    {
        var userId = Guid.NewGuid();
        Users.Ensure(db, userId);
        var rec = new Recording
        {
            Id = Guid.NewGuid(), UserId = userId, Title = "Standup", BlobKey = "",
            Status = status, LiveSessionId = Guid.NewGuid(),
        };
        var tr = new Transcription
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "whisperx-live",
            Version = 1, IsProvisional = true,
        };
        db.Recordings.Add(rec);
        db.Transcriptions.Add(tr);
        db.RecordingChunks.Add(new RecordingChunk
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Sequence = 4, BlobKey = "u/r/chunks/00004.webm",
            StartMs = 24_000, EndMs = 30_000, SizeBytes = 1, ReceivedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (rec, tr);
    }

    [Fact]
    public async Task AFailureArrivingAfterTheMeetingEndedIsNotAnnouncedToThePage()
    {
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, RecordingStatus.Transcribing);
        var (controller, hub) = Callback(db);

        await controller.LiveChunkFailure(new LiveChunkFailure(rec.Id, tr.Id, 4, "boom"));

        Assert.DoesNotContain(hub.Sent, m => m.Method == "LiveTranscriptDegraded");
    }

    [Fact]
    public async Task AFailureDuringTheMeetingIsStillAnnounced()
    {
        // The event is what turns a silent gap into a visible one. Suppressing it while the meeting
        // runs would be the opposite mistake, and a fix that never announced anything would pass a
        // test written only around the case above.
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, RecordingStatus.Live);
        var (controller, hub) = Callback(db);

        await controller.LiveChunkFailure(new LiveChunkFailure(rec.Id, tr.Id, 4, "boom"));

        Assert.Contains(hub.Sent, m => m.Method == "LiveTranscriptDegraded");
    }

    [Fact]
    public async Task TheChunkIsStillSettledEitherWay()
    {
        // Settling is bookkeeping the gate depends on (issue #758) and has nothing to do with whether
        // anybody is listening, so staying quiet must not quietly stop doing it.
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, RecordingStatus.Transcribing);
        var (controller, _) = Callback(db);

        await controller.LiveChunkFailure(new LiveChunkFailure(rec.Id, tr.Id, 4, "boom"));

        var chunk = db.RecordingChunks.Single(c => c.RecordingId == rec.Id && c.Sequence == 4);
        Assert.NotNull(chunk.SettledAt);
    }
}
