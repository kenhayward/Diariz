using System.Text;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diariz.Api.IntegrationTests;

/// <summary>Real-Postgres fidelity for folder-direct attachments: the migration applies, the
/// section-delete cascade fires, and the quota sum spans recording + section attachments under Npgsql.</summary>
[Collection(IntegrationCollection.Name)]
public class SectionAttachmentsIntegrationTests(ContainersFixture fx)
{
    private static Task<Guid> RoomOf(Diariz.Domain.DiarizDbContext db, Guid owner) => new RoomScope(db).PersonalRoomIdAsync(owner);

    private static SectionAttachmentsController Build(
        Diariz.Domain.DiarizDbContext db, Guid userId, FakeAudioStorage storage) =>
        new(db, storage, new StorageUsage(db), Options.Create(new AttachmentOptions { MaxBytes = 50L * 1024 * 1024 }),
            new RoomScope(db))
        { ControllerContext = Http.Context(userId) };

    private async Task<Guid> SeedUser(long quota = 1_000_000)
    {
        await using var db = fx.CreateDbContext();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test", QuotaBytes = quota };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static IFormFile FileOf(string name, string contentType, byte[] bytes) =>
        new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", name)
        { Headers = new HeaderDictionary(), ContentType = contentType };

    [Fact]
    public async Task Add_then_delete_folder_attachment_round_trips_under_postgres()
    {
        var userId = await SeedUser();
        var sectionId = Guid.NewGuid();
        await using (var db = fx.CreateDbContext())
        {
            db.Sections.Add(new Section { Id = sectionId, UserId = userId, RoomId = await RoomOf(db, userId), Name = "F" });
            await db.SaveChangesAsync();
        }

        var storage = new FakeAudioStorage();
        await using var db2 = fx.CreateDbContext();
        var controller = Build(db2, userId, storage);
        var created = (await controller.AddFile(sectionId, FileOf("spec.pdf", "application/pdf", Encoding.UTF8.GetBytes("%PDF-1.4")))).Value!;

        var stored = await db2.SectionAttachments.SingleAsync(a => a.SectionId == sectionId);
        Assert.Equal(created.Id, stored.Id);

        Assert.IsType<Microsoft.AspNetCore.Mvc.NoContentResult>(await controller.Delete(sectionId, created.Id));
        Assert.False(await db2.SectionAttachments.AnyAsync(a => a.SectionId == sectionId));
    }

    [Fact]
    public async Task Deleting_a_section_cascades_to_its_direct_attachments()
    {
        var userId = await SeedUser();
        var sectionId = Guid.NewGuid();
        await using (var db = fx.CreateDbContext())
        {
            db.Sections.Add(new Section { Id = sectionId, UserId = userId, RoomId = await RoomOf(db, userId), Name = "F" });
            db.SectionAttachments.Add(new SectionAttachment
            {
                Id = Guid.NewGuid(), SectionId = sectionId, Kind = AttachmentKind.File, Name = "a.md",
                BlobKey = $"{userId}/section-attachments/x.md", ContentType = "text/markdown", SizeBytes = 3,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
        {
            db.Sections.Remove(await db.Sections.SingleAsync(s => s.Id == sectionId));
            await db.SaveChangesAsync();
        }

        await using var verify = fx.CreateDbContext();
        Assert.False(await verify.SectionAttachments.AnyAsync(x => x.SectionId == sectionId));
    }

    [Fact]
    public async Task Quota_sums_recording_and_section_attachment_bytes()
    {
        var userId = await SeedUser();
        await using var db = fx.CreateDbContext();
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, Title = "R", BlobKey = "k", SizeBytes = 100 };
        rec.Attachments.Add(new Attachment { Id = Guid.NewGuid(), Kind = AttachmentKind.File, Name = "r.pdf", SizeBytes = 20 });
        db.Recordings.Add(rec);
        var section = new Section { Id = Guid.NewGuid(), UserId = userId, RoomId = await RoomOf(db, userId), Name = "F" };
        section.Attachments.Add(new SectionAttachment { Id = Guid.NewGuid(), Kind = AttachmentKind.File, Name = "s.md", SizeBytes = 7 });
        db.Sections.Add(section);
        await db.SaveChangesAsync();

        Assert.Equal(127, await new StorageUsage(db).UsedBytesAsync(userId));
    }
}
