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
/// own Personal type regardless of the requested scope.</summary>
public class MeetingTypesCrudTests
{
    // Platform authority is group membership in the database now, not a role claim on the principal.
    private static MeetingTypesController Build(DiarizDbContext db, Guid userId, bool admin = false)
    {
        if (admin) Perms.Grant(db, userId, PlatformPermission.ManagePlatform);
        Users.Ensure(db, userId); // create paths mint the owner's personal room, which needs a real user row
        return new(db, new UserPermissions(db), new Diariz.Api.Services.RoomScope(db)) { ControllerContext = Http.Context(userId) };
    }

    private static TemplateContent OneSection() =>
        new([new TemplateSection(1, "Summary", [new TemplateBlock(TemplateBlock.Prompt, Text: "Summarise.")])]);

    private static MeetingTypeRequest Req(bool isPlatform = false, string icon = "document", string color = "#5C6BC0") =>
        new("Standard", "My type", "Some context", icon, color, OneSection(), isPlatform);

    private static T Ok<T>(ActionResult<T> r) => (T)((ObjectResult)r.Result!).Value!;
    private static int Status(ActionResult<MeetingTypeDto> r) => ((ObjectResult)r.Result!).StatusCode!.Value;

    // ---- create ----

    [Fact]
    public async Task Create_by_normal_user_makes_a_personal_type_they_own()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();

        var dto = Ok(await Build(db, me).Create(Req()));

        Assert.False(dto.IsPlatform);
        Assert.True(dto.CanEdit);
        var row = await db.MeetingTypes.SingleAsync();
        Assert.Equal(me, row.UserId);
        Assert.Equal("Summarise.", TemplateContent.Parse(row.ContentJson).Sections[0].Blocks[0].Text);
    }

    [Fact]
    public async Task Create_with_IsPlatform_by_normal_user_is_forced_personal()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();

        var dto = Ok(await Build(db, me).Create(Req(isPlatform: true)));

        Assert.False(dto.IsPlatform);
        Assert.Equal(me, (await db.MeetingTypes.SingleAsync()).UserId);
    }

    [Fact]
    public async Task Create_with_IsPlatform_by_admin_makes_a_shared_platform_type()
    {
        using var db = TestDb.Create();

        var dto = Ok(await Build(db, Guid.NewGuid(), admin: true).Create(Req(isPlatform: true)));

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
        var req = new MeetingTypeRequest(group, title, "", icon, color, OneSection());
        Assert.IsType<BadRequestObjectResult>((await Build(db, Guid.NewGuid()).Create(req)).Result);
    }

    [Fact]
    public async Task Create_rejects_malformed_content()
    {
        using var db = TestDb.Create();
        var bad = new TemplateContent([new TemplateSection(4, "Bad", [])]); // level 4 (H1-H3 only)
        var req = new MeetingTypeRequest("Standard", "My type", "", "document", "#5C6BC0", bad);
        Assert.IsType<BadRequestObjectResult>((await Build(db, Guid.NewGuid()).Create(req)).Result);
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
        var t = new MeetingType
        {
            Id = Guid.NewGuid(), UserId = owner, RoomId = roomId, GroupName = "G", Title = "T", Icon = "document",
            Color = "#5C6BC0", ContentJson = new TemplateContent([]).Serialize(),
        };
        db.MeetingTypes.Add(t);
        await db.SaveChangesAsync();
        return t.Id;
    }

    [Fact]
    public async Task Update_own_personal_type_saves_atomically()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var id = await SeedType(db, me);

        var dto = Ok(await Build(db, me).Update(id, Req()));

        Assert.Equal("My type", dto.Title);
        var saved = TemplateContent.Parse((await db.MeetingTypes.SingleAsync()).ContentJson);
        Assert.Equal("Summarise.", saved.Sections[0].Blocks[0].Text); // whole content replaced atomically
    }

    [Fact]
    public async Task Update_platform_type_by_normal_user_is_forbidden()
    {
        using var db = TestDb.Create();
        var id = await SeedType(db, owner: null);
        Assert.Equal(403, Status(await Build(db, Guid.NewGuid()).Update(id, Req())));
    }

    [Fact]
    public async Task Update_platform_type_by_admin_succeeds()
    {
        using var db = TestDb.Create();
        var id = await SeedType(db, owner: null);
        var dto = Ok(await Build(db, Guid.NewGuid(), admin: true).Update(id, Req()));
        Assert.True(dto.IsPlatform); // stays a platform type
    }

    [Fact]
    public async Task Update_other_users_personal_type_is_not_found()
    {
        using var db = TestDb.Create();
        var id = await SeedType(db, owner: Guid.NewGuid());
        Assert.IsType<NotFoundResult>((await Build(db, Guid.NewGuid()).Update(id, Req())).Result);
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
