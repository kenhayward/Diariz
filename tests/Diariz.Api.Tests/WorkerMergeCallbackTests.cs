using System.Text;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

public class WorkerMergeCallbackTests
{
    private const string Secret = "test-secret";

    private static WorkerMergeCallbackController Build(DiarizDbContext db, FakeAudioStorage storage, string? secret = Secret)
    {
        var controller = new WorkerMergeCallbackController(
            db, new FakeHubContext(), storage, Options.Create(new WorkerOptions { CallbackSecret = Secret }));
        var ctx = Http.Context(Guid.NewGuid());
        if (secret is not null) ctx.HttpContext.Request.Headers["X-Worker-Secret"] = secret;
        controller.ControllerContext = ctx;
        return controller;
    }

    private static Recording Rec(Guid userId, string key, long size = 100) =>
        new() { Id = Guid.NewGuid(), UserId = userId, BlobKey = key, SizeBytes = size, Status = RecordingStatus.Merging };

    [Fact]
    public async Task Result_SwapsAudioOntoSurvivor_DeletesOthersAndOldBlob_SetsTranscribed()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var storage = new FakeAudioStorage();
        var survivor = Rec(userId, $"{userId}/old.webm", size: 50);
        var other = Rec(userId, $"{userId}/other.webm", size: 70);
        db.Recordings.AddRange(survivor, other);
        await db.SaveChangesAsync();
        storage.Objects[survivor.BlobKey] = Encoding.UTF8.GetBytes("old");
        storage.Objects[other.BlobKey] = Encoding.UTF8.GetBytes("oth");
        var mergedKey = $"{userId}/merged.webm";
        storage.Objects[mergedKey] = Encoding.UTF8.GetBytes("combined");

        var result = await Build(db, storage).Result(
            new AudioMergeResult(survivor.Id, mergedKey, "audio/webm", SizeBytes: 999, DurationMs: 3000, [other.Id]));

        Assert.IsType<OkResult>(result);
        var reloaded = (await db.Recordings.FindAsync(survivor.Id))!;
        Assert.Equal(mergedKey, reloaded.BlobKey);
        Assert.Equal(999, reloaded.SizeBytes);
        Assert.Equal(3000, reloaded.DurationMs);
        Assert.Equal(RecordingStatus.Transcribed, reloaded.Status);
        Assert.Null(await db.Recordings.FindAsync(other.Id));            // source removed
        Assert.False(storage.Objects.ContainsKey($"{userId}/old.webm")); // survivor's old blob dropped
        Assert.False(storage.Objects.ContainsKey($"{userId}/other.webm"));// source blob dropped
        Assert.True(storage.Objects.ContainsKey(mergedKey));             // combined audio kept
    }

    [Fact]
    public async Task Result_FreesSourceFileAttachmentBlobs()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var storage = new FakeAudioStorage();
        var survivor = Rec(userId, $"{userId}/old.webm", size: 50);
        var other = Rec(userId, $"{userId}/other.webm", size: 70);
        db.Recordings.AddRange(survivor, other);
        // A file attachment still hanging off the merged-away source at callback time must not be orphaned.
        var attKey = $"{userId}/attachments/{Guid.NewGuid()}.pdf";
        db.Attachments.Add(new Attachment { Id = Guid.NewGuid(), RecordingId = other.Id, Kind = AttachmentKind.File, Name = "late.pdf", BlobKey = attKey, Ordinal = 0 });
        await db.SaveChangesAsync();
        storage.Objects[survivor.BlobKey] = Encoding.UTF8.GetBytes("old");
        storage.Objects[other.BlobKey] = Encoding.UTF8.GetBytes("oth");
        storage.Objects[attKey] = Encoding.UTF8.GetBytes("pdf");
        var mergedKey = $"{userId}/merged.webm";
        storage.Objects[mergedKey] = Encoding.UTF8.GetBytes("combined");

        var result = await Build(db, storage).Result(
            new AudioMergeResult(survivor.Id, mergedKey, "audio/webm", SizeBytes: 999, DurationMs: 3000, [other.Id]));

        Assert.IsType<OkResult>(result);
        Assert.False(storage.Objects.ContainsKey(attKey)); // source attachment blob freed (no leak)
    }

    [Fact]
    public async Task Result_FreesSourceScreenshotBlobs_ButLeavesSurvivorsScreenshotsAlone()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var storage = new FakeAudioStorage();
        var survivor = Rec(userId, $"{userId}/old.webm", size: 50);
        var other = Rec(userId, $"{userId}/other.webm", size: 70);
        db.Recordings.AddRange(survivor, other);

        // A screenshot still hanging off the merged-away source must have both its blobs freed.
        var otherFullKey = $"{userId}/screenshots/{Guid.NewGuid()}.png";
        var otherThumbKey = $"{userId}/screenshots/{Guid.NewGuid()}-thumb.jpg";
        db.MeetingScreenshots.Add(new MeetingScreenshot
        {
            Id = Guid.NewGuid(), UserId = userId, RecordingId = other.Id,
            BlobKey = otherFullKey, ThumbBlobKey = otherThumbKey, CapturedAtMs = 1000,
        });

        // The survivor's own screenshot blobs must NOT be touched by the merge.
        var survivorFullKey = $"{userId}/screenshots/{Guid.NewGuid()}.png";
        var survivorThumbKey = $"{userId}/screenshots/{Guid.NewGuid()}-thumb.jpg";
        db.MeetingScreenshots.Add(new MeetingScreenshot
        {
            Id = Guid.NewGuid(), UserId = userId, RecordingId = survivor.Id,
            BlobKey = survivorFullKey, ThumbBlobKey = survivorThumbKey, CapturedAtMs = 500,
        });
        await db.SaveChangesAsync();

        storage.Objects[survivor.BlobKey] = Encoding.UTF8.GetBytes("old");
        storage.Objects[other.BlobKey] = Encoding.UTF8.GetBytes("oth");
        storage.Objects[otherFullKey] = Encoding.UTF8.GetBytes("png");
        storage.Objects[otherThumbKey] = Encoding.UTF8.GetBytes("jpg");
        storage.Objects[survivorFullKey] = Encoding.UTF8.GetBytes("png2");
        storage.Objects[survivorThumbKey] = Encoding.UTF8.GetBytes("jpg2");
        var mergedKey = $"{userId}/merged.webm";
        storage.Objects[mergedKey] = Encoding.UTF8.GetBytes("combined");

        var result = await Build(db, storage).Result(
            new AudioMergeResult(survivor.Id, mergedKey, "audio/webm", SizeBytes: 999, DurationMs: 3000, [other.Id]));

        Assert.IsType<OkResult>(result);
        Assert.False(storage.Objects.ContainsKey(otherFullKey));   // source screenshot's full image freed
        Assert.False(storage.Objects.ContainsKey(otherThumbKey));  // source screenshot's thumbnail freed
        Assert.True(storage.Objects.ContainsKey(survivorFullKey)); // survivor's screenshot untouched
        Assert.True(storage.Objects.ContainsKey(survivorThumbKey));// survivor's thumbnail untouched
    }

    [Fact]
    public async Task Result_WithWrongSecret_ReturnsUnauthorized()
    {
        using var db = TestDb.Create();
        var rec = Rec(Guid.NewGuid(), "k");
        db.Recordings.Add(rec);
        await db.SaveChangesAsync();

        var result = await Build(db, new FakeAudioStorage(), secret: "nope")
            .Result(new AudioMergeResult(rec.Id, "m", "audio/webm", 1, 1, []));

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task Failure_SetsFailed_AndKeepsTheOtherRecordings()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var survivor = Rec(userId, "k");
        var other = Rec(userId, "k2");
        db.Recordings.AddRange(survivor, other);
        await db.SaveChangesAsync();

        var result = await Build(db, new FakeAudioStorage())
            .Failure(new AudioMergeFailure(survivor.Id, "ffmpeg blew up"));

        Assert.IsType<OkResult>(result);
        var reloaded = (await db.Recordings.FindAsync(survivor.Id))!;
        Assert.Equal(RecordingStatus.Failed, reloaded.Status);
        Assert.Equal("ffmpeg blew up", reloaded.Error);
        Assert.NotNull(await db.Recordings.FindAsync(other.Id)); // originals untouched on failure
    }
}
