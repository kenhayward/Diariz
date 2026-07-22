using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

public class StorageUsageTests
{
    [Fact]
    public async Task UsedBytes_SumsAudio_RecordingAttachments_AndSectionAttachments()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "u@x.test", Email = "u@x.test" });

        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, BlobKey = "k", SizeBytes = 100 };
        rec.Attachments.Add(new Attachment { Id = Guid.NewGuid(), Kind = AttachmentKind.File, SizeBytes = 20 });
        db.Recordings.Add(rec);

        var section = new Section { Id = Guid.NewGuid(), UserId = userId, Name = "F" };
        section.Attachments.Add(new SectionAttachment { Id = Guid.NewGuid(), Kind = AttachmentKind.File, SizeBytes = 5 });
        db.Sections.Add(section);
        await db.SaveChangesAsync();

        Assert.Equal(125, await new StorageUsage(db).UsedBytesAsync(userId));
    }

    [Fact]
    public async Task UsedBytes_ExcludesOtherUsersSectionAttachments()
    {
        using var db = TestDb.Create();
        var mine = Guid.NewGuid();
        var other = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = mine, UserName = "a@x.test", Email = "a@x.test" });
        db.Users.Add(new ApplicationUser { Id = other, UserName = "b@x.test", Email = "b@x.test" });

        var theirs = new Section { Id = Guid.NewGuid(), UserId = other, Name = "Theirs" };
        theirs.Attachments.Add(new SectionAttachment { Id = Guid.NewGuid(), Kind = AttachmentKind.File, SizeBytes = 999 });
        db.Sections.Add(theirs);
        await db.SaveChangesAsync();

        Assert.Equal(0, await new StorageUsage(db).UsedBytesAsync(mine));
    }

    [Fact]
    public async Task UsedBytes_IncludesScreenshotBytes()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var recordingId = Guid.NewGuid();
        db.Recordings.Add(new Recording { Id = recordingId, UserId = userId, Title = "t", SizeBytes = 1_000 });
        db.MeetingScreenshots.Add(new MeetingScreenshot
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RecordingId = recordingId,
            CapturedAtMs = 0,
            BlobKey = "k.png",
            ThumbBlobKey = "k.thumb.jpg",
            SizeBytes = 250,
        });
        await db.SaveChangesAsync();

        var used = await new StorageUsage(db).UsedBytesAsync(userId);

        Assert.Equal(1_250, used);
    }

    [Fact]
    public async Task UsedBytes_IgnoresAnotherUsersScreenshots()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.MeetingScreenshots.Add(new MeetingScreenshot
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            RecordingId = Guid.NewGuid(),
            CapturedAtMs = 0,
            BlobKey = "k.png",
            ThumbBlobKey = "k.thumb.jpg",
            SizeBytes = 900,
        });
        await db.SaveChangesAsync();

        Assert.Equal(0, await new StorageUsage(db).UsedBytesAsync(userId));
    }
}
