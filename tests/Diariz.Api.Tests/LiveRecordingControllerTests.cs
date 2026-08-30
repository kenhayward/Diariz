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
/// The live-capture lifecycle: begin, chunk, finalise. These are the ownership, permission and
/// idempotency rules - anything that needs a real unique constraint, a real blob store or a real
/// Redis stream is in <c>LiveChunkFlowTests</c> in the integration project instead.
/// </summary>
public class LiveRecordingControllerTests
{
    private static BeginLiveRecordingRequest Req(
        Guid? sessionId = null, Guid? roomId = null, Guid? sectionId = null,
        DateTimeOffset? startedAt = null, long expectedDurationMs = 30 * 60 * 1000) =>
        new("Standup", RecordingSource.Microphone, sectionId, roomId, startedAt,
            sessionId ?? Guid.NewGuid(), expectedDurationMs);

    [Fact]
    public async Task Begin_CreatesRecordingWithLiveStatus_AndPlacesItInThePersonalRoom()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me);
        var controller = LiveTestSupport.Build(db, me);

        var result = await controller.BeginLive(Req());

        var dto = Assert.IsType<LiveRecordingDto>(Assert.IsType<CreatedAtActionResult>(result.Result).Value);
        var rec = await db.Recordings.SingleAsync(r => r.Id == dto.Id);
        Assert.Equal(RecordingStatus.Live, rec.Status);
        Assert.Equal(me, rec.UserId);
        // No audio has arrived, so there is nothing to point a blob key at yet.
        Assert.Equal("", rec.BlobKey);
        Assert.Equal(0, rec.SizeBytes);
        Assert.True(await db.RoomRecordings.AnyAsync(rr => rr.RecordingId == dto.Id));
    }

    [Fact]
    public async Task Begin_IntoASharedRoomWithoutCreateRecording_Returns403()
    {
        // Exactly the gate Upload applies. A second entry point that quietly dropped it would be a
        // permission bypass, so this is the guard that matters most in this file.
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me);
        var room = await LiveTestSupport.SeedSharedRoomWithoutPermission(db, me);
        var controller = LiveTestSupport.Build(db, me);

        var result = await controller.BeginLive(Req(roomId: room));

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, status.StatusCode);
        Assert.False(await db.Recordings.AnyAsync(r => r.UserId == me));
    }

    [Fact]
    public async Task Begin_WhenTheEstimateWouldExceedQuota_Returns413()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me, quotaBytes: 1024);
        var controller = LiveTestSupport.Build(db, me);

        var result = await controller.BeginLive(Req(expectedDurationMs: 60 * 60 * 1000));

        Assert.Equal(413, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        Assert.False(await db.Recordings.AnyAsync(r => r.UserId == me));
    }

    [Fact]
    public async Task Begin_WithAnImplausibleStartedAt_FallsBackToCreatedAt()
    {
        // A skewed or hostile clock must not poison calendar matching, but it must never cost the
        // user their recording either - the same rule Upload applies.
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me);
        var controller = LiveTestSupport.Build(db, me);

        var result = await controller.BeginLive(Req(startedAt: DateTimeOffset.UtcNow.AddYears(5)));

        var dto = Assert.IsType<LiveRecordingDto>(Assert.IsType<CreatedAtActionResult>(result.Result).Value);
        var rec = await db.Recordings.SingleAsync(r => r.Id == dto.Id);
        Assert.Null(rec.StartedAt);
    }

    [Fact]
    public async Task Begin_StoresTheClientSessionId()
    {
        // The session id is what stops a second device interleaving its chunks into this recording,
        // so it has to survive the round trip rather than being echoed back from the request.
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me);
        var controller = LiveTestSupport.Build(db, me);
        var session = Guid.NewGuid();

        var result = await controller.BeginLive(Req(sessionId: session));

        var dto = Assert.IsType<LiveRecordingDto>(Assert.IsType<CreatedAtActionResult>(result.Result).Value);
        var rec = await db.Recordings.SingleAsync(r => r.Id == dto.Id);
        Assert.Equal(session, rec.LiveSessionId);
        Assert.Equal(session, dto.SessionId);
    }

    // ---- PUT /api/recordings/{id}/chunks/{sequence} ----

    private static FormFile Chunk(string body = "chunk-bytes") =>
        new(new MemoryStream(Encoding.UTF8.GetBytes(body)), 0, Encoding.UTF8.GetByteCount(body),
            "chunk", "chunk.webm")
        {
            Headers = new HeaderDictionary(),
            ContentType = "audio/webm",
        };

    private static async Task<(Guid RecordingId, Guid SessionId)> BeginAsync(
        DiarizDbContext db, Guid userId, RecordingsController controller)
    {
        var session = Guid.NewGuid();
        var result = await controller.BeginLive(Req(sessionId: session));
        var dto = (LiveRecordingDto)((CreatedAtActionResult)result.Result!).Value!;
        return (dto.Id, session);
    }

    [Fact]
    public async Task PutChunk_StoresTheBlobAndCreatesTheRow()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me);
        var storage = new FakeAudioStorage();
        var controller = LiveTestSupport.Build(db, me, storage: storage);
        var (id, session) = await BeginAsync(db, me, controller);

        var result = await controller.PutChunk(id, 0, Chunk(), session, startMs: 0, endMs: 30_000);

        Assert.IsType<NoContentResult>(result);
        var chunk = await db.RecordingChunks.SingleAsync(c => c.RecordingId == id);
        Assert.Equal(0, chunk.Sequence);
        Assert.Equal(30_000, chunk.EndMs);
        // Zero-padded so a plain object-store listing sorts into capture order.
        Assert.EndsWith("/chunks/00000.webm", chunk.BlobKey);
        Assert.True(storage.Objects.ContainsKey(chunk.BlobKey));
    }

    [Fact]
    public async Task PutChunk_Twice_IsIdempotent()
    {
        // The retry path. Every flaky network in production exercises this, and a version that
        // appended would break finalise's contiguity check as well as double-charging quota.
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me);
        var storage = new FakeAudioStorage();
        var controller = LiveTestSupport.Build(db, me, storage: storage);
        var (id, session) = await BeginAsync(db, me, controller);

        await controller.PutChunk(id, 0, Chunk("first-attempt"), session, 0, 30_000);
        await controller.PutChunk(id, 0, Chunk("retry-wins"), session, 0, 30_000);

        var chunk = Assert.Single(await db.RecordingChunks.Where(c => c.RecordingId == id).ToListAsync());
        Assert.Equal("retry-wins", Encoding.UTF8.GetString(storage.Objects[chunk.BlobKey]));
        Assert.Equal(Encoding.UTF8.GetByteCount("retry-wins"), chunk.SizeBytes);
        var rec = await db.Recordings.SingleAsync(r => r.Id == id);
        Assert.Equal(Encoding.UTF8.GetByteCount("retry-wins"), rec.SizeBytes);
    }

    [Fact]
    public async Task PutChunk_ForAnotherUsersRecording_Returns404()
    {
        // 404 rather than 403: confirming the id exists would leak that it does.
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me);
        await LiveTestSupport.SeedUser(db, other);
        var (id, session) = await BeginAsync(db, other, LiveTestSupport.Build(db, other));

        var result = await LiveTestSupport.Build(db, me).PutChunk(id, 0, Chunk(), session, 0, 30_000);

        Assert.IsType<NotFoundResult>(result);
        Assert.False(await db.RecordingChunks.AnyAsync());
    }

    [Fact]
    public async Task PutChunk_WithAMismatchedSessionId_Returns409()
    {
        // A second device signed in as the same user. Interleaving its chunks would silently corrupt
        // the recording, so it is refused rather than merged.
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me);
        var controller = LiveTestSupport.Build(db, me);
        var (id, _) = await BeginAsync(db, me, controller);

        var result = await controller.PutChunk(id, 0, Chunk(), Guid.NewGuid(), 0, 30_000);

        Assert.Equal(StatusCodes.Status409Conflict, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(await db.RecordingChunks.AnyAsync());
    }

    [Fact]
    public async Task PutChunk_WhenTheRecordingIsNotLive_Returns409()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me);
        var controller = LiveTestSupport.Build(db, me);
        var (id, session) = await BeginAsync(db, me, controller);
        (await db.Recordings.SingleAsync(r => r.Id == id)).Status = RecordingStatus.Transcribed;
        await db.SaveChangesAsync();

        var result = await controller.PutChunk(id, 0, Chunk(), session, 0, 30_000);

        Assert.Equal(StatusCodes.Status409Conflict, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task PutChunk_WithAnEmptyBody_Returns400()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me);
        var controller = LiveTestSupport.Build(db, me);
        var (id, session) = await BeginAsync(db, me, controller);

        var result = await controller.PutChunk(id, 0, Chunk(""), session, 0, 30_000);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ---- DELETE /api/recordings/{id}/live ----

    [Fact]
    public async Task DiscardLive_RemovesTheRecordingAndItsChunkBlobs()
    {
        // Called when a take is stopped while the begin call was still in flight. The reaper would
        // collect it eventually, but the user would see a stray recording in their list first.
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me);
        var storage = new FakeAudioStorage();
        var controller = LiveTestSupport.Build(db, me, storage: storage);
        var (id, session) = await BeginAsync(db, me, controller);
        await controller.PutChunk(id, 0, Chunk(), session, 0, 30_000);
        var chunkKey = (await db.RecordingChunks.SingleAsync(c => c.RecordingId == id)).BlobKey;

        var result = await controller.DiscardLive(id);

        Assert.IsType<NoContentResult>(result);
        Assert.False(await db.Recordings.AnyAsync(r => r.Id == id));
        Assert.False(await db.RecordingChunks.AnyAsync(c => c.RecordingId == id));
        Assert.False(storage.Objects.ContainsKey(chunkKey), "the chunk blob should be freed, not orphaned");
    }

    [Fact]
    public async Task DiscardLive_ForAnotherUsersRecording_Returns404()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me);
        await LiveTestSupport.SeedUser(db, other);
        var (id, _) = await BeginAsync(db, other, LiveTestSupport.Build(db, other));

        Assert.IsType<NotFoundResult>(await LiveTestSupport.Build(db, me).DiscardLive(id));
        Assert.True(await db.Recordings.AnyAsync(r => r.Id == id));
    }

    [Fact]
    public async Task DiscardLive_RefusesARecordingThatIsNoLongerLive()
    {
        // Once finalise has started, the chunks are the worker's input. Deleting them from under it
        // would fail the merge rather than tidy anything.
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me);
        var controller = LiveTestSupport.Build(db, me);
        var (id, _) = await BeginAsync(db, me, controller);
        (await db.Recordings.SingleAsync(r => r.Id == id)).Status = RecordingStatus.Merging;
        await db.SaveChangesAsync();

        var result = await controller.DiscardLive(id);

        Assert.Equal(StatusCodes.Status409Conflict, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.True(await db.Recordings.AnyAsync(r => r.Id == id));
    }
}
