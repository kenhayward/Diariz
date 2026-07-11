using System.Text;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

public class SectionAttachmentsControllerTests
{
    private static SectionAttachmentsController Build(
        DiarizDbContext db, Guid userId, FakeAudioStorage? storage = null, long maxBytes = 50L * 1024 * 1024) =>
        new(db, storage ?? new FakeAudioStorage(), new StorageUsage(db),
            Options.Create(new AttachmentOptions { MaxBytes = maxBytes }), new Diariz.Api.Services.RoomScope(db))
        {
            ControllerContext = Http.Context(userId),
        };

    private static async Task<Section> Seed(DiarizDbContext db, Guid userId, long quota = 1_000_000)
    {
        db.Users.Add(new ApplicationUser { Id = userId, UserName = $"{userId}@x.test", Email = $"{userId}@x.test", QuotaBytes = quota });
        await db.SaveChangesAsync();
        var roomId = await new Diariz.Api.Services.RoomScope(db).PersonalRoomIdAsync(userId); // folders are room-scoped now
        var section = new Section { Id = Guid.NewGuid(), UserId = userId, RoomId = roomId, Name = "Folder" };
        db.Sections.Add(section);
        await db.SaveChangesAsync();
        return section;
    }

    private static IFormFile FileOf(string name, string contentType, byte[] bytes)
    {
        var stream = new MemoryStream(bytes);
        return new FormFile(stream, 0, bytes.Length, "file", name) { Headers = new HeaderDictionary(), ContentType = contentType };
    }

    [Fact]
    public async Task AddFile_StoresBlob_CreatesRow_AndCountsTowardQuota()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var section = await Seed(db, userId);
        var storage = new FakeAudioStorage();
        var controller = Build(db, userId, storage);

        var bytes = Encoding.UTF8.GetBytes("%PDF-1.4 fake");
        var result = await controller.AddFile(section.Id, FileOf("../notes.pdf", "application/pdf", bytes));

        var dto = Assert.IsType<AttachmentDto>(result.Value);
        Assert.Equal(AttachmentKind.File, dto.Kind);
        Assert.Equal("notes.pdf", dto.Name);            // path stripped
        Assert.Equal(bytes.Length, dto.SizeBytes);

        var a = await db.SectionAttachments.SingleAsync();
        Assert.Equal($"{userId}/section-attachments/{a.Id}.pdf", a.BlobKey);
        Assert.True(storage.Objects.ContainsKey(a.BlobKey!));
        Assert.Equal(bytes.Length, await new StorageUsage(db).UsedBytesAsync(userId)); // folder-attachment bytes count
    }

    [Fact]
    public async Task AddMarkdown_StoresMdBlob_WithMarkdownType()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var section = await Seed(db, userId);
        var controller = Build(db, userId);

        var dto = Assert.IsType<AttachmentDto>(
            (await controller.AddMarkdown(section.Id, new AddMarkdownAttachmentRequest("Meeting notes", "# Notes"))).Value);
        Assert.Equal("Meeting notes.md", dto.Name);
        Assert.Equal("text/markdown", dto.ContentType);

        var a = await db.SectionAttachments.SingleAsync();
        Assert.Equal($"{userId}/section-attachments/{a.Id}.md", a.BlobKey);
    }

    [Fact]
    public async Task AddUrl_CreatesRow_WithHostNameDefault()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var section = await Seed(db, userId);
        var controller = Build(db, userId);

        var dto = Assert.IsType<AttachmentDto>(
            (await controller.AddUrl(section.Id, new AddUrlAttachmentRequest("https://example.com/spec", null))).Value);
        Assert.Equal(AttachmentKind.Url, dto.Kind);
        Assert.Equal("example.com", dto.Name);
        Assert.Equal(0, dto.SizeBytes);
    }

    [Fact]
    public async Task Operations_OnAnotherUsersFolder_ReturnNotFound()
    {
        using var db = TestDb.Create();
        var section = await Seed(db, Guid.NewGuid());
        var caller = Guid.NewGuid();
        Users.Ensure(db, caller); // the caller's personal room is minted when scoping the ownership check
        var controller = Build(db, caller);

        Assert.IsType<NotFoundResult>((await controller.List(section.Id)).Result);
        Assert.IsType<NotFoundResult>(
            (await controller.AddMarkdown(section.Id, new AddMarkdownAttachmentRequest("n", "c"))).Result);
    }

    [Fact]
    public async Task Delete_File_RemovesBlobAndRow()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var section = await Seed(db, userId);
        var storage = new FakeAudioStorage();
        var controller = Build(db, userId, storage);
        var created = (await controller.AddFile(section.Id, FileOf("a.txt", "text/plain", new byte[4]))).Value!;
        var key = (await db.SectionAttachments.FindAsync(created.Id))!.BlobKey!;

        var result = await controller.Delete(section.Id, created.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.False(storage.Objects.ContainsKey(key));
        Assert.Empty(await db.SectionAttachments.ToListAsync());
    }

    [Fact]
    public async Task UpdateContent_OverwritesBlob_RecomputesSize()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var section = await Seed(db, userId);
        var storage = new FakeAudioStorage();
        var controller = Build(db, userId, storage);
        var created = (await controller.AddMarkdown(section.Id, new AddMarkdownAttachmentRequest("doc", "old"))).Value!;
        var key = (await db.SectionAttachments.FindAsync(created.Id))!.BlobKey!;

        var result = await controller.UpdateContent(section.Id, created.Id, new UpdateAttachmentContentRequest("# New\n\nlonger body"));

        Assert.IsType<NoContentResult>(result);
        var updated = await db.SectionAttachments.FindAsync(created.Id);
        var expected = Encoding.UTF8.GetBytes("# New\n\nlonger body");
        Assert.Equal(expected.Length, updated!.SizeBytes);
        Assert.Equal(expected, storage.Objects[key]);   // overwritten in place
    }

    [Fact]
    public async Task UpdateContent_OnNonMarkdown_ReturnsBadRequest()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var section = await Seed(db, userId);
        var controller = Build(db, userId, new FakeAudioStorage());
        var created = (await controller.AddFile(section.Id, FileOf("a.pdf", "application/pdf", new byte[4]))).Value!;

        var result = await controller.UpdateContent(section.Id, created.Id, new UpdateAttachmentContentRequest("x"));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task UpdateContent_OverQuotaOnDelta_Returns413()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var section = await Seed(db, userId, quota: 20);
        var controller = Build(db, userId);
        var created = (await controller.AddMarkdown(section.Id, new AddMarkdownAttachmentRequest("doc", "small"))).Value!;

        var result = await controller.UpdateContent(
            section.Id, created.Id, new UpdateAttachmentContentRequest(new string('x', 100)));

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task AddedAttachments_GetSequentialOrdinals()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var section = await Seed(db, userId);
        var controller = Build(db, userId);

        await controller.AddUrl(section.Id, new AddUrlAttachmentRequest("https://a.test", "a"));
        await controller.AddUrl(section.Id, new AddUrlAttachmentRequest("https://b.test", "b"));

        var ordinals = await db.SectionAttachments.OrderBy(a => a.Ordinal).Select(a => a.Ordinal).ToListAsync();
        Assert.Equal([0, 1], ordinals);
    }
}
