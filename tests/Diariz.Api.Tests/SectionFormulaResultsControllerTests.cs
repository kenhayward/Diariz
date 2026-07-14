using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;

namespace Diariz.Api.Tests;

/// <summary>The async folder (section) formula-run endpoint: gate folder membership, validate formula
/// run-access + LLM config via <see cref="IFormulaRunner.ValidateFormulaRunAccessAsync"/>, require at least one
/// included meeting, create a <c>Generating</c> <see cref="SectionFormulaResult"/>, enqueue a section-scoped
/// <see cref="FormulaRunJob"/>, and return 202. Mirrors the recording run in <c>FormulasControllerTests</c>.</summary>
public class SectionFormulaResultsControllerTests
{
    private static SectionFormulaResultsController Build(DiarizDbContext db, Guid userId,
        IFormulaRunner? runner = null, FakeJobQueue? queue = null, FakeHubContext? hub = null,
        IEmailSender? email = null) =>
        new(db, runner ?? new FakeFormulaRunner(), queue ?? new FakeJobQueue(), hub ?? new FakeHubContext(),
            new RoomScope(db), email ?? new FakeEmailSender())
        { ControllerContext = Http.Context(userId) };

    // A folder in the given user's own personal room.
    private static async Task<Section> Section(DiarizDbContext db, Guid userId)
    {
        Users.Ensure(db, userId);
        var roomId = await new RoomScope(db).PersonalRoomIdAsync(userId);
        var s = new Section { Id = Guid.NewGuid(), UserId = userId, RoomId = roomId, Name = "F" };
        db.Sections.Add(s);
        await db.SaveChangesAsync();
        return s;
    }

    // Seeds a recording + its main placement in the owner's personal room, filed under the section.
    private static async Task<Recording> Recording(DiarizDbContext db, Guid userId, Guid sectionId)
    {
        Users.Ensure(db, userId);
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, Title = "Rec", BlobKey = "k" };
        db.Recordings.Add(rec);
        await db.SaveChangesAsync();
        await new RoomScope(db).PlaceInMainRoomAsync(rec.Id, userId, sectionId);
        return rec;
    }

    private static Formula RunnableFormula(Guid formulaId, string name = "Key Decisions") => new()
    {
        Id = formulaId, Scope = FormulaScope.Personal, OwnerUserId = Guid.NewGuid(),
        Name = name, ContentJson = TemplateContent.FromPrompt("P").Serialize(), Context = FormulaContext.Transcript, Enabled = true,
    };

    [Fact]
    public async Task Run_Valid_CreatesGeneratingResult_EnqueuesSectionJob_Returns202()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var formulaId = Guid.NewGuid();
        var section = await Section(db, userId);
        await Recording(db, userId, section.Id);
        var runner = new FakeFormulaRunner { ValidatedFormula = RunnableFormula(formulaId) };
        var queue = new FakeJobQueue();
        var hub = new FakeHubContext();
        var controller = Build(db, userId, runner, queue, hub);

        var result = await controller.Run(section.Id, formulaId, CancellationToken.None);

        // 202 Accepted with a pending section DTO.
        var dto = Assert.IsType<SectionFormulaResultDto>(Assert.IsType<AcceptedResult>(result.Result).Value);
        Assert.Equal("Generating", dto.Status);
        Assert.Null(dto.Error);
        Assert.Equal(section.Id, dto.SectionId);
        Assert.Equal("Key Decisions", dto.Name);
        Assert.Equal(userId, dto.CreatedByUserId);
        Assert.Equal((userId, formulaId), runner.LastFormulaAccessCall);

        // A pending row was persisted.
        var row = Assert.Single(db.SectionFormulaResults);
        Assert.Equal(FormulaRunStatus.Generating, row.Status);
        Assert.Equal("Key Decisions", row.Name);
        Assert.Equal(userId, row.CreatedByUserId);
        Assert.Equal(section.Id, row.SectionId);
        Assert.Equal(formulaId, row.FormulaId);
        Assert.Equal(0, row.Ordinal);
        Assert.Equal(row.Id, dto.Id);

        // Exactly one job was enqueued, scoped to the section (no recording).
        var job = Assert.Single(queue.FormulaRunJobs);
        Assert.Null(job.RecordingId);
        Assert.Equal(section.Id, job.SectionId);
        Assert.Equal(row.Id, job.ResultId);
        Assert.Equal(formulaId, job.FormulaId);
        Assert.Equal(userId, job.UserId);

        // The owner was notified of the pending run.
        var msg = Assert.Single(hub.Sent);
        Assert.Equal("FormulaResultStatusChanged", msg.Method);
    }

    [Fact]
    public async Task Run_Valid_NextResult_GetsNextOrdinal()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var formulaId = Guid.NewGuid();
        var section = await Section(db, userId);
        await Recording(db, userId, section.Id);
        db.SectionFormulaResults.Add(new SectionFormulaResult
        {
            Id = Guid.NewGuid(), SectionId = section.Id, CreatedByUserId = userId,
            Name = "Existing", Ordinal = 5, Status = FormulaRunStatus.Ready,
        });
        await db.SaveChangesAsync();
        var runner = new FakeFormulaRunner { ValidatedFormula = RunnableFormula(formulaId) };
        var controller = Build(db, userId, runner);

        await controller.Run(section.Id, formulaId, CancellationToken.None);

        var created = db.SectionFormulaResults.Single(r => r.Name == "Key Decisions");
        Assert.Equal(6, created.Ordinal);
    }

    [Fact]
    public async Task Run_NonMember_Returns404_NoRowNoJob()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var section = await Section(db, owner);
        await Recording(db, owner, section.Id);
        var outsider = Guid.NewGuid();
        Users.Ensure(db, outsider);
        var runner = new FakeFormulaRunner { ValidatedFormula = RunnableFormula(Guid.NewGuid()) };
        var queue = new FakeJobQueue();
        var controller = Build(db, outsider, runner, queue);

        var result = await controller.Run(section.Id, Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.Empty(db.SectionFormulaResults);
        Assert.Empty(queue.FormulaRunJobs);
        Assert.Equal(0, runner.Calls); // the runner is never consulted for a non-member
    }

    [Fact]
    public async Task Run_MissingSection_Returns404()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        Users.Ensure(db, userId);
        var controller = Build(db, userId);

        var result = await controller.Run(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.Empty(db.SectionFormulaResults);
    }

    [Fact]
    public async Task Run_FormulaAccessException_Returns403_NoRowNoJob()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var section = await Section(db, userId);
        await Recording(db, userId, section.Id);
        var runner = new FakeFormulaRunner { ThrowOnCall = new FormulaAccessException("nope") };
        var queue = new FakeJobQueue();
        var controller = Build(db, userId, runner, queue);

        var result = await controller.Run(section.Id, Guid.NewGuid(), CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, status.StatusCode);
        Assert.Empty(db.SectionFormulaResults);
        Assert.Empty(queue.FormulaRunJobs);
    }

    [Fact]
    public async Task Run_FormulaNotConfiguredException_Returns400()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var section = await Section(db, userId);
        await Recording(db, userId, section.Id);
        var runner = new FakeFormulaRunner { ThrowOnCall = new FormulaNotConfiguredException("no endpoint") };
        var controller = Build(db, userId, runner);

        var result = await controller.Run(section.Id, Guid.NewGuid(), CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("Formulas need an AI endpoint. Set one in Settings.", badRequest.Value);
        Assert.Empty(db.SectionFormulaResults);
    }

    [Fact]
    public async Task Run_FormulaNotFoundException_Returns404()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var section = await Section(db, userId);
        await Recording(db, userId, section.Id);
        var runner = new FakeFormulaRunner { ThrowOnCall = new FormulaNotFoundException("missing") };
        var controller = Build(db, userId, runner);

        var result = await controller.Run(section.Id, Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.Empty(db.SectionFormulaResults);
    }

    [Fact]
    public async Task Run_FolderWithNoIncludedRecordings_Returns400_NoRowNoJob()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var formulaId = Guid.NewGuid();
        var section = await Section(db, userId); // no recordings placed under it
        var runner = new FakeFormulaRunner { ValidatedFormula = RunnableFormula(formulaId) };
        var queue = new FakeJobQueue();
        var controller = Build(db, userId, runner, queue);

        var result = await controller.Run(section.Id, formulaId, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("This folder has no meetings to run a formula over.", badRequest.Value);
        Assert.Empty(db.SectionFormulaResults);
        Assert.Empty(queue.FormulaRunJobs);
    }

    // ---- Results CRUD (list/get/update/delete/email/download) ----
    // View = section member; edit/delete = the result's creator OR a member with room ManageContents.
    // Mirrors the recording FormulaResultsController; a non-permitted edit returns 403 like that controller.

    // A folder inside a shared room the given user belongs to (with the given permission).
    private static async Task<Section> SharedRoomSection(
        DiarizDbContext db, Guid memberId, RoomPermission perm = RoomPermission.ManageContents)
    {
        Users.Ensure(db, memberId);
        var scope = new RoomScope(db);
        var roomId = await scope.CreateSharedRoomAsync("Eng", null, null, null);
        await scope.SetMemberAsync(roomId, RoomPrincipalType.User, memberId, perm);
        var s = new Section { Id = Guid.NewGuid(), UserId = memberId, RoomId = roomId, Name = "Shared F" };
        db.Sections.Add(s);
        await db.SaveChangesAsync();
        return s;
    }

    private static async Task<SectionFormulaResult> SeedResult(
        DiarizDbContext db, Guid sectionId, Guid creatorId, string name = "Key Decisions",
        string text = "# Key Decisions\n\n- Ship it", int ordinal = 0)
    {
        Users.Ensure(db, creatorId);
        var r = new SectionFormulaResult
        {
            Id = Guid.NewGuid(), SectionId = sectionId, CreatedByUserId = creatorId,
            Name = name, Text = text, Ordinal = ordinal, Status = FormulaRunStatus.Ready,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.SectionFormulaResults.Add(r);
        await db.SaveChangesAsync();
        return r;
    }

    [Fact]
    public async Task List_ForMember_ReturnsResults_OrderedWithStatusAndOrigin()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var section = await Section(db, userId);
        await SeedResult(db, section.Id, userId, "Second", ordinal: 1);
        await SeedResult(db, section.Id, userId, "First", ordinal: 0);

        var result = await Build(db, userId).List(section.Id);

        var list = Assert.IsAssignableFrom<IReadOnlyList<SectionFormulaResultDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(2, list.Count);
        Assert.Equal("First", list[0].Name);
        Assert.Equal("Second", list[1].Name);
        Assert.Equal("Ready", list[0].Status);
        Assert.Null(list[0].Error);
        Assert.NotNull(list[0].Origin);
        Assert.Equal(section.Id, list[0].SectionId);
    }

    [Fact]
    public async Task List_ForNonMember_Returns404()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var section = await Section(db, owner);
        await SeedResult(db, section.Id, owner);
        var outsider = Guid.NewGuid();
        Users.Ensure(db, outsider);

        var result = await Build(db, outsider).List(section.Id);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Get_ReturnsMarkdownText_ForMember()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var section = await Section(db, userId);
        var r = await SeedResult(db, section.Id, userId, text: "# Decisions\n\n- One");

        var result = await Build(db, userId).Get(section.Id, r.Id);

        var dto = Assert.IsType<FormulaResultTextDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("# Decisions\n\n- One", dto.Text);
    }

    [Fact]
    public async Task Get_ForNonMember_Returns404()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var section = await Section(db, owner);
        var r = await SeedResult(db, section.Id, owner);
        var outsider = Guid.NewGuid();
        Users.Ensure(db, outsider);

        var result = await Build(db, outsider).Get(section.Id, r.Id);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Update_ByCreator_ChangesTextAndUpdatedAt()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var section = await Section(db, userId);
        var r = await SeedResult(db, section.Id, userId, text: "Old text");
        r.UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1);
        await db.SaveChangesAsync();
        var before = r.UpdatedAt;

        var result = await Build(db, userId).Update(section.Id, r.Id, new UpdateFormulaResultRequest("New text"));

        var dto = Assert.IsType<SectionFormulaResultDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(r.Id, dto.Id);
        var updated = await db.SectionFormulaResults.FindAsync(r.Id);
        Assert.Equal("New text", updated!.Text);
        Assert.True(updated.UpdatedAt > before);
    }

    [Fact]
    public async Task Update_ByMemberWithManageContents_WhenCreatorIsSomeoneElse_Ok()
    {
        using var db = TestDb.Create();
        var manager = Guid.NewGuid();
        var creator = Guid.NewGuid();
        var section = await SharedRoomSection(db, manager, RoomPermission.ManageContents);
        var r = await SeedResult(db, section.Id, creator);

        var result = await Build(db, manager).Update(section.Id, r.Id, new UpdateFormulaResultRequest("Edited by manager"));

        var dto = Assert.IsType<SectionFormulaResultDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(r.Id, dto.Id);
        Assert.Equal("Edited by manager", (await db.SectionFormulaResults.FindAsync(r.Id))!.Text);
    }

    [Fact]
    public async Task Update_ByMemberWithoutManageContents_WhoIsNotCreator_Returns403()
    {
        using var db = TestDb.Create();
        var member = Guid.NewGuid();
        var creator = Guid.NewGuid();
        // A member who can view but not manage contents, and who did not create the result.
        var section = await SharedRoomSection(db, member, RoomPermission.CreateRecording);
        var r = await SeedResult(db, section.Id, creator);

        var result = await Build(db, member).Update(section.Id, r.Id, new UpdateFormulaResultRequest("Hacked"));

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, status.StatusCode);
        Assert.Equal(r.Text, (await db.SectionFormulaResults.FindAsync(r.Id))!.Text);
    }

    [Fact]
    public async Task Update_ByNonMember_Returns404()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var section = await Section(db, owner);
        var r = await SeedResult(db, section.Id, owner);
        var outsider = Guid.NewGuid();
        Users.Ensure(db, outsider);

        var result = await Build(db, outsider).Update(section.Id, r.Id, new UpdateFormulaResultRequest("Hacked"));

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.Equal(r.Text, (await db.SectionFormulaResults.FindAsync(r.Id))!.Text);
    }

    [Fact]
    public async Task Delete_ByCreator_Returns204()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var section = await Section(db, userId);
        var r = await SeedResult(db, section.Id, userId);

        var result = await Build(db, userId).Delete(section.Id, r.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.False(db.SectionFormulaResults.Any(x => x.Id == r.Id));
    }

    [Fact]
    public async Task Delete_ByMemberWithManageContents_WhenCreatorIsSomeoneElse_Returns204()
    {
        using var db = TestDb.Create();
        var manager = Guid.NewGuid();
        var creator = Guid.NewGuid();
        var section = await SharedRoomSection(db, manager, RoomPermission.ManageContents);
        var r = await SeedResult(db, section.Id, creator);

        var result = await Build(db, manager).Delete(section.Id, r.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.False(db.SectionFormulaResults.Any(x => x.Id == r.Id));
    }

    [Fact]
    public async Task Delete_ByMemberWithoutManageContents_WhoIsNotCreator_Returns403()
    {
        using var db = TestDb.Create();
        var member = Guid.NewGuid();
        var creator = Guid.NewGuid();
        var section = await SharedRoomSection(db, member, RoomPermission.CreateRecording);
        var r = await SeedResult(db, section.Id, creator);

        var result = await Build(db, member).Delete(section.Id, r.Id);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, status.StatusCode);
        Assert.True(db.SectionFormulaResults.Any(x => x.Id == r.Id));
    }

    [Fact]
    public async Task Email_SendsToSignedInUsersOwnAddress()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var section = await Section(db, userId);
        var r = await SeedResult(db, section.Id, userId, "Key Decisions", "# Key Decisions\n\n- Ship it");
        var email = new FakeEmailSender { Sent = true };

        var result = await Build(db, userId, email: email).Email(section.Id, r.Id);

        Assert.IsType<OkResult>(result);
        var msg = Assert.Single(email.Messages);
        Assert.Equal($"{userId:N}@x.test", msg.To);
        Assert.Contains("Key Decisions", msg.Subject);
        Assert.Contains("Ship it", msg.Body);
    }

    [Fact]
    public async Task Email_ForNonMember_Returns404()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var section = await Section(db, owner);
        var r = await SeedResult(db, section.Id, owner);
        var outsider = Guid.NewGuid();
        Users.Ensure(db, outsider);
        var email = new FakeEmailSender();

        var result = await Build(db, outsider, email: email).Email(section.Id, r.Id);

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(email.Messages);
    }

    [Fact]
    public async Task Download_ReturnsMarkdown_WithSluggedFilename()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var section = await Section(db, userId);
        var r = await SeedResult(db, section.Id, userId, "Key Decisions", "# Key Decisions\n\n- Ship it");

        var result = await Build(db, userId).Download(section.Id, r.Id);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("text/markdown", file.ContentType);
        Assert.Equal("key-decisions.md", file.FileDownloadName);
        Assert.Equal("# Key Decisions\n\n- Ship it", System.Text.Encoding.UTF8.GetString(file.FileContents));
    }

    [Fact]
    public async Task ResultFromADifferentSection_Returns404()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var section = await Section(db, userId);
        var other = new Section { Id = Guid.NewGuid(), UserId = userId, RoomId = section.RoomId, Name = "Other" };
        db.Sections.Add(other);
        await db.SaveChangesAsync();
        var r = await SeedResult(db, other.Id, userId); // belongs to a different folder

        var result = await Build(db, userId).Get(section.Id, r.Id);

        Assert.IsType<NotFoundResult>(result.Result);
    }
}
