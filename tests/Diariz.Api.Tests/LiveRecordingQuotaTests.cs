using System.Text;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

/// <summary>
/// Quota across the whole live lifecycle. Split out from the endpoint tests because the interesting
/// behaviour is the sum of begin, chunk, finalise and reap - each of which looks fine alone.
/// </summary>
public class LiveRecordingQuotaTests
{
    private static FormFile Bytes(int n) =>
        new(new MemoryStream(new byte[n]), 0, n, "chunk", "chunk.webm")
        { Headers = new HeaderDictionary(), ContentType = "audio/webm" };

    private static async Task<(Guid Id, Guid Session)> Begin(
        RecordingsController controller, long expectedMs = 60_000)
    {
        var session = Guid.NewGuid();
        var begun = await controller.BeginLive(new BeginLiveRecordingRequest(
            "Standup", RecordingSource.Microphone, null, null, null, session, expectedMs));
        var dto = (LiveRecordingDto)((CreatedAtActionResult)begun.Result!).Value!;
        return (dto.Id, session);
    }

    [Fact]
    public async Task Chunks_AccumulateIntoTheRecordingsSize()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me);
        var controller = LiveTestSupport.Build(db, me);
        var (id, session) = await Begin(controller);

        await controller.PutChunk(id, 0, Bytes(1000), session, 0, 30_000);
        await controller.PutChunk(id, 1, Bytes(1500), session, 30_000, 60_000);

        Assert.Equal(2500, (await db.Recordings.SingleAsync(r => r.Id == id)).SizeBytes);
    }

    [Fact]
    public async Task AReplacedChunkSwapsItsContribution_RatherThanAddingToIt()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me);
        var controller = LiveTestSupport.Build(db, me);
        var (id, session) = await Begin(controller);

        await controller.PutChunk(id, 0, Bytes(1000), session, 0, 30_000);
        await controller.PutChunk(id, 0, Bytes(400), session, 0, 30_000);   // the retry

        Assert.Equal(400, (await db.Recordings.SingleAsync(r => r.Id == id)).SizeBytes);
    }

    [Fact]
    public async Task PutChunk_ThatWouldExceedQuota_Returns413_AndStoresNothing()
    {
        // Without this a live capture could run past the owner's quota indefinitely: the begin check
        // is a pre-flight estimate, and nothing else in the chunk path looks at quota.
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me, quotaBytes: 2000);
        var storage = new FakeAudioStorage();
        var controller = LiveTestSupport.Build(db, me, storage: storage);
        var (id, session) = await Begin(controller, expectedMs: 1);

        await controller.PutChunk(id, 0, Bytes(1500), session, 0, 30_000);
        var result = await controller.PutChunk(id, 1, Bytes(1500), session, 30_000, 60_000);

        Assert.Equal(413, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal(1500, (await db.Recordings.SingleAsync(r => r.Id == id)).SizeBytes);
        Assert.Single(await db.RecordingChunks.Where(c => c.RecordingId == id).ToListAsync());
        Assert.Single(storage.Objects);   // the rejected chunk's blob was never written
    }

    [Fact]
    public async Task AnInProgressLiveRecording_CountsTowardTheNextUploadsQuotaCheck()
    {
        // Otherwise a user could start a live capture and then upload files as though the capture were
        // free, because Upload sums Recordings.SizeBytes and a live row would contribute nothing.
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me, quotaBytes: 3000);
        var controller = LiveTestSupport.Build(db, me);
        // A short declared duration so the begin estimate itself fits inside the small quota -
        // this test is about what the chunks cost afterwards.
        var (id, session) = await Begin(controller, expectedMs: 1);
        await controller.PutChunk(id, 0, Bytes(2500), session, 0, 30_000);

        var upload = await controller.Upload(
            new FormFile(new MemoryStream(new byte[1000]), 0, 1000, "audio", "a.webm")
            { Headers = new HeaderDictionary(), ContentType = "audio/webm" },
            title: "File", durationMs: 1000);

        Assert.Equal(413, Assert.IsType<ObjectResult>(upload.Result).StatusCode);
    }

    [Fact]
    public async Task FinalizeCallback_ReconcilesTheSizeToTheConcatenatedBlob()
    {
        // The chunk total and the concatenated file are not the same number - ffmpeg re-encodes, and
        // every chunk carried container overhead. What the user is charged for is what is stored.
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me);
        var storage = new FakeAudioStorage();
        var queue = new FakeJobQueue();
        var controller = LiveTestSupport.Build(db, me, queue, storage);
        var (id, session) = await Begin(controller);
        await controller.PutChunk(id, 0, Bytes(5000), session, 0, 30_000);
        await controller.FinalizeLive(id);

        await LiveTestSupport.MergeCallback(db, storage, queue).Result(new AudioMergeResult(
            id, $"{me}/{id}-live.webm", "audio/webm", 4200, 30_000, [], "live-chunks"));

        Assert.Equal(4200, (await db.Recordings.SingleAsync(r => r.Id == id)).SizeBytes);
    }

    [Fact]
    public async Task ReapingAnEmptyCapture_ReleasesItsCharge()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me);
        var controller = LiveTestSupport.Build(db, me);
        var (id, _) = await Begin(controller);

        await controller.FinalizeLive(id);   // nothing ever arrived

        Assert.False(await db.Recordings.AnyAsync(r => r.Id == id));
        Assert.Equal(0, await db.Recordings.Where(r => r.UserId == me).SumAsync(r => r.SizeBytes));
    }
}
