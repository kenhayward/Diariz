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
        IFormulaRunner? runner = null, FakeJobQueue? queue = null, FakeHubContext? hub = null) =>
        new(db, runner ?? new FakeFormulaRunner(), queue ?? new FakeJobQueue(), hub ?? new FakeHubContext(),
            new RoomScope(db))
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
        Name = name, Prompt = "P", Context = FormulaContext.Transcript, Enabled = true,
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
}
