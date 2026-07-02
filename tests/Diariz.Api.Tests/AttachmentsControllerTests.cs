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

public class AttachmentsControllerTests
{
    private static AttachmentsController Build(
        DiarizDbContext db, Guid userId, FakeAudioStorage? storage = null, long maxBytes = 50L * 1024 * 1024) =>
        new(db, storage ?? new FakeAudioStorage(), new StorageUsage(db),
            Options.Create(new AttachmentOptions { MaxBytes = maxBytes }))
        {
            ControllerContext = Http.Context(userId),
        };

    private static async Task<Recording> Seed(DiarizDbContext db, Guid userId, long quota = 1_000_000)
    {
        db.Users.Add(new ApplicationUser { Id = userId, UserName = $"{userId}@x.test", Email = $"{userId}@x.test", QuotaBytes = quota });
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, BlobKey = "k", Status = RecordingStatus.Transcribed };
        db.Recordings.Add(rec);
        await db.SaveChangesAsync();
        return rec;
    }

    private static IFormFile FileOf(string name, string contentType, byte[] bytes)
    {
        var stream = new MemoryStream(bytes);
        return new FormFile(stream, 0, bytes.Length, "file", name) { Headers = new HeaderDictionary(), ContentType = contentType };
    }

    [Fact]
    public async Task AddMarkdown_StoresMdBlob_CreatesRow_WithMarkdownType()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await Seed(db, userId);
        var storage = new FakeAudioStorage();
        var controller = Build(db, userId, storage);

        var result = await controller.AddMarkdown(rec.Id, new AddMarkdownAttachmentRequest("Meeting notes", "# Notes\n\n- one"));

        var dto = Assert.IsType<AttachmentDto>(result.Value);
        Assert.Equal(AttachmentKind.File, dto.Kind);
        Assert.Equal("Meeting notes.md", dto.Name);          // .md appended
        Assert.Equal("text/markdown", dto.ContentType);

        var a = await db.Attachments.SingleAsync();
        Assert.Equal($"{userId}/attachments/{a.Id}.md", a.BlobKey);
        Assert.True(storage.Objects.ContainsKey(a.BlobKey!));
        Assert.Equal(Encoding.UTF8.GetByteCount("# Notes\n\n- one"), a.SizeBytes);
    }

    [Fact]
    public async Task AddMarkdown_KeepsMdExtension_AndDefaultsBlankNameToNote()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await Seed(db, userId);
        var c = Build(db, userId);

        var kept = Assert.IsType<AttachmentDto>((await c.AddMarkdown(rec.Id, new AddMarkdownAttachmentRequest("summary.md", "x"))).Value);
        Assert.Equal("summary.md", kept.Name);

        var blank = Assert.IsType<AttachmentDto>((await c.AddMarkdown(rec.Id, new AddMarkdownAttachmentRequest("  ", "x"))).Value);
        Assert.Equal("note.md", blank.Name);
    }

    [Fact]
    public async Task AddMarkdown_PreservesPunctuationInName_IncludingColon()
    {
        // "Email: <subject>" names (from the send_email tool) must survive intact — on Windows
        // Path.GetFileName would treat ':' as the volume separator and drop the "Email:" prefix.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await Seed(db, userId);
        var c = Build(db, userId);

        var dto = Assert.IsType<AttachmentDto>(
            (await c.AddMarkdown(rec.Id, new AddMarkdownAttachmentRequest("Email: Follow-ups", "x"))).Value);
        Assert.Equal("Email: Follow-ups.md", dto.Name);
    }

    [Fact]
    public async Task AddMarkdown_StripsSmuggledDirectoryPath_FromName()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await Seed(db, userId);
        var c = Build(db, userId);

        var win = Assert.IsType<AttachmentDto>(
            (await c.AddMarkdown(rec.Id, new AddMarkdownAttachmentRequest(@"..\..\secret", "x"))).Value);
        Assert.Equal("secret.md", win.Name);

        var nix = Assert.IsType<AttachmentDto>(
            (await c.AddMarkdown(rec.Id, new AddMarkdownAttachmentRequest("../../secret2", "x"))).Value);
        Assert.Equal("secret2.md", nix.Name);
    }

    [Fact]
    public async Task AddMarkdown_NotOwned_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await Seed(db, userId);
        var controller = Build(db, userId);

        var result = await controller.AddMarkdown(Guid.NewGuid(), new AddMarkdownAttachmentRequest("n", "c"));
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task AddMarkdown_OverQuota_Returns413()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await Seed(db, userId, quota: 10);
        var controller = Build(db, userId);

        var result = await controller.AddMarkdown(rec.Id, new AddMarkdownAttachmentRequest("n", new string('x', 100)));
        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, obj.StatusCode);
        Assert.Empty(await db.Attachments.ToListAsync());
    }

    [Fact]
    public async Task AddFile_StoresBlob_CreatesRow_AndCountsTowardQuota()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await Seed(db, userId);
        var storage = new FakeAudioStorage();
        var controller = Build(db, userId, storage);

        var bytes = Encoding.UTF8.GetBytes("%PDF-1.4 fake");
        var result = await controller.AddFile(rec.Id, FileOf("../notes.pdf", "application/pdf", bytes));

        var dto = Assert.IsType<AttachmentDto>(result.Value);
        Assert.Equal(AttachmentKind.File, dto.Kind);
        Assert.Equal("notes.pdf", dto.Name);            // path stripped
        Assert.Equal(bytes.Length, dto.SizeBytes);

        var a = await db.Attachments.SingleAsync();
        Assert.Equal($"{userId}/attachments/{a.Id}.pdf", a.BlobKey);
        Assert.True(storage.Objects.ContainsKey(a.BlobKey!));
        Assert.Equal(bytes.Length, await new StorageUsage(db).UsedBytesAsync(userId)); // attachment bytes count
    }

    [Fact]
    public async Task AddFile_StripsSmuggledWindowsPath_FromFileName()
    {
        // A malicious multipart could smuggle a backslash path in the filename. Path.GetFileName only
        // strips '\' on Windows, so on the Linux production containers it would pass through — the name
        // must be stripped on every platform.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await Seed(db, userId);
        var controller = Build(db, userId, new FakeAudioStorage());

        var dto = Assert.IsType<AttachmentDto>(
            (await controller.AddFile(rec.Id, FileOf(@"..\..\evil.pdf", "application/pdf", Encoding.UTF8.GetBytes("%PDF-1.4")))).Value);
        Assert.Equal("evil.pdf", dto.Name);
    }

    [Fact]
    public async Task AddFile_OverTheSizeCap_Returns413()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await Seed(db, userId);
        var controller = Build(db, userId, maxBytes: 8);

        var result = await controller.AddFile(rec.Id, FileOf("big.bin", "application/octet-stream", new byte[64]));

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, status.StatusCode);
        Assert.Empty(await db.Attachments.ToListAsync());
    }

    [Fact]
    public async Task AddFile_ExceedingQuota_Returns413()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await Seed(db, userId, quota: 10);
        var controller = Build(db, userId);

        var result = await controller.AddFile(rec.Id, FileOf("a.txt", "text/plain", new byte[64]));

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, Assert.IsType<ObjectResult>(result.Result).StatusCode);
    }

    [Fact]
    public async Task AddFile_OnAnotherUsersRecording_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var rec = await Seed(db, Guid.NewGuid());
        var controller = Build(db, userId: Guid.NewGuid());

        var result = await controller.AddFile(rec.Id, FileOf("a.txt", "text/plain", new byte[4]));

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task AddUrl_CreatesRow_WithHostNameDefault()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await Seed(db, userId);
        var controller = Build(db, userId);

        var result = await controller.AddUrl(rec.Id, new AddUrlAttachmentRequest("https://example.com/spec", null));

        var dto = Assert.IsType<AttachmentDto>(result.Value);
        Assert.Equal(AttachmentKind.Url, dto.Kind);
        Assert.Equal("example.com", dto.Name);
        Assert.Equal("https://example.com/spec", dto.Url);
        Assert.Equal(0, dto.SizeBytes);
    }

    [Fact]
    public async Task AddUrl_RejectsNonHttpScheme()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await Seed(db, userId);
        var controller = Build(db, userId);

        var result = await controller.AddUrl(rec.Id, new AddUrlAttachmentRequest("file:///etc/passwd", "x"));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task AddedAttachments_GetSequentialOrdinals()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await Seed(db, userId);
        var controller = Build(db, userId);

        await controller.AddUrl(rec.Id, new AddUrlAttachmentRequest("https://a.test", "a"));
        await controller.AddUrl(rec.Id, new AddUrlAttachmentRequest("https://b.test", "b"));

        var ordinals = await db.Attachments.OrderBy(a => a.Ordinal).Select(a => a.Ordinal).ToListAsync();
        Assert.Equal([0, 1], ordinals);
    }

    [Fact]
    public async Task Rename_UpdatesName()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await Seed(db, userId);
        var controller = Build(db, userId);
        var created = (await controller.AddUrl(rec.Id, new AddUrlAttachmentRequest("https://a.test", "a"))).Value!;

        var result = await controller.Rename(rec.Id, created.Id, new RenameAttachmentRequest("Renamed"));

        Assert.IsType<NoContentResult>(result);
        Assert.Equal("Renamed", (await db.Attachments.FindAsync(created.Id))!.Name);
    }

    [Fact]
    public async Task Delete_File_RemovesBlobAndRow()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await Seed(db, userId);
        var storage = new FakeAudioStorage();
        var controller = Build(db, userId, storage);
        var created = (await controller.AddFile(rec.Id, FileOf("a.txt", "text/plain", new byte[4]))).Value!;
        var key = (await db.Attachments.FindAsync(created.Id))!.BlobKey!;

        var result = await controller.Delete(rec.Id, created.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.False(storage.Objects.ContainsKey(key)); // blob removed
        Assert.Empty(await db.Attachments.ToListAsync());
    }

    [Fact]
    public async Task Content_ReturnsTheStoredBytes()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await Seed(db, userId);
        var storage = new FakeAudioStorage();
        var controller = Build(db, userId, storage);
        var bytes = Encoding.UTF8.GetBytes("hello");
        var created = (await controller.AddFile(rec.Id, FileOf("a.txt", "text/plain", bytes))).Value!;

        var result = await controller.Content(rec.Id, created.Id);

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("text/plain", file.ContentType);
        using var ms = new MemoryStream();
        await file.FileStream.CopyToAsync(ms);
        Assert.Equal(bytes, ms.ToArray());
    }

    [Fact]
    public async Task List_OnAnotherUsersRecording_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var rec = await Seed(db, Guid.NewGuid());
        var controller = Build(db, userId: Guid.NewGuid());

        Assert.IsType<NotFoundResult>((await controller.List(rec.Id)).Result);
    }
}
