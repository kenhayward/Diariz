using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

public class MeetingTypesControllerTests
{
    // Platform authority is group membership in the database now, not a role claim on the principal.
    private static MeetingTypesController Build(DiarizDbContext db, Guid userId, bool platformAdmin = false)
    {
        if (platformAdmin) Perms.Grant(db, userId, PlatformPermission.ManagePlatform);
        return new(db, new UserPermissions(db)) { ControllerContext = Http.Context(userId) };
    }

    private static MeetingType Platform(string key) =>
        new() { Id = Guid.NewGuid(), Key = key, UserId = null, GroupName = "Standard", Title = key,
                ContentJson = new MeetingTypeContent([]).Serialize() };

    private static MeetingType Personal(Guid owner, string title) =>
        new() { Id = Guid.NewGuid(), UserId = owner, GroupName = "Mine", Title = title,
                ContentJson = new MeetingTypeContent([]).Serialize() };

    [Fact]
    public async Task List_returns_platform_types_and_own_but_not_other_users()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        db.MeetingTypes.AddRange(Platform("general"), Personal(me, "My type"), Personal(other, "Their type"));
        await db.SaveChangesAsync();

        var list = await Build(db, me).List();

        Assert.Equal(2, list.Count);
        Assert.Contains(list, t => t.Title == "general" && t.IsPlatform);
        Assert.Contains(list, t => t.Title == "My type" && !t.IsPlatform);
        Assert.DoesNotContain(list, t => t.Title == "Their type");
    }

    [Fact]
    public async Task List_marks_canEdit_for_own_personal_types_only_when_not_admin()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        db.MeetingTypes.AddRange(Platform("general"), Personal(me, "Mine"));
        await db.SaveChangesAsync();

        var list = await Build(db, me).List();

        Assert.False(list.Single(t => t.IsPlatform).CanEdit);       // normal user can't edit platform types
        Assert.True(list.Single(t => !t.IsPlatform).CanEdit);       // but can edit their own
    }

    [Fact]
    public async Task List_lets_a_platform_admin_edit_platform_types()
    {
        using var db = TestDb.Create();
        var admin = Guid.NewGuid();
        db.MeetingTypes.Add(Platform("general"));
        await db.SaveChangesAsync();

        var list = await Build(db, admin, platformAdmin: true).List();

        Assert.True(list.Single().CanEdit);
    }
}
