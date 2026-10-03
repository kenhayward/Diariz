using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

/// <summary>The administrator's switch for live transcription - the lever for an overloaded server.
///
/// <para>Off applies at once to every meeting in progress; on applies only to recordings started
/// afterwards, so a meeting never resumes mid-way with an unexplained gap. Recording and the final
/// transcript never depend on it.</para></summary>
public class LiveTranscriptionSwitchTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 14, 0, 0, TimeSpan.Zero);

    // ---- the gate ----

    [Fact]
    public void Gate_AllowsEverythingWhileTheSwitchHasNeverMoved() =>
        Assert.True(LiveTranscriptionGate.Allows(new PlatformSettings(), Now.AddYears(-1)));

    [Fact]
    public void Gate_RefusesEveryRecordingWhileOff() =>
        Assert.False(LiveTranscriptionGate.Allows(
            new PlatformSettings { LiveTranscriptionEnabled = false, LiveTranscriptionChangedAt = Now },
            Now.AddMinutes(5)));

    [Fact]
    public void Gate_OnceBackOn_RefusesARecordingThatStartedWhileItWasOff()
    {
        var s = new PlatformSettings { LiveTranscriptionEnabled = true, LiveTranscriptionChangedAt = Now };

        Assert.False(LiveTranscriptionGate.Allows(s, Now.AddMinutes(-1)));
        Assert.True(LiveTranscriptionGate.Allows(s, Now));
        Assert.True(LiveTranscriptionGate.Allows(s, Now.AddMinutes(1)));
    }

    [Fact]
    public void Gate_WithNoSettingsRowAllows() =>
        Assert.True(LiveTranscriptionGate.Allows(null, Now));

    // ---- flipping the switch ----

    [Fact]
    public async Task SwitchingOff_RecordsWhen_TellsTheWorkers_AndSettlesChunksStillInFlight()
    {
        using var db = TestDb.Create();
        var queue = new FakeJobQueue();
        var settings = await new PlatformSettingsService(db).GetAsync();
        var live = await SeedRecording(db, RecordingStatus.Live, unsettledChunks: 2);
        var finished = await SeedRecording(db, RecordingStatus.Transcribing, unsettledChunks: 1);

        await LiveTranscriptionSwitch.ApplyAsync(db, queue, settings, enabled: false, Now);

        Assert.False(settings.LiveTranscriptionEnabled);
        Assert.Equal(Now, settings.LiveTranscriptionChangedAt);
        Assert.False(queue.LiveTranscriptionEnabled);
        // Chunks of a meeting still running are settled: none will ever be transcribed now, and left
        // outstanding they would read as a backlog the moment the switch went back on (issue #758).
        Assert.All(await db.RecordingChunks.Where(c => c.RecordingId == live).ToListAsync(),
            c => Assert.Equal(Now, c.SettledAt));
        // Anything else is not this switch's business.
        Assert.All(await db.RecordingChunks.Where(c => c.RecordingId == finished).ToListAsync(),
            c => Assert.Null(c.SettledAt));
    }

    [Fact]
    public async Task SwitchingOn_RecordsWhen_AndTellsTheWorkers()
    {
        using var db = TestDb.Create();
        var queue = new FakeJobQueue();
        var settings = await new PlatformSettingsService(db).GetAsync();
        settings.LiveTranscriptionEnabled = false;
        settings.LiveTranscriptionChangedAt = Now.AddHours(-1);

        await LiveTranscriptionSwitch.ApplyAsync(db, queue, settings, enabled: true, Now);

        Assert.True(settings.LiveTranscriptionEnabled);
        Assert.Equal(Now, settings.LiveTranscriptionChangedAt);
        Assert.True(queue.LiveTranscriptionEnabled);
    }

    [Fact]
    public async Task SavingTheSameValue_LeavesTheTimestampAlone()
    {
        // The settings page saves every field at once. Saving an unrelated change must not reset "off
        // since", nor make recordings already running count as started before the switch went on.
        using var db = TestDb.Create();
        var queue = new FakeJobQueue();
        var settings = await new PlatformSettingsService(db).GetAsync();
        settings.LiveTranscriptionEnabled = false;
        settings.LiveTranscriptionChangedAt = Now.AddHours(-1);

        await LiveTranscriptionSwitch.ApplyAsync(db, queue, settings, enabled: false, Now);

        Assert.Equal(Now.AddHours(-1), settings.LiveTranscriptionChangedAt);
        Assert.Null(queue.LiveTranscriptionEnabled);
    }

    [Fact]
    public async Task AWorkerFlagThatCannotBeWritten_DoesNotUndoTheSwitch()
    {
        // The database is the record. The API stops queueing on its own say-so, so a Redis hiccup costs
        // only the dropping of chunks already queued, not the switch itself.
        using var db = TestDb.Create();
        var settings = await new PlatformSettingsService(db).GetAsync();

        await LiveTranscriptionSwitch.ApplyAsync(db, new ThrowingFlagQueue(), settings, enabled: false, Now);

        Assert.False(settings.LiveTranscriptionEnabled);
    }

    [Fact]
    public async Task SettingsUpdate_AppliesTheSwitchAndReportsIt()
    {
        using var db = TestDb.Create();
        var queue = new FakeJobQueue();
        var controller = PlatformSettingsControllerTests.Build(db, queue: queue);

        var result = await controller.Update(new UpdatePlatformSettingsRequest(1024, 2048,
            LiveTranscriptionEnabled: false));

        var dto = Assert.IsType<PlatformSettingsDto>(result.Value);
        Assert.False(dto.LiveTranscriptionEnabled);
        Assert.NotNull(dto.LiveTranscriptionChangedAt);
        Assert.False(queue.LiveTranscriptionEnabled);
    }

    // ---- what a meeting sees ----

    [Fact]
    public async Task BeginLive_SaysWhetherThisRecordingWillBeTranscribedLive()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me);

        var on = await BeginDto(db, me);
        await SetSwitch(db, enabled: false, at: DateTimeOffset.UtcNow);
        var off = await BeginDto(db, me);

        Assert.True(on.LiveTranscription);
        Assert.False(off.LiveTranscription);
    }

    [Fact]
    public async Task PutChunk_WhileOff_StoresTheChunkButDoesNotQueueIt_AndTellsThePage()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me);
        var queue = new FakeJobQueue();
        var hub = new FakeHubContext();
        var controller = LiveTestSupport.Build(db, me, queue, hub: hub);
        var (id, session) = await Begin(controller);
        await controller.PutChunk(id, 0, Chunk(), session, 0, 10_000);

        await SetSwitch(db, enabled: false, at: DateTimeOffset.UtcNow);
        var result = await controller.PutChunk(id, 1, Chunk(), session, 10_000, 20_000);

        Assert.IsType<NoContentResult>(result); // the recording itself is untouched
        Assert.Single(queue.LiveChunkEnqueued);
        var chunk = await db.RecordingChunks.SingleAsync(c => c.RecordingId == id && c.Sequence == 1);
        Assert.NotNull(chunk.SettledAt);
        // Sent with every refused chunk, so a page that missed one hears it again a few seconds later.
        Assert.Contains(hub.Sent, m => m.Method == "LiveTranscriptStopped");
    }

    [Fact]
    public async Task PutChunk_AfterTheSwitchComesBackOn_StaysOffForAMeetingThatStartedWhileItWasOff()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await LiveTestSupport.SeedUser(db, me);
        var queue = new FakeJobQueue();
        var controller = LiveTestSupport.Build(db, me, queue);
        var (id, session) = await Begin(controller);

        await SetSwitch(db, enabled: true, at: DateTimeOffset.UtcNow.AddMinutes(1));
        await controller.PutChunk(id, 0, Chunk(), session, 0, 10_000);

        Assert.Empty(queue.LiveChunkEnqueued);
    }

    // ---- helpers ----

    private static async Task SetSwitch(DiarizDbContext db, bool enabled, DateTimeOffset at)
    {
        var s = await new PlatformSettingsService(db).GetAsync();
        s.LiveTranscriptionEnabled = enabled;
        s.LiveTranscriptionChangedAt = at;
        await db.SaveChangesAsync();
    }

    private static async Task<LiveRecordingDto> BeginDto(DiarizDbContext db, Guid me)
    {
        var result = await LiveTestSupport.Build(db, me).BeginLive(Req());
        return Assert.IsType<LiveRecordingDto>(Assert.IsType<CreatedAtActionResult>(result.Result).Value);
    }

    private static async Task<(Guid Id, Guid Session)> Begin(RecordingsController controller)
    {
        var result = await controller.BeginLive(Req());
        var dto = Assert.IsType<LiveRecordingDto>(Assert.IsType<CreatedAtActionResult>(result.Result).Value);
        return (dto.Id, dto.SessionId);
    }

    private static BeginLiveRecordingRequest Req() =>
        new("Standup", RecordingSource.Microphone, null, null, null, Guid.NewGuid(), 30 * 60 * 1000);

    private static FormFile Chunk() =>
        new(new MemoryStream([1, 2, 3]), 0, 3, "chunk", "chunk.webm")
        {
            Headers = new HeaderDictionary(),
            ContentType = "audio/webm",
        };

    private static async Task<Guid> SeedRecording(DiarizDbContext db, RecordingStatus status, int unsettledChunks)
    {
        var rec = new Recording
        {
            Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Title = "Take", BlobKey = "", Status = status,
        };
        db.Recordings.Add(rec);
        for (var i = 0; i < unsettledChunks; i++)
            db.RecordingChunks.Add(new RecordingChunk
            {
                Id = Guid.NewGuid(), RecordingId = rec.Id, Sequence = i, BlobKey = $"k/{i}",
                StartMs = i * 10_000, EndMs = (i + 1) * 10_000, SizeBytes = 1, ReceivedAt = Now.AddMinutes(-1),
            });
        await db.SaveChangesAsync();
        return rec.Id;
    }

    private sealed class ThrowingFlagQueue : FakeJobQueue
    {
        public override Task SetLiveTranscriptionEnabledAsync(bool enabled, CancellationToken ct = default) =>
            throw new InvalidOperationException("redis down");
    }
}
