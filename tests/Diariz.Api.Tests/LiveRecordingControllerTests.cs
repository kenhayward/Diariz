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
}
