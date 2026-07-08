using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diariz.Api.Tests;

public class PlatformSettingsControllerTests
{
    private static PlatformSettingsController Build(
        DiarizDbContext db, FakeAudioStorage? storage = null, FakeJobQueue? queue = null) =>
        new(new PlatformSettingsService(db), db, storage ?? new FakeAudioStorage(), queue ?? new FakeJobQueue(),
            NullLogger<PlatformSettingsController>.Instance)
        { ControllerContext = Http.Context(Guid.NewGuid()) };

    [Fact]
    public async Task Get_ReturnsDefaults_WhenUnset()
    {
        using var db = TestDb.Create();
        var dto = await Build(db).Get();

        Assert.Equal(PlatformSettings.DefaultStarterQuotaBytes, dto.StarterQuotaBytes);
        Assert.Equal(PlatformSettings.DefaultMaxQuotaBytes, dto.MaxQuotaBytes);
    }

    [Fact]
    public async Task Update_PersistsNewValues()
    {
        using var db = TestDb.Create();
        var controller = Build(db);

        var result = await controller.Update(new UpdatePlatformSettingsRequest(2L * 1024 * 1024 * 1024, 10L * 1024 * 1024 * 1024));

        var dto = Assert.IsType<PlatformSettingsDto>(result.Value);
        Assert.Equal(2L * 1024 * 1024 * 1024, dto.StarterQuotaBytes);
        var row = await db.PlatformSettings.SingleAsync();
        Assert.Equal(10L * 1024 * 1024 * 1024, row.MaxQuotaBytes);
    }

    [Fact]
    public async Task Update_StarterAboveMax_ReturnsBadRequest()
    {
        using var db = TestDb.Create();
        var result = await Build(db).Update(new UpdatePlatformSettingsRequest(20L * 1024 * 1024 * 1024, 5L * 1024 * 1024 * 1024));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Update_NonPositive_ReturnsBadRequest()
    {
        using var db = TestDb.Create();
        var result = await Build(db).Update(new UpdatePlatformSettingsRequest(0, 5L * 1024 * 1024 * 1024));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Update_RoundTrips_LlmTimeoutSeconds()
    {
        using var db = TestDb.Create();
        var gb = 5L * 1024 * 1024 * 1024;

        var result = await Build(db).Update(new UpdatePlatformSettingsRequest(gb, gb, LlmTimeoutSeconds: 300));

        Assert.Equal(300, Assert.IsType<PlatformSettingsDto>(result.Value).LlmTimeoutSeconds);
        Assert.Equal(300, (await db.PlatformSettings.SingleAsync()).LlmTimeoutSeconds);
    }

    [Fact]
    public async Task Update_TimeoutBelowMinimum_ReturnsBadRequest()
    {
        using var db = TestDb.Create();
        var gb = 5L * 1024 * 1024 * 1024;

        var result = await Build(db).Update(new UpdatePlatformSettingsRequest(gb, gb, LlmTimeoutSeconds: 2));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Get_ReturnsLlmTimeoutDefault()
    {
        using var db = TestDb.Create();
        Assert.Equal(PlatformSettings.DefaultLlmTimeoutSeconds, (await Build(db).Get()).LlmTimeoutSeconds);
    }

    [Fact]
    public async Task Update_RoundTrips_MinutesGenerationMode()
    {
        using var db = TestDb.Create();
        var gb = 5L * 1024 * 1024 * 1024;

        var result = await Build(db).Update(
            new UpdatePlatformSettingsRequest(gb, gb, MinutesGenerationMode.PerSection));

        Assert.Equal(MinutesGenerationMode.PerSection, Assert.IsType<PlatformSettingsDto>(result.Value).MinutesGenerationMode);
        Assert.Equal(MinutesGenerationMode.PerSection, (await db.PlatformSettings.SingleAsync()).MinutesGenerationMode);
    }

    [Fact]
    public async Task Get_DefaultsTo_SingleCall()
    {
        using var db = TestDb.Create();
        Assert.Equal(MinutesGenerationMode.SingleCall, (await Build(db).Get()).MinutesGenerationMode);
    }

    [Fact]
    public async Task Get_DefaultsTo_AutoDeleteDisabled_30Days_0300()
    {
        using var db = TestDb.Create();
        var dto = await Build(db).Get();

        Assert.False(dto.AutoDeleteAudioEnabled);
        Assert.Equal(30, dto.AudioRetentionDays);
        Assert.Equal(new TimeOnly(3, 0), dto.AudioDeletionTimeOfDay);
    }

    [Fact]
    public async Task Update_RoundTrips_AudioRetentionSettings()
    {
        using var db = TestDb.Create();
        var gb = 5L * 1024 * 1024 * 1024;

        var result = await Build(db).Update(new UpdatePlatformSettingsRequest(
            gb, gb, MinutesGenerationMode.SingleCall,
            AutoDeleteAudioEnabled: true, AudioRetentionDays: 7, AudioDeletionTimeOfDay: new TimeOnly(2, 15)));

        var dto = Assert.IsType<PlatformSettingsDto>(result.Value);
        Assert.True(dto.AutoDeleteAudioEnabled);
        Assert.Equal(7, dto.AudioRetentionDays);
        Assert.Equal(new TimeOnly(2, 15), dto.AudioDeletionTimeOfDay);
        var row = await db.PlatformSettings.SingleAsync();
        Assert.True(row.AutoDeleteAudioEnabled);
        Assert.Equal(7, row.AudioRetentionDays);
        Assert.Equal(new TimeOnly(2, 15), row.AudioDeletionTimeOfDay);
    }

    [Fact]
    public async Task Update_RoundTrips_ApiAccessEnabled()
    {
        using var db = TestDb.Create();
        var gb = 5L * 1024 * 1024 * 1024;

        var result = await Build(db).Update(new UpdatePlatformSettingsRequest(gb, gb, ApiAccessEnabled: true));

        Assert.True(Assert.IsType<PlatformSettingsDto>(result.Value).ApiAccessEnabled);
        Assert.True((await db.PlatformSettings.SingleAsync()).ApiAccessEnabled);
    }

    [Fact]
    public async Task Update_AudioRetentionDaysBelowOne_ReturnsBadRequest()
    {
        using var db = TestDb.Create();
        var gb = 5L * 1024 * 1024 * 1024;

        var result = await Build(db).Update(new UpdatePlatformSettingsRequest(
            gb, gb, AudioRetentionDays: 0));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task RunAudioRetentionNow_DeletesEligibleAudio_ReturnsCount_EvenWhenDisabled()
    {
        using var db = TestDb.Create();
        var storage = new FakeAudioStorage();
        var userId = Guid.NewGuid();
        var eligible = new Recording
        {
            Id = Guid.NewGuid(), UserId = userId, BlobKey = "u/old.webm", SizeBytes = 100,
            Status = RecordingStatus.Transcribed, CreatedAt = DateTimeOffset.UtcNow.AddDays(-40),
        };
        var recent = new Recording
        {
            Id = Guid.NewGuid(), UserId = userId, BlobKey = "u/new.webm", SizeBytes = 100,
            Status = RecordingStatus.Transcribed, CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
        };
        db.Recordings.AddRange(eligible, recent);
        storage.Objects["u/old.webm"] = new byte[100];
        storage.Objects["u/new.webm"] = new byte[100];
        await db.SaveChangesAsync();
        // Auto-delete is disabled by default; Run Now is a manual trigger that runs regardless.
        var controller = Build(db, storage);

        var result = await controller.RunAudioRetentionNow();

        Assert.Equal(1, result.Deleted); // only the 40-day-old one (default 30-day window)
        Assert.False(storage.Objects.ContainsKey("u/old.webm"));
        Assert.True(storage.Objects.ContainsKey("u/new.webm"));
        Assert.NotNull((await db.Recordings.FindAsync(eligible.Id))!.AudioDeletedAt);
    }

    [Fact]
    public async Task RunTagBackfillNow_EnqueuesUntaggedRecordings_ReturnsCount()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var untagged = new Recording { Id = Guid.NewGuid(), UserId = userId, BlobKey = "k1" };
        var tagged = new Recording
        {
            Id = Guid.NewGuid(), UserId = userId, BlobKey = "k2", TagsExtractedAt = DateTimeOffset.UtcNow,
        };
        db.Recordings.AddRange(untagged, tagged);
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = untagged.Id, Model = "whisperx", Version = 1 };
        db.Transcriptions.Add(tr);
        db.Transcriptions.Add(new Transcription
        {
            Id = Guid.NewGuid(), RecordingId = tagged.Id, Model = "whisperx", Version = 1,
        });
        await db.SaveChangesAsync();
        var queue = new FakeJobQueue();

        var result = await Build(db, queue: queue).RunTagBackfillNow();

        // Enqueues (fan-out to the tags worker) rather than extracting inline — the count is jobs queued.
        Assert.Equal(1, result.Enqueued);
        var job = Assert.Single(queue.TagsEnqueued);
        Assert.Equal(untagged.Id, job.RecordingId);
        Assert.Equal(tr.Id, job.TranscriptionId);
    }
}
