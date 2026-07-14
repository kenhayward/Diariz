using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

/// <summary>Create/update/delete of meeting types and the Platform-vs-Personal authorization matrix: a Platform
/// type (shared) needs a Platform Administrator; a Personal type needs ownership; normal users always get their
/// own Personal type regardless of the requested scope.
///
/// A type carries no template of its own - it points at the <b>formula</b> that generates its minutes - so these
/// also cover the rule that keeps that pointer usable (see <c>ValidateFormulasAsync</c>).</summary>
public class MeetingTypesCrudTests
{
    // Platform authority is group membership in the database now, not a role claim on the principal.
    private static MeetingTypesController Build(DiarizDbContext db, Guid userId, bool admin = false)
    {
        if (admin) Perms.Grant(db, userId, PlatformPermission.ManagePlatform);
        Users.Ensure(db, userId); // create paths mint the owner's personal room, which needs a real user row
        return new(db, new UserPermissions(db), new Diariz.Api.Services.RoomScope(db)) { ControllerContext = Http.Context(userId) };
    }

    /// <summary>A formula the type can point at. Defaults to a Platform one, which any type may reference.</summary>
    private static Formula SeedFormula(
        DiarizDbContext db, FormulaScope scope = FormulaScope.Platform, Guid? owner = null,
        bool enabled = true, string name = "F")
    {
        var f = new Formula
        {
            Id = Guid.NewGuid(),
            Scope = scope,
            OwnerUserId = scope == FormulaScope.Personal ? owner : null,
            Name = name,
            ContentJson = TemplateContent.FromPrompt("Summarise.").Serialize(),
            Context = FormulaContext.Transcript,
            Enabled = enabled,
        };
        db.Formulas.Add(f);
        db.SaveChanges();
        return f;
    }

    private static MeetingTypeRequest Req(
        Guid? primary = null, bool isPlatform = false, string icon = "document", string color = "#5C6BC0",
        IReadOnlyList<Guid>? additional = null) =>
        new("Standard", "My type", "Some context", icon, color, primary, additional ?? [], isPlatform);

    private static T Ok<T>(ActionResult<T> r) => (T)((ObjectResult)r.Result!).Value!;
    private static int Status(ActionResult<MeetingTypeDto> r) => ((ObjectResult)r.Result!).StatusCode!.Value;
    private static string Message(ActionResult<MeetingTypeDto> r) => (string)((ObjectResult)r.Result!).Value!;

    // ---- create ----

    [Fact]
    public async Task Create_by_normal_user_makes_a_personal_type_they_own()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var f = SeedFormula(db);

        var dto = Ok(await Build(db, me).Create(Req(primary: f.Id)));

        Assert.False(dto.IsPlatform);
        Assert.True(dto.CanEdit);
        Assert.Equal(f.Id, dto.PrimaryFormulaId);
        var row = await db.MeetingTypes.SingleAsync();
        Assert.Equal(me, row.UserId);
        Assert.Equal(f.Id, row.PrimaryFormulaId);
    }

    [Fact]
    public async Task Create_stores_the_additional_formulas_in_order()
    {
        using var db = TestDb.Create();
        var a = SeedFormula(db, name: "A");
        var b = SeedFormula(db, name: "B");

        var dto = Ok(await Build(db, Guid.NewGuid()).Create(Req(primary: a.Id, additional: [b.Id, a.Id])));

        Assert.Equal([b.Id, a.Id], dto.AdditionalFormulaIds);
    }

    [Fact]
    public async Task Create_with_IsPlatform_by_normal_user_is_forced_personal()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();

        var dto = Ok(await Build(db, me).Create(Req(primary: SeedFormula(db).Id, isPlatform: true)));

        Assert.False(dto.IsPlatform);
        Assert.Equal(me, (await db.MeetingTypes.SingleAsync()).UserId);
    }

    [Fact]
    public async Task Create_with_IsPlatform_by_admin_makes_a_shared_platform_type()
    {
        using var db = TestDb.Create();

        var dto = Ok(await Build(db, Guid.NewGuid(), admin: true)
            .Create(Req(primary: SeedFormula(db).Id, isPlatform: true)));

        Assert.True(dto.IsPlatform);
        Assert.Null((await db.MeetingTypes.SingleAsync()).UserId);
    }

    [Theory]
    [InlineData("", "My type", "document", "#5C6BC0")]     // blank group
    [InlineData("Standard", "", "document", "#5C6BC0")]    // blank title
    [InlineData("Standard", "My type", "skull", "#5C6BC0")] // icon not in the set
    [InlineData("Standard", "My type", "document", "blue")] // colour not a hex
    public async Task Create_rejects_invalid_input(string group, string title, string icon, string color)
    {
        using var db = TestDb.Create();
        var req = new MeetingTypeRequest(group, title, "", icon, color, SeedFormula(db).Id, []);
        Assert.IsType<BadRequestObjectResult>((await Build(db, Guid.NewGuid()).Create(req)).Result);
    }

    [Fact]
    public async Task Create_rejects_a_formula_that_does_not_exist()
    {
        using var db = TestDb.Create();
        var result = await Build(db, Guid.NewGuid()).Create(Req(primary: Guid.NewGuid()));
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // ---- the scope rule ----
    //
    // Minutes generate as the RECORDING OWNER, and a Personal formula may only be run by its owner. So a Platform
    // type pointing at someone's Personal formula would produce no minutes for every other user on the instance.
    // It is refused at save, not left to fail silently at generation time for people who never touched it.

    [Fact]
    public async Task A_platform_type_cannot_point_at_a_personal_formula()
    {
        using var db = TestDb.Create();
        var admin = Guid.NewGuid();
        var mine = SeedFormula(db, FormulaScope.Personal, owner: admin, name: "My private formula");

        var result = await Build(db, admin, admin: true).Create(Req(primary: mine.Id, isPlatform: true));

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("personal formula", Message(result));
    }

    [Fact]
    public async Task A_platform_type_cannot_point_at_a_disabled_formula()
    {
        using var db = TestDb.Create();
        var disabled = SeedFormula(db, enabled: false, name: "Off");

        var result = await Build(db, Guid.NewGuid(), admin: true).Create(Req(primary: disabled.Id, isPlatform: true));

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("disabled", Message(result));
    }

    [Fact]
    public async Task A_platform_type_cannot_smuggle_a_personal_formula_in_as_an_additional_one()
    {
        using var db = TestDb.Create();
        var admin = Guid.NewGuid();
        var shared = SeedFormula(db, name: "Shared");
        var mine = SeedFormula(db, FormulaScope.Personal, owner: admin, name: "Mine");

        var result = await Build(db, admin, admin: true)
            .Create(Req(primary: shared.Id, isPlatform: true, additional: [mine.Id]));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task A_personal_type_may_point_at_its_owners_personal_formula()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var mine = SeedFormula(db, FormulaScope.Personal, owner: me);

        var dto = Ok(await Build(db, me).Create(Req(primary: mine.Id)));

        Assert.Equal(mine.Id, dto.PrimaryFormulaId);
    }

    [Fact]
    public async Task A_personal_type_cannot_point_at_someone_elses_personal_formula()
    {
        using var db = TestDb.Create();
        var theirs = SeedFormula(db, FormulaScope.Personal, owner: Guid.NewGuid());

        var result = await Build(db, Guid.NewGuid()).Create(Req(primary: theirs.Id));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // ---- update ----

    private static async Task<Guid> SeedType(DiarizDbContext db, Guid? owner)
    {
        // A personal type lives in its owner's personal room (now the scope); a platform type has RoomId null.
        Guid? roomId = null;
        if (owner is { } o)
        {
            Users.Ensure(db, o);
            roomId = await new Diariz.Api.Services.RoomScope(db).PersonalRoomIdAsync(o);
        }
        var t = MeetingTypes.With(db, TemplateContent.Empty, title: "T", userId: owner, roomId: roomId);
        await db.SaveChangesAsync();
        return t.Id;
    }

    [Fact]
    public async Task Update_own_personal_type_repoints_its_formula()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var id = await SeedType(db, me);
        var other = SeedFormula(db, name: "Another");

        var dto = Ok(await Build(db, me).Update(id, Req(primary: other.Id)));

        Assert.Equal("My type", dto.Title);
        Assert.Equal(other.Id, (await db.MeetingTypes.SingleAsync(t => t.Id == id)).PrimaryFormulaId);
    }

    // Update ignores the payload's IsPlatform (the scope is fixed at create), so the type's OWN scope must decide
    // which formulas it may reference - not what the caller claims.
    [Fact]
    public async Task Updating_a_platform_type_to_a_personal_formula_is_refused()
    {
        using var db = TestDb.Create();
        var admin = Guid.NewGuid();
        var id = await SeedType(db, owner: null);
        var mine = SeedFormula(db, FormulaScope.Personal, owner: admin, name: "Mine");

        var result = await Build(db, admin, admin: true).Update(id, Req(primary: mine.Id, isPlatform: false));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Update_platform_type_by_normal_user_is_forbidden()
    {
        using var db = TestDb.Create();
        var id = await SeedType(db, owner: null);
        Assert.Equal(403, Status(await Build(db, Guid.NewGuid()).Update(id, Req(primary: SeedFormula(db).Id))));
    }

    [Fact]
    public async Task Update_platform_type_by_admin_succeeds()
    {
        using var db = TestDb.Create();
        var id = await SeedType(db, owner: null);
        var dto = Ok(await Build(db, Guid.NewGuid(), admin: true).Update(id, Req(primary: SeedFormula(db).Id)));
        Assert.True(dto.IsPlatform); // stays a platform type
    }

    [Fact]
    public async Task Update_other_users_personal_type_is_not_found()
    {
        using var db = TestDb.Create();
        var id = await SeedType(db, owner: Guid.NewGuid());
        Assert.IsType<NotFoundResult>((await Build(db, Guid.NewGuid()).Update(id, Req(primary: SeedFormula(db).Id))).Result);
    }

    // ---- delete ----

    [Fact]
    public async Task Delete_own_personal_type_succeeds()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var id = await SeedType(db, me);
        Assert.IsType<NoContentResult>(await Build(db, me).Delete(id));
        Assert.Equal(0, await db.MeetingTypes.CountAsync());
    }

    [Fact]
    public async Task Delete_platform_type_by_normal_user_is_forbidden()
    {
        using var db = TestDb.Create();
        var id = await SeedType(db, owner: null);
        var result = await Build(db, Guid.NewGuid()).Delete(id);
        Assert.Equal(403, ((ObjectResult)result).StatusCode);
        Assert.Equal(1, await db.MeetingTypes.CountAsync());
    }

    [Fact]
    public async Task Delete_platform_type_by_admin_succeeds()
    {
        using var db = TestDb.Create();
        var id = await SeedType(db, owner: null);
        Assert.IsType<NoContentResult>(await Build(db, Guid.NewGuid(), admin: true).Delete(id));
    }
}
