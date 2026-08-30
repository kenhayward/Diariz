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
/// Finalising a live capture: the API's half (gap detection, enqueue) and the worker callback's half
/// (swap the blob, drop the chunks, hand over to the normal transcription pipeline).
/// </summary>
public class LiveFinalizeTests
{
    private static FormFile Chunk(string body = "chunk") =>
        new(new MemoryStream(Encoding.UTF8.GetBytes(body)), 0, Encoding.UTF8.GetByteCount(body),
            "chunk", "chunk.webm")
        { Headers = new HeaderDictionary(), ContentType = "audio/webm" };

    private static async Task<(Guid Id, Guid Session)> BeginWithChunks(
        DiarizDbContext db, Guid userId, RecordingsController controller, params int[] sequences)
    {
        var session = Guid.NewGuid();
        var begun = await controller.BeginLive(new BeginLiveRecordingRequest(
            "Standup", RecordingSource.Microphone, null, null, null, session, 60_000));
        var dto = (LiveRecordingDto)((CreatedAtActionResult)begun.Result!).Value!;
        foreach (var s in sequences)
            await controller.PutChunk(dto.Id, s, Chunk($"c{s}"), session, s * 30_000, (s + 1) * 30_000);
        return (dto.Id, session);
    }

    private static WorkerMergeCallbackController Callback(
        DiarizDbContext db, FakeAudioStorage storage, FakeJobQueue queue) =>
        new(db, new FakeHubContext(), storage, queue,
            Options.Create(new WorkerOptions { CallbackSecret = "s3cret" }))
        {
            ControllerContext = Http.Context(Guid.NewGuid(), ("X-Worker-Secret", "s3cret")),
        };

    // ---- POST /api/recordings/{id}/live/finalize ----

    [Fact]
    public async Task Finalize_WithAContiguousChunkSet_EnqueuesAMergeJobOverTheKeysInOrder()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me);
        var queue = new FakeJobQueue();
        var controller = LiveTestSupport.Build(db, me, queue);
        var (id, _) = await BeginWithChunks(db, me, controller, 0, 1, 2);

        var result = await controller.FinalizeLive(id);

        Assert.IsType<AcceptedResult>(result);
        var job = Assert.Single(queue.AudioMergeEnqueued);
        Assert.Equal(id, job.RecordingId);
        Assert.Equal("live-chunks", job.Kind);
        Assert.Empty(job.DeleteRecordingIds);
        Assert.Equal(
            new[] { "/chunks/00000.webm", "/chunks/00001.webm", "/chunks/00002.webm" },
            job.BlobKeys.Select(k => k[k.IndexOf("/chunks/", StringComparison.Ordinal)..]).ToArray());
        Assert.Equal(RecordingStatus.Merging, (await db.Recordings.SingleAsync(r => r.Id == id)).Status);
    }

    [Fact]
    public async Task Finalize_WithAGap_Returns409AndNamesTheMissingSequences()
    {
        // A bare 409 would leave the client unable to act. It has the chunks in its own IndexedDB
        // queue, so naming the gap is what lets it retry exactly those and finalise again.
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me);
        var queue = new FakeJobQueue();
        var controller = LiveTestSupport.Build(db, me, queue);
        var (id, _) = await BeginWithChunks(db, me, controller, 0, 1, 3, 4);

        var result = await controller.FinalizeLive(id);

        var conflict = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        var missing = Assert.IsType<MissingChunksDto>(conflict.Value);
        Assert.Equal(new[] { 2 }, missing.MissingSequences);
        Assert.Empty(queue.AudioMergeEnqueued);
        Assert.Equal(RecordingStatus.Live, (await db.Recordings.SingleAsync(r => r.Id == id)).Status);
    }

    [Fact]
    public async Task Finalize_WithNoChunks_DeletesTheRecording()
    {
        // A session where nothing ever arrived is not a recording, and leaving it would show the user
        // an empty row they have to tidy up themselves.
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me);
        var queue = new FakeJobQueue();
        var controller = LiveTestSupport.Build(db, me, queue);
        var (id, _) = await BeginWithChunks(db, me, controller);

        var result = await controller.FinalizeLive(id);

        Assert.IsType<NoContentResult>(result);
        Assert.False(await db.Recordings.AnyAsync(r => r.Id == id));
        Assert.Empty(queue.AudioMergeEnqueued);
    }

    [Fact]
    public async Task Finalize_WhenAlreadyFinalizing_IsIdempotent()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me);
        var queue = new FakeJobQueue();
        var controller = LiveTestSupport.Build(db, me, queue);
        var (id, _) = await BeginWithChunks(db, me, controller, 0);

        await controller.FinalizeLive(id);
        var second = await controller.FinalizeLive(id);

        Assert.IsType<AcceptedResult>(second);
        Assert.Single(queue.AudioMergeEnqueued);
    }

    [Fact]
    public async Task Finalize_ForAnotherUsersRecording_Returns404()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me);
        await LiveTestSupport.SeedUser(db, other);
        var (id, _) = await BeginWithChunks(db, other, LiveTestSupport.Build(db, other), 0);

        Assert.IsType<NotFoundResult>(await LiveTestSupport.Build(db, me).FinalizeLive(id));
    }

    // ---- the worker callback ----

    [Fact]
    public async Task MergeResult_WithKindLiveChunks_SwapsTheBlob_DropsChunks_AndTranscribes()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me);
        var storage = new FakeAudioStorage();
        var queue = new FakeJobQueue();
        var controller = LiveTestSupport.Build(db, me, queue, storage);
        var (id, _) = await BeginWithChunks(db, me, controller, 0, 1);
        await controller.FinalizeLive(id);
        var chunkKeys = await db.RecordingChunks.Where(c => c.RecordingId == id)
            .Select(c => c.BlobKey).ToListAsync();

        var result = await Callback(db, storage, queue).Result(new AudioMergeResult(
            id, $"{me}/{id}-live.webm", "audio/webm", 4096, 60_000, [], "live-chunks"));

        Assert.IsType<OkResult>(result);
        var rec = await db.Recordings.SingleAsync(r => r.Id == id);
        Assert.Equal($"{me}/{id}-live.webm", rec.BlobKey);
        Assert.Equal(4096, rec.SizeBytes);          // reconciled from the estimate
        Assert.Equal(60_000, rec.DurationMs);
        Assert.Empty(await db.RecordingChunks.Where(c => c.RecordingId == id).ToListAsync());
        foreach (var key in chunkKeys)
            Assert.False(storage.Objects.ContainsKey(key), $"chunk blob {key} should be freed");
        // Hand-off to the pipeline that already exists - nothing about it changes for a live capture.
        Assert.Single(queue.Enqueued);
        Assert.Single(await db.Transcriptions.Where(t => t.RecordingId == id).ToListAsync());
    }

    [Fact]
    public async Task MergeResult_WithKindRecordings_StillDeletesSources_AndDoesNotTranscribe()
    {
        // The pre-existing merge path. This is the assertion that stops live capture regressing a
        // feature that has been in production far longer than it has.
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me);
        var storage = new FakeAudioStorage();
        var queue = new FakeJobQueue();

        var survivor = new Recording
        {
            Id = Guid.NewGuid(), UserId = me, Title = "Survivor",
            BlobKey = $"{me}/a.webm", Status = RecordingStatus.Merging,
        };
        var source = new Recording
        {
            Id = Guid.NewGuid(), UserId = me, Title = "Folded in",
            BlobKey = $"{me}/b.webm", Status = RecordingStatus.Transcribed,
        };
        db.Recordings.AddRange(survivor, source);
        await db.SaveChangesAsync();

        var result = await Callback(db, storage, queue).Result(new AudioMergeResult(
            survivor.Id, $"{me}/merged.webm", "audio/webm", 900, 120_000, [source.Id]));

        Assert.IsType<OkResult>(result);
        Assert.False(await db.Recordings.AnyAsync(r => r.Id == source.Id));
        Assert.Equal(RecordingStatus.Transcribed,
            (await db.Recordings.SingleAsync(r => r.Id == survivor.Id)).Status);
        Assert.Empty(queue.Enqueued);
    }
}
