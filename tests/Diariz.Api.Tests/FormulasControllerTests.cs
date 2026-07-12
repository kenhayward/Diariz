using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;

namespace Diariz.Api.Tests;

/// <summary>CRUD + run for Formulas. Platform authority is group membership read from the database
/// (<see cref="Perms.Grant"/>), matching the convention in <c>MeetingTypesControllerTests</c> - the real
/// <see cref="UserPermissions"/> service runs against the in-memory provider, so no fake is needed for it.</summary>
public class FormulasControllerTests
{
    private static FormulasController Build(DiarizDbContext db, Guid userId, IFormulaRunner? runner = null) =>
        new(db, new UserPermissions(db), runner ?? new FakeFormulaRunner()) { ControllerContext = Http.Context(userId) };

    private static Formula Personal(Guid owner, string name = "Mine") => new()
    {
        Id = Guid.NewGuid(), Scope = FormulaScope.Personal, OwnerUserId = owner,
        Name = name, Prompt = "Do a thing.", Context = FormulaContext.Transcript, Enabled = true,
    };

    private static Formula Platform(string name = "Shared", bool enabled = true) => new()
    {
        Id = Guid.NewGuid(), Scope = FormulaScope.Platform, OwnerUserId = null,
        Name = name, Prompt = "Do a thing.", Context = FormulaContext.Transcript, Enabled = enabled,
    };

    private static Formula Diariz(string name = "Built-in", bool isBuiltIn = true) => new()
    {
        Id = Guid.NewGuid(), Scope = FormulaScope.Diariz, OwnerUserId = null,
        Name = name, Prompt = "Do a thing.", Context = FormulaContext.Transcript, Enabled = true, IsBuiltIn = isBuiltIn,
    };

    // ---- Create ----

    [Fact]
    public async Task Create_Personal_ByAnyUser_SetsOwnerAndReturnsDto()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var controller = Build(db, userId);

        var result = await controller.Create(new CreateFormulaRequest(
            "Personal", "My Formula", "desc", "Summarize.", (int)FormulaContext.Transcript));

        var dto = Assert.IsType<FormulaDto>(Assert.IsType<CreatedResult>(result.Result).Value);
        Assert.Equal("Personal", dto.Scope);
        Assert.Equal(userId, dto.OwnerUserId);
        Assert.Equal("My Formula", dto.Name);
        Assert.Equal((int)FormulaContext.Transcript, dto.Context);
        Assert.False(dto.IsBuiltIn);
        Assert.True(dto.Enabled);
    }

    [Fact]
    public async Task Create_Platform_WithoutManageFormulas_Returns403()
    {
        using var db = TestDb.Create();
        var controller = Build(db, Guid.NewGuid());

        var result = await controller.Create(new CreateFormulaRequest(
            "Platform", "Shared Formula", null, "Summarize.", (int)FormulaContext.Transcript));

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, status.StatusCode);
    }

    [Fact]
    public async Task Create_Platform_WithManageFormulas_Ok_OwnerUserIdIsNull()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        Perms.Grant(db, userId, PlatformPermission.ManageFormulas);
        var controller = Build(db, userId);

        var result = await controller.Create(new CreateFormulaRequest(
            "Platform", "Shared Formula", null, "Summarize.", (int)FormulaContext.Transcript));

        var dto = Assert.IsType<FormulaDto>(Assert.IsType<CreatedResult>(result.Result).Value);
        Assert.Equal("Platform", dto.Scope);
        Assert.Null(dto.OwnerUserId);
    }

    [Fact]
    public async Task Create_ContextOutOfRange_Returns400()
    {
        using var db = TestDb.Create();
        var controller = Build(db, Guid.NewGuid());

        // 64 sets a bit above the highest valid FormulaContext flag (Actions = 32; valid mask = 63).
        var result = await controller.Create(new CreateFormulaRequest(
            "Personal", "My Formula", null, "Summarize.", 64));

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("Invalid context.", badRequest.Value);
    }

    // ---- Update ----

    [Fact]
    public async Task Update_PersonalFormulaOwnedBySomeoneElse_Returns404()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var formula = Personal(owner);
        db.Formulas.Add(formula);
        await db.SaveChangesAsync();

        var controller = Build(db, Guid.NewGuid());
        var result = await controller.Update(formula.Id, new UpdateFormulaRequest("New Name", null, null, null));

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Update_PersonalFormulaByOwner_Ok()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var formula = Personal(owner);
        db.Formulas.Add(formula);
        await db.SaveChangesAsync();

        var controller = Build(db, owner);
        var result = await controller.Update(formula.Id, new UpdateFormulaRequest("New Name", null, null, null));

        var dto = Assert.IsType<FormulaDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("New Name", dto.Name);
    }

    [Fact]
    public async Task Update_PlatformFormula_WithoutManageFormulas_Returns403()
    {
        using var db = TestDb.Create();
        var formula = Platform();
        db.Formulas.Add(formula);
        await db.SaveChangesAsync();

        var controller = Build(db, Guid.NewGuid());
        var result = await controller.Update(formula.Id, new UpdateFormulaRequest("New Name", null, null, null));

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, status.StatusCode);
    }

    [Fact]
    public async Task Update_PlatformFormula_WithManageFormulas_Ok()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        Perms.Grant(db, userId, PlatformPermission.ManageFormulas);
        var formula = Platform();
        db.Formulas.Add(formula);
        await db.SaveChangesAsync();

        var controller = Build(db, userId);
        var result = await controller.Update(formula.Id, new UpdateFormulaRequest("New Name", null, null, null));

        var dto = Assert.IsType<FormulaDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("New Name", dto.Name);
    }

    [Fact]
    public async Task Update_ContextOutOfRange_Returns400()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var formula = Personal(owner);
        db.Formulas.Add(formula);
        await db.SaveChangesAsync();

        var controller = Build(db, owner);
        var result = await controller.Update(formula.Id, new UpdateFormulaRequest(null, null, null, 64));

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("Invalid context.", badRequest.Value);
    }

    // ---- Delete ----

    [Fact]
    public async Task Delete_DiarizBuiltIn_Returns400()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        Perms.Grant(db, userId, PlatformPermission.ManageFormulas);
        var formula = Diariz(isBuiltIn: true);
        db.Formulas.Add(formula);
        await db.SaveChangesAsync();

        var controller = Build(db, userId);
        var result = await controller.Delete(formula.Id);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Built-in formulas can't be deleted.", badRequest.Value);
    }

    [Fact]
    public async Task Delete_PersonalByOwner_Returns204()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var formula = Personal(owner);
        db.Formulas.Add(formula);
        await db.SaveChangesAsync();

        var controller = Build(db, owner);
        var result = await controller.Delete(formula.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.False(db.Formulas.Any(f => f.Id == formula.Id));
    }

    [Fact]
    public async Task Delete_PersonalOwnedBySomeoneElse_Returns404()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var formula = Personal(owner);
        db.Formulas.Add(formula);
        await db.SaveChangesAsync();

        var controller = Build(db, Guid.NewGuid());
        var result = await controller.Delete(formula.Id);

        Assert.IsType<NotFoundResult>(result);
        Assert.True(db.Formulas.Any(f => f.Id == formula.Id)); // not deleted, existence not leaked
    }

    [Fact]
    public async Task Delete_PlatformFormula_WithoutManageFormulas_Returns403()
    {
        using var db = TestDb.Create();
        var formula = Platform();
        db.Formulas.Add(formula);
        await db.SaveChangesAsync();

        var controller = Build(db, Guid.NewGuid());
        var result = await controller.Delete(formula.Id);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, status.StatusCode);
    }

    [Fact]
    public async Task Delete_PlatformFormula_WithManageFormulas_Returns204()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        Perms.Grant(db, userId, PlatformPermission.ManageFormulas);
        var formula = Platform();
        db.Formulas.Add(formula);
        await db.SaveChangesAsync();

        var controller = Build(db, userId);
        var result = await controller.Delete(formula.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.False(db.Formulas.Any(f => f.Id == formula.Id));
    }

    // ---- Enable/disable ----

    [Fact]
    public async Task SetEnabled_Personal_Returns400()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var formula = Personal(owner);
        db.Formulas.Add(formula);
        await db.SaveChangesAsync();

        var controller = Build(db, owner);
        var result = await controller.SetEnabled(formula.Id, new SetFormulaEnabledRequest(false));

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Personal formulas are always available.", badRequest.Value);
    }

    [Fact]
    public async Task SetEnabled_PersonalOwnedBySomeoneElse_Returns404_NotBadRequest()
    {
        // The 400 "always available" hint would otherwise let a caller distinguish another user's Personal
        // formula from a non-existent one - a non-owned Personal formula must 404 first, like Update/Delete.
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var formula = Personal(owner);
        db.Formulas.Add(formula);
        await db.SaveChangesAsync();

        var controller = Build(db, Guid.NewGuid());
        var result = await controller.SetEnabled(formula.Id, new SetFormulaEnabledRequest(false));

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task SetEnabled_Platform_WithPermission_Returns204()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        Perms.Grant(db, userId, PlatformPermission.ManageFormulas);
        var formula = Platform();
        db.Formulas.Add(formula);
        await db.SaveChangesAsync();

        var controller = Build(db, userId);
        var result = await controller.SetEnabled(formula.Id, new SetFormulaEnabledRequest(false));

        Assert.IsType<NoContentResult>(result);
        var updated = await db.Formulas.FindAsync(formula.Id);
        Assert.False(updated!.Enabled);
    }

    [Fact]
    public async Task SetEnabled_Platform_WithoutPermission_Returns403()
    {
        using var db = TestDb.Create();
        var formula = Platform();
        db.Formulas.Add(formula);
        await db.SaveChangesAsync();

        var controller = Build(db, Guid.NewGuid());
        var result = await controller.SetEnabled(formula.Id, new SetFormulaEnabledRequest(false));

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, status.StatusCode);
    }

    // ---- List ----

    [Fact]
    public async Task List_ReturnsOwnPersonal_PlusEnabledPlatformAndDiariz_NotOthersOrDisabled()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        var mine = Personal(me, "Mine");
        var theirs = Personal(other, "Theirs");
        var enabledPlatform = Platform("Enabled Platform", enabled: true);
        var disabledPlatform = Platform("Disabled Platform", enabled: false);
        var diarizFormula = Diariz("Built-in");
        db.Formulas.AddRange(mine, theirs, enabledPlatform, disabledPlatform, diarizFormula);
        await db.SaveChangesAsync();

        var controller = Build(db, me);
        var list = await controller.List();

        Assert.Contains(list, f => f.Name == "Mine");
        Assert.Contains(list, f => f.Name == "Enabled Platform");
        Assert.Contains(list, f => f.Name == "Built-in");
        Assert.DoesNotContain(list, f => f.Name == "Theirs");
        Assert.DoesNotContain(list, f => f.Name == "Disabled Platform");
        Assert.Equal(3, list.Count);
    }

    // ---- Run ----

    [Fact]
    public async Task Run_ReturnsFormulaResultDto()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var recordingId = Guid.NewGuid();
        var formulaId = Guid.NewGuid();
        var runner = new FakeFormulaRunner
        {
            Result = new FormulaResult
            {
                Id = Guid.NewGuid(), RecordingId = recordingId, CreatedByUserId = userId,
                FormulaId = formulaId, Name = "Key Decisions", Text = "- Decision one",
            },
        };
        var controller = Build(db, userId, runner);

        var result = await controller.Run(recordingId, formulaId, CancellationToken.None);

        var dto = Assert.IsType<FormulaResultDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(recordingId, dto.RecordingId);
        Assert.Equal("Key Decisions", dto.Name);
        Assert.Equal(userId, dto.CreatedByUserId);
        Assert.Equal((userId, recordingId, formulaId), runner.LastCall);
    }

    [Fact]
    public async Task Run_FormulaAccessException_Returns403()
    {
        using var db = TestDb.Create();
        var runner = new FakeFormulaRunner { ThrowOnCall = new FormulaAccessException("nope") };
        var controller = Build(db, Guid.NewGuid(), runner);

        var result = await controller.Run(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(403, status.StatusCode);
    }

    [Fact]
    public async Task Run_FormulaNotConfiguredException_Returns400()
    {
        using var db = TestDb.Create();
        var runner = new FakeFormulaRunner { ThrowOnCall = new FormulaNotConfiguredException("no endpoint") };
        var controller = Build(db, Guid.NewGuid(), runner);

        var result = await controller.Run(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("Formulas need an AI endpoint. Set one in Settings.", badRequest.Value);
    }

    [Fact]
    public async Task Run_FormulaNotFoundException_Returns404()
    {
        using var db = TestDb.Create();
        var runner = new FakeFormulaRunner { ThrowOnCall = new FormulaNotFoundException("missing") };
        var controller = Build(db, Guid.NewGuid(), runner);

        var result = await controller.Run(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Run_HttpRequestException_Returns502()
    {
        using var db = TestDb.Create();
        var runner = new FakeFormulaRunner { ThrowOnCall = new HttpRequestException("unreachable") };
        var controller = Build(db, Guid.NewGuid(), runner);

        var result = await controller.Run(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(502, status.StatusCode);
    }

    [Fact]
    public async Task Run_TimeoutOperationCanceled_Returns504()
    {
        using var db = TestDb.Create();
        var runner = new FakeFormulaRunner { ThrowOnCall = new OperationCanceledException("timed out") };
        var controller = Build(db, Guid.NewGuid(), runner);

        var result = await controller.Run(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(504, status.StatusCode);
    }

    [Fact]
    public async Task Run_ClientAbort_PropagatesOperationCanceled_NotTurnedInto504()
    {
        // When the request's own token is already cancelled, an OperationCanceledException is a genuine client
        // abort and must propagate (the `when (ct.IsCancellationRequested)` rethrow branch), not become a 504.
        using var db = TestDb.Create();
        var runner = new FakeFormulaRunner { ThrowOnCall = new OperationCanceledException("aborted") };
        var controller = Build(db, Guid.NewGuid(), runner);
        var cancelled = new CancellationToken(canceled: true);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => controller.Run(Guid.NewGuid(), Guid.NewGuid(), cancelled));
    }
}
