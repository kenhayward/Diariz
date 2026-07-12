using System.Text;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;

namespace Diariz.Api.Tests;

/// <summary>List/get/edit/delete/email/download for a recording's <see cref="FormulaResult"/>s. Phase 1 access:
/// view requires recording ownership; edit/delete requires being the result's creator OR the recording owner
/// (see <see cref="FormulaResultsController"/>'s factored access checks - Phase 2 rooms will extend these).</summary>
public class FormulaResultsControllerTests
{
    private static FormulaResultsController Build(DiarizDbContext db, Guid userId, IEmailSender? email = null) =>
        new(db, email ?? new FakeEmailSender()) { ControllerContext = Http.Context(userId) };

    private static async Task<Recording> SeedRecording(DiarizDbContext db, Guid ownerId, string title = "Sync")
    {
        Users.Ensure(db, ownerId);
        var rec = new Recording { Id = Guid.NewGuid(), UserId = ownerId, BlobKey = "k", Title = title };
        db.Recordings.Add(rec);
        await db.SaveChangesAsync();
        return rec;
    }

    private static FormulaResult NewResult(Guid recordingId, Guid? creatorId, string name = "Key Decisions", string text = "- Decision one") => new()
    {
        Id = Guid.NewGuid(), RecordingId = recordingId, CreatedByUserId = creatorId,
        Name = name, Text = text, Ordinal = 0,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    // ---- List ----

    [Fact]
    public async Task List_ReturnsRecordingsResults_WithoutText_ForOwner()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var rec = await SeedRecording(db, owner);
        var r1 = NewResult(rec.Id, owner, "First");
        r1.Ordinal = 0;
        var r2 = NewResult(rec.Id, owner, "Second");
        r2.Ordinal = 1;
        db.FormulaResults.AddRange(r1, r2);
        await db.SaveChangesAsync();

        var controller = Build(db, owner);
        var result = await controller.List(rec.Id);

        var list = Assert.IsAssignableFrom<IReadOnlyList<FormulaResultDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(2, list.Count);
        Assert.Equal("First", list[0].Name);
        Assert.Equal("Second", list[1].Name);
    }

    [Fact]
    public async Task List_ForNonOwner_Returns404()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var rec = await SeedRecording(db, owner);
        db.FormulaResults.Add(NewResult(rec.Id, owner));
        await db.SaveChangesAsync();

        var controller = Build(db, Guid.NewGuid());
        var result = await controller.List(rec.Id);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // ---- Get ----

    [Fact]
    public async Task Get_ReturnsText_ForOwner()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var rec = await SeedRecording(db, owner);
        var fr = NewResult(rec.Id, owner, text: "# Decisions\n\n- One");
        db.FormulaResults.Add(fr);
        await db.SaveChangesAsync();

        var controller = Build(db, owner);
        var result = await controller.Get(rec.Id, fr.Id);

        var dto = Assert.IsType<FormulaResultTextDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("# Decisions\n\n- One", dto.Text);
    }

    [Fact]
    public async Task Get_ForNonViewer_Returns404()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var rec = await SeedRecording(db, owner);
        var fr = NewResult(rec.Id, owner);
        db.FormulaResults.Add(fr);
        await db.SaveChangesAsync();

        var controller = Build(db, Guid.NewGuid());
        var result = await controller.Get(rec.Id, fr.Id);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Get_UnknownResultId_Returns404()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var rec = await SeedRecording(db, owner);
        await db.SaveChangesAsync();

        var controller = Build(db, owner);
        var result = await controller.Get(rec.Id, Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // ---- Update ----

    [Fact]
    public async Task Update_ByCreator_ChangesTextAndUpdatedAt()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var rec = await SeedRecording(db, owner);
        var fr = NewResult(rec.Id, owner, text: "Old text");
        fr.UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1);
        db.FormulaResults.Add(fr);
        await db.SaveChangesAsync();
        var before = fr.UpdatedAt;

        var controller = Build(db, owner);
        var result = await controller.Update(rec.Id, fr.Id, new UpdateFormulaResultRequest("New text"));

        var dto = Assert.IsType<FormulaResultDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(fr.Id, dto.Id);
        var updated = await db.FormulaResults.FindAsync(fr.Id);
        Assert.Equal("New text", updated!.Text);
        Assert.True(updated.UpdatedAt > before);
    }

    [Fact]
    public async Task Update_ByRecordingOwner_WhenCreatorIsSomeoneElse_Ok()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var creator = Guid.NewGuid();
        var rec = await SeedRecording(db, owner);
        var fr = NewResult(rec.Id, creator);
        db.FormulaResults.Add(fr);
        await db.SaveChangesAsync();

        var controller = Build(db, owner);
        var result = await controller.Update(rec.Id, fr.Id, new UpdateFormulaResultRequest("Edited by owner"));

        var dto = Assert.IsType<FormulaResultDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(fr.Id, dto.Id);
    }

    [Fact]
    public async Task Update_ByUnrelatedUser_WhoCannotView_Returns404()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var rec = await SeedRecording(db, owner);
        var fr = NewResult(rec.Id, owner);
        db.FormulaResults.Add(fr);
        await db.SaveChangesAsync();

        var controller = Build(db, Guid.NewGuid());
        var result = await controller.Update(rec.Id, fr.Id, new UpdateFormulaResultRequest("Hacked"));

        Assert.IsType<NotFoundResult>(result.Result);
        var untouched = await db.FormulaResults.FindAsync(fr.Id);
        Assert.Equal(fr.Text, untouched!.Text);
    }

    // ---- Delete ----

    [Fact]
    public async Task Delete_ByCreator_Returns204()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var rec = await SeedRecording(db, owner);
        var fr = NewResult(rec.Id, owner);
        db.FormulaResults.Add(fr);
        await db.SaveChangesAsync();

        var controller = Build(db, owner);
        var result = await controller.Delete(rec.Id, fr.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.False(db.FormulaResults.Any(x => x.Id == fr.Id));
    }

    [Fact]
    public async Task Delete_ByNonViewer_Returns404_AndDoesNotDelete()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var rec = await SeedRecording(db, owner);
        var fr = NewResult(rec.Id, owner);
        db.FormulaResults.Add(fr);
        await db.SaveChangesAsync();

        var controller = Build(db, Guid.NewGuid());
        var result = await controller.Delete(rec.Id, fr.Id);

        Assert.IsType<NotFoundResult>(result);
        Assert.True(db.FormulaResults.Any(x => x.Id == fr.Id));
    }

    // ---- Email ----

    [Fact]
    public async Task Email_SendsToSignedInUsersOwnAddress()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var rec = await SeedRecording(db, owner, "Team Sync");
        var fr = NewResult(rec.Id, owner, "Key Decisions", "# Key Decisions\n\n- Ship it");
        db.FormulaResults.Add(fr);
        await db.SaveChangesAsync();
        var email = new FakeEmailSender { Sent = true };
        var controller = Build(db, owner, email);

        var result = await controller.Email(rec.Id, fr.Id);

        Assert.IsType<OkResult>(result);
        var msg = Assert.Single(email.Messages);
        Assert.Equal($"{owner:N}@x.test", msg.To);
        Assert.Contains("Key Decisions", msg.Subject);
        Assert.Contains("Ship it", msg.Body);
    }

    [Fact]
    public async Task Email_WhenSmtpNotConfigured_ReturnsBadRequest()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var rec = await SeedRecording(db, owner);
        var fr = NewResult(rec.Id, owner);
        db.FormulaResults.Add(fr);
        await db.SaveChangesAsync();
        var controller = Build(db, owner, new FakeEmailSender { Sent = false });

        var result = await controller.Email(rec.Id, fr.Id);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("configured", (string)badRequest.Value!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Email_ForNonViewer_Returns404()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var rec = await SeedRecording(db, owner);
        var fr = NewResult(rec.Id, owner);
        db.FormulaResults.Add(fr);
        await db.SaveChangesAsync();
        var email = new FakeEmailSender();
        var controller = Build(db, Guid.NewGuid(), email);

        var result = await controller.Email(rec.Id, fr.Id);

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(email.Messages);
    }

    // ---- Download ----

    [Fact]
    public async Task Download_ReturnsMarkdownBytes_WithMdFilename()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var rec = await SeedRecording(db, owner);
        var fr = NewResult(rec.Id, owner, "Key Decisions", "# Key Decisions\n\n- Ship it");
        db.FormulaResults.Add(fr);
        await db.SaveChangesAsync();

        var controller = Build(db, owner);
        var result = await controller.Download(rec.Id, fr.Id);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("text/markdown", file.ContentType);
        Assert.EndsWith(".md", file.FileDownloadName);
        Assert.Equal("# Key Decisions\n\n- Ship it", Encoding.UTF8.GetString(file.FileContents));
    }

    [Fact]
    public async Task Download_ForNonViewer_Returns404()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var rec = await SeedRecording(db, owner);
        var fr = NewResult(rec.Id, owner);
        db.FormulaResults.Add(fr);
        await db.SaveChangesAsync();

        var controller = Build(db, Guid.NewGuid());
        var result = await controller.Download(rec.Id, fr.Id);

        Assert.IsType<NotFoundResult>(result);
    }
}
