using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Diariz.Api.Configuration;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Diariz.Api.IntegrationTests;

/// <summary>Deleting a user must not leak their MinIO blobs, and must not let a SectionAttachment they
/// uploaded into someone else's folder survive with a dangling UploadedByUserId. Verified against real
/// Postgres (cascade behaviour) and real MinIO (blob presence/absence) - the in-memory provider enforces
/// neither, so it cannot prove either half of this.</summary>
[Collection(IntegrationCollection.Name)]
public class UserDeletionBlobCleanupIntegrationTests(ContainersFixture fx)
{
    private AudioStorage CreateStorage()
    {
        var opts = new StorageOptions
        {
            Endpoint = fx.MinioEndpoint,
            AccessKey = fx.MinioAccessKey,
            SecretKey = fx.MinioSecretKey,
            Bucket = $"recordings-{Guid.NewGuid():N}",
            ForcePathStyle = true
        };
        var cfg = new AmazonS3Config
        {
            ServiceURL = opts.Endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1"
        };
        var s3 = new AmazonS3Client(new BasicAWSCredentials(opts.AccessKey, opts.SecretKey), cfg);
        return new AudioStorage(s3, Options.Create(opts));
    }

    private ServiceProvider BuildIdentity()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddDbContext<DiarizDbContext>(o => o.UseNpgsql(fx.PostgresConnectionString, x => x.UseVector()));
        services.AddIdentityCore<ApplicationUser>(o =>
            {
                o.Password.RequiredLength = 8;
                o.Password.RequireUppercase = true;
                o.Password.RequireLowercase = true;
                o.Password.RequireDigit = true;
                o.Password.RequireNonAlphanumeric = true;
                o.User.RequireUniqueEmail = true;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<DiarizDbContext>()
            .AddDefaultTokenProviders();
        return services.BuildServiceProvider();
    }

    private static AdminUsersController AdminUsers(
        UserManager<ApplicationUser> users, DiarizDbContext db, Guid callerId, IAudioStorage storage) =>
        new(users, new FakeEmailSender(), db, new PlatformSettingsService(db),
            Options.Create(new AppPublicOptions { PublicUrl = "http://localhost:8081" }), new UserPermissions(db),
            storage, NullLogger<AdminUsersController>.Instance)
        {
            ControllerContext = Http.Context(callerId),
        };

    [Fact]
    public async Task DeletingAUser_DeletesTheirBlobs_AndRepointsForeignFolderAttachments()
    {
        await using var sp = BuildIdentity();
        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var db = sp.GetRequiredService<DiarizDbContext>();
        var storage = CreateStorage();
        await storage.EnsureBucketAsync();

        // The caller: an administrator who may delete users.
        var admin = new ApplicationUser { UserName = $"adm-{Guid.NewGuid():N}@x.test", Email = $"adm-{Guid.NewGuid():N}@x.test" };
        await users.CreateAsync(admin);
        Perms.Grant(db, admin.Id, Perms.Administrator);

        // The victim, and the owner of a folder the victim uploaded into but does not own.
        var victim = new ApplicationUser { UserName = $"v-{Guid.NewGuid():N}@x.test", Email = $"v-{Guid.NewGuid():N}@x.test" };
        await users.CreateAsync(victim);
        var folderOwner = new ApplicationUser { UserName = $"f-{Guid.NewGuid():N}@x.test", Email = $"f-{Guid.NewGuid():N}@x.test" };
        await users.CreateAsync(folderOwner);

        var scope = new RoomScope(db);
        var victimRoom = await scope.PersonalRoomIdAsync(victim.Id);
        var ownerRoom = await scope.PersonalRoomIdAsync(folderOwner.Id);

        async Task Put(string key, string content)
        {
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(content));
            await storage.UploadAsync(key, ms, "application/octet-stream");
        }

        var audioKey = $"{victim.Id}/{Guid.NewGuid():N}.webm";
        var attachmentKey = $"{victim.Id}/attachments/{Guid.NewGuid():N}.pdf";
        var screenshotKey = $"{victim.Id}/screenshots/{Guid.NewGuid():N}.png";
        var thumbKey = $"{victim.Id}/screenshots/{Guid.NewGuid():N}-thumb.jpg";
        var ownFolderKey = $"{victim.Id}/section-attachments/{Guid.NewGuid():N}.md";
        // Uploaded by the victim into folderOwner's folder - the key still carries the victim's prefix
        // (the original uploader), which is exactly why it can never be reconstructed from UploadedByUserId.
        var foreignFolderKey = $"{victim.Id}/section-attachments/{Guid.NewGuid():N}.md";

        await Put(audioKey, "audio bytes");
        await Put(attachmentKey, "pdf bytes");
        await Put(screenshotKey, "png bytes");
        await Put(thumbKey, "jpg bytes");
        await Put(ownFolderKey, "own folder markdown");
        await Put(foreignFolderKey, "foreign folder markdown");

        var recordingId = Guid.NewGuid();
        var ownSectionId = Guid.NewGuid();
        var foreignSectionId = Guid.NewGuid();
        var ownAttachmentId = Guid.NewGuid();
        var foreignAttachmentId = Guid.NewGuid();

        db.Recordings.Add(new Recording
        {
            Id = recordingId, UserId = victim.Id, Title = "R", BlobKey = audioKey, SizeBytes = 11,
            Status = RecordingStatus.Transcribed,
        });
        db.Attachments.Add(new Attachment
        {
            Id = Guid.NewGuid(), RecordingId = recordingId, Kind = AttachmentKind.File, Name = "doc.pdf",
            BlobKey = attachmentKey, ContentType = "application/pdf", SizeBytes = 9,
        });
        db.MeetingScreenshots.Add(new MeetingScreenshot
        {
            Id = Guid.NewGuid(), UserId = victim.Id, RecordingId = recordingId, CapturedAtMs = 1000,
            BlobKey = screenshotKey, ThumbBlobKey = thumbKey, Width = 100, Height = 100,
        });
        // The victim's own folder: cascades away entirely with the victim (Section.UserId FK is Cascade).
        db.Sections.Add(new Section { Id = ownSectionId, UserId = victim.Id, RoomId = victimRoom, Name = "Victim's folder" });
        db.SectionAttachments.Add(new SectionAttachment
        {
            Id = ownAttachmentId, SectionId = ownSectionId, Kind = AttachmentKind.File, Name = "own.md",
            BlobKey = ownFolderKey, ContentType = "text/markdown", SizeBytes = 19, UploadedByUserId = victim.Id,
        });
        // Someone else's folder the victim uploaded into: the Section survives (owned by folderOwner), so
        // this SectionAttachment row must survive too - re-pointed, not deleted.
        db.Sections.Add(new Section { Id = foreignSectionId, UserId = folderOwner.Id, RoomId = ownerRoom, Name = "Owner's folder" });
        db.SectionAttachments.Add(new SectionAttachment
        {
            Id = foreignAttachmentId, SectionId = foreignSectionId, Kind = AttachmentKind.File, Name = "foreign.md",
            BlobKey = foreignFolderKey, ContentType = "text/markdown", SizeBytes = 23, UploadedByUserId = victim.Id,
        });
        await db.SaveChangesAsync();

        Assert.Equal(0, await new StorageUsage(db).UsedBytesAsync(folderOwner.Id));

        var result = await AdminUsers(users, db, admin.Id, storage).Delete(victim.Id);
        Assert.IsType<NoContentResult>(result);

        // The victim's own blobs are gone: audio, recording attachment, both screenshot images, own-folder attachment.
        Assert.Null(await storage.OpenAsync(audioKey));
        Assert.Null(await storage.OpenAsync(attachmentKey));
        Assert.Null(await storage.OpenAsync(screenshotKey));
        Assert.Null(await storage.OpenAsync(thumbKey));
        Assert.Null(await storage.OpenAsync(ownFolderKey));

        // The row that survives (foreign-folder attachment) keeps its blob, and its blob key is untouched -
        // never reconstructed from the new owner.
        Assert.NotNull(await storage.OpenAsync(foreignFolderKey));

        await using var verify = fx.CreateDbContext();
        var survivor = await verify.SectionAttachments.SingleAsync(a => a.Id == foreignAttachmentId);
        Assert.Equal(folderOwner.Id, survivor.UploadedByUserId); // re-pointed to the folder owner
        Assert.Equal(foreignFolderKey, survivor.BlobKey); // blob key left exactly as it was

        // The victim's own folder (and its attachment row) is gone via cascade.
        Assert.False(await verify.Sections.AnyAsync(s => s.Id == ownSectionId));
        Assert.False(await verify.SectionAttachments.AnyAsync(a => a.Id == ownAttachmentId));
        Assert.False(await verify.Recordings.AnyAsync(r => r.Id == recordingId));

        // The folder owner's storage ledger now includes the re-pointed bytes.
        Assert.Equal(23, await new StorageUsage(verify).UsedBytesAsync(folderOwner.Id));

        Assert.Null(await users.FindByIdAsync(victim.Id.ToString()));
    }
}
