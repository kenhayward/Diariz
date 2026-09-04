using System.Text;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

/// <summary>
/// Recovering from "Live transcript paused" (issue #758).
///
/// <para>The pause used to be a <b>latch</b>. The gate refuses to queue live work while the oldest
/// chunk that has not come back has been waiting longer than <c>Live:MaxLagSeconds</c> - and a refused
/// chunk was never queued, so it never came back either, so it became the new oldest. One failed chunk
/// was enough to end the live transcript for the rest of the meeting, while both the code and the help
/// promised it would resume once the transcriber caught up.</para>
///
/// <para>The fix is that a chunk stops counting as outstanding the moment the live pass is done with
/// it, for any of the three reasons it can be: transcribed, failed, or never sent.</para>
/// </summary>
public class LivePauseRecoveryTests
{
    private const string Secret = "s3cret";

    private static BeginLiveRecordingRequest Req(Guid sessionId) =>
        new("Standup", RecordingSource.Microphone, null, null, null, sessionId, 30 * 60 * 1000);

    private static async Task<(Guid Id, Guid Session)> BeginAsync(
        DiarizDbContext db, Guid userId, RecordingsController controller)
    {
        var session = Guid.NewGuid();
        var result = await controller.BeginLive(Req(session));
        return (((LiveRecordingDto)((CreatedAtActionResult)result.Result!).Value!).Id, session);
    }

    private static LiveChunkCallbackController Callback(DiarizDbContext db) =>
        new(db, new FakeHubContext(), Options.Create(new WorkerOptions { CallbackSecret = Secret }))
        {
            ControllerContext = Http.Context(Guid.NewGuid(), ("X-Worker-Secret", Secret)),
        };

    private static FormFile Chunk(string body = "chunk-bytes") =>
        new(new MemoryStream(Encoding.UTF8.GetBytes(body)), 0, Encoding.UTF8.GetByteCount(body),
            "chunk", "chunk.webm")
        {
            Headers = new HeaderDictionary(),
            ContentType = "audio/webm",
        };

    [Fact]
    public async Task AFailedChunkIsSettled_SoItStopsHoldingTheGateOpen()
    {
        // The failure path already promises a failed chunk "costs a gap in the live transcript and
        // nothing else". It has to record that the chunk is done with, or it costs the rest of the
        // meeting instead.
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me);
        var controller = LiveTestSupport.Build(db, me, storage: new FakeAudioStorage());
        var (id, session) = await BeginAsync(db, me, controller);
        await controller.PutChunk(id, 0, Chunk(), session, 0, 6_000);
        var tr = await db.Transcriptions.FirstOrDefaultAsync(t => t.RecordingId == id);

        await Callback(db).LiveChunkFailure(new LiveChunkFailure(id, tr?.Id ?? Guid.Empty, 0, "boom"));

        var chunk = await db.RecordingChunks.SingleAsync(c => c.RecordingId == id && c.Sequence == 0);
        Assert.NotNull(chunk.SettledAt);
    }

    [Fact]
    public async Task ARefusedChunkIsSettled_SoTheRefusalDoesNotFeedItself()
    {
        // The heart of the latch. A refused chunk was never queued, so it never came back, so it became
        // the oldest outstanding one and refused the next - and so on to the end of the meeting.
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me);
        var queue = new FakeJobQueue();
        var controller = LiveTestSupport.Build(db, me, queue, new FakeAudioStorage());
        var (id, session) = await BeginAsync(db, me, controller);

        // A chunk that has been outstanding far longer than the threshold: the transcriber is behind.
        await controller.PutChunk(id, 0, Chunk(), session, 0, 6_000);
        var stuck = await db.RecordingChunks.SingleAsync(c => c.RecordingId == id && c.Sequence == 0);
        stuck.ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await db.SaveChangesAsync();

        await controller.PutChunk(id, 1, Chunk(), session, 6_000, 12_000);

        var refused = await db.RecordingChunks.SingleAsync(c => c.RecordingId == id && c.Sequence == 1);
        Assert.NotNull(refused.SettledAt);
    }

    [Fact]
    public async Task TheTranscriptResumesOnceTheTranscriberHasCaughtUp()
    {
        // The property both the code comment and the help article promise, and the one the latch broke.
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me);
        var queue = new FakeJobQueue();
        var controller = LiveTestSupport.Build(db, me, queue, new FakeAudioStorage());
        var (id, session) = await BeginAsync(db, me, controller);

        // Chunk 0 goes long-outstanding, so chunk 1 is refused.
        await controller.PutChunk(id, 0, Chunk(), session, 0, 6_000);
        var stuck = await db.RecordingChunks.SingleAsync(c => c.RecordingId == id && c.Sequence == 0);
        stuck.ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await db.SaveChangesAsync();
        await controller.PutChunk(id, 1, Chunk(), session, 6_000, 12_000);
        var queuedWhileBehind = queue.LiveChunkEnqueued.Count;

        // The transcriber catches up: the stuck chunk comes back.
        stuck.SettledAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await controller.PutChunk(id, 2, Chunk(), session, 12_000, 18_000);

        Assert.True(queue.LiveChunkEnqueued.Count > queuedWhileBehind,
            "a chunk arriving after the backlog cleared must be queued again");
        Assert.Contains(queue.LiveChunkEnqueued, j => j.Sequence == 2);
    }

    [Fact]
    public async Task AGenuineBacklogStillPauses()
    {
        // The gate has to keep doing its job. Queueing into an unbounded backlog is what it exists to
        // prevent, and a fix that simply never paused would be worse than the latch.
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me);
        var queue = new FakeJobQueue();
        var controller = LiveTestSupport.Build(db, me, queue, new FakeAudioStorage());
        var (id, session) = await BeginAsync(db, me, controller);

        await controller.PutChunk(id, 0, Chunk(), session, 0, 6_000);
        var stuck = await db.RecordingChunks.SingleAsync(c => c.RecordingId == id && c.Sequence == 0);
        stuck.ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await db.SaveChangesAsync();
        var before = queue.LiveChunkEnqueued.Count;

        await controller.PutChunk(id, 1, Chunk(), session, 6_000, 12_000);

        Assert.Equal(before, queue.LiveChunkEnqueued.Count);
        Assert.DoesNotContain(queue.LiveChunkEnqueued, j => j.Sequence == 1);
    }

    [Fact]
    public async Task OneFailedChunkDoesNotPauseTheRestOfTheMeeting()
    {
        // The reported symptom, end to end: a chunk fails, and everything after it still goes through.
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me);
        var queue = new FakeJobQueue();
        var controller = LiveTestSupport.Build(db, me, queue, new FakeAudioStorage());
        var (id, session) = await BeginAsync(db, me, controller);

        await controller.PutChunk(id, 0, Chunk(), session, 0, 6_000);
        var failed = await db.RecordingChunks.SingleAsync(c => c.RecordingId == id && c.Sequence == 0);
        // The worker took it, failed, and reported so - a while ago.
        failed.ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await db.SaveChangesAsync();
        var tr = await db.Transcriptions.FirstOrDefaultAsync(t => t.RecordingId == id);
        await Callback(db).LiveChunkFailure(new LiveChunkFailure(id, tr?.Id ?? Guid.Empty, 0, "boom"));

        for (var seq = 1; seq <= 3; seq++)
            await controller.PutChunk(id, seq, Chunk(), session, seq * 6_000, (seq + 1) * 6_000);

        Assert.Equal([1, 2, 3], queue.LiveChunkEnqueued.Where(j => j.Sequence > 0)
            .Select(j => j.Sequence).OrderBy(s => s));
    }
}
