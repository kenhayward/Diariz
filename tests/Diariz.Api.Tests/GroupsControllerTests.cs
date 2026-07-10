using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;

namespace Diariz.Api.Tests;

/// <summary>Group administration. The guards that matter: a system group cannot be deleted, its name and flags
/// cannot be edited, and its last member cannot be removed - otherwise a deployment could be left with nobody
/// able to administer it.</summary>
public class GroupsControllerTests
{
    private static GroupsController Sut(DiarizDbContext db, Guid? callerId = null) =>
        new(db) { ControllerContext = Http.Context(callerId ?? Guid.NewGuid()) };

    private static async Task<UserGroup> SeedSystemGroup(DiarizDbContext db, params Guid[] members)
    {
        var group = new UserGroup
        {
            Id = Guid.NewGuid(),
            Name = Seeder.PlatformAdminsGroup,
            IsSystem = true,
            Permissions = PlatformPermission.ManagePlatform,
        };
        db.UserGroups.Add(group);
        foreach (var m in members) db.UserGroupMembers.Add(new UserGroupMember { GroupId = group.Id, UserId = m });
        await db.SaveChangesAsync();
        return group;
    }

    private static int StatusOf(IActionResult result) => ((ObjectResult)result).StatusCode!.Value;

    [Fact]
    public async Task Create_ThenList_ReturnsTheGroup()
    {
        using var db = TestDb.Create();
        var sut = Sut(db);

        await sut.Create(new GroupInput("Engineering", "The eng team", "users", "#5C6BC0", PlatformPermission.ManageRooms));

        var groups = await sut.List();
        Assert.Contains(groups, g => g.Name == "Engineering" && g.Permissions == (int)PlatformPermission.ManageRooms);
    }

    [Fact]
    public async Task Create_DuplicateName_IsRejected()
    {
        using var db = TestDb.Create();
        var sut = Sut(db);
        await sut.Create(new GroupInput("Engineering", null, null, null, PlatformPermission.None));

        var result = await sut.Create(new GroupInput("Engineering", null, null, null, PlatformPermission.None));

        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task Delete_SystemGroup_IsForbidden()
    {
        using var db = TestDb.Create();
        var group = await SeedSystemGroup(db, Guid.NewGuid());

        var result = await Sut(db).Delete(group.Id);

        Assert.Equal(403, StatusOf(result));
        Assert.NotNull(await db.UserGroups.FindAsync(group.Id));
    }

    [Fact]
    public async Task Delete_OrdinaryGroup_RemovesIt()
    {
        using var db = TestDb.Create();
        var group = new UserGroup { Id = Guid.NewGuid(), Name = "Engineering" };
        db.UserGroups.Add(group);
        await db.SaveChangesAsync();

        var result = await Sut(db).Delete(group.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.Null(await db.UserGroups.FindAsync(group.Id));
    }

    [Fact]
    public async Task RemoveLastMember_OfSystemGroup_IsForbidden()
    {
        using var db = TestDb.Create();
        var onlyAdmin = Guid.NewGuid();
        var group = await SeedSystemGroup(db, onlyAdmin);

        var result = await Sut(db, onlyAdmin).RemoveMember(group.Id, onlyAdmin);

        Assert.Equal(403, StatusOf(result));
        Assert.Single(db.UserGroupMembers.Where(m => m.GroupId == group.Id));
    }

    [Fact]
    public async Task RemoveMember_OfSystemGroup_IsAllowed_WhenOthersRemain()
    {
        using var db = TestDb.Create();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var group = await SeedSystemGroup(db, a, b);

        var result = await Sut(db, a).RemoveMember(group.Id, b);

        Assert.IsType<NoContentResult>(result);
        Assert.Single(db.UserGroupMembers.Where(m => m.GroupId == group.Id));
    }

    [Fact]
    public async Task RemoveLastMember_OfOrdinaryGroup_IsAllowed()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var group = new UserGroup { Id = Guid.NewGuid(), Name = "Engineering" };
        db.UserGroups.Add(group);
        db.UserGroupMembers.Add(new UserGroupMember { GroupId = group.Id, UserId = userId });
        await db.SaveChangesAsync();

        var result = await Sut(db).RemoveMember(group.Id, userId);

        Assert.IsType<NoContentResult>(result);
        Assert.Empty(db.UserGroupMembers.Where(m => m.GroupId == group.Id));
    }

    [Fact]
    public async Task AddMember_IsIdempotent()
    {
        using var db = TestDb.Create();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = "a@b.test", Email = "a@b.test" };
        db.Users.Add(user);
        var group = new UserGroup { Id = Guid.NewGuid(), Name = "Engineering" };
        db.UserGroups.Add(group);
        await db.SaveChangesAsync();

        await Sut(db).AddMember(group.Id, user.Id);
        await Sut(db).AddMember(group.Id, user.Id);

        Assert.Single(db.UserGroupMembers.Where(m => m.GroupId == group.Id && m.UserId == user.Id));
    }

    /// <summary>A system group's flags are the seeder's to own. Letting an admin clear ManagePlatform from
    /// Platform Administrators would lock the whole deployment out of maintenance.</summary>
    [Fact]
    public async Task Update_SystemGroup_CannotChangeNameOrPermissions()
    {
        using var db = TestDb.Create();
        var group = await SeedSystemGroup(db, Guid.NewGuid());

        var result = await Sut(db).Update(group.Id,
            new GroupInput("Renamed", "new description", "star", "#ff0000", PlatformPermission.None));

        Assert.IsType<NoContentResult>(result);
        var loaded = await db.UserGroups.FindAsync(group.Id);
        Assert.Equal(Seeder.PlatformAdminsGroup, loaded!.Name);
        Assert.Equal(PlatformPermission.ManagePlatform, loaded.Permissions);
        Assert.Equal("new description", loaded.Description); // cosmetics are still editable
    }

    [Fact]
    public async Task Update_OrdinaryGroup_ChangesNameAndPermissions()
    {
        using var db = TestDb.Create();
        var group = new UserGroup { Id = Guid.NewGuid(), Name = "Engineering" };
        db.UserGroups.Add(group);
        await db.SaveChangesAsync();

        await Sut(db).Update(group.Id, new GroupInput("Platform Team", null, null, null, PlatformPermission.ManageRooms));

        var loaded = await db.UserGroups.FindAsync(group.Id);
        Assert.Equal("Platform Team", loaded!.Name);
        Assert.Equal(PlatformPermission.ManageRooms, loaded.Permissions);
    }

    [Fact]
    public async Task Delete_UnknownGroup_IsNotFound()
    {
        using var db = TestDb.Create();
        Assert.IsType<NotFoundResult>(await Sut(db).Delete(Guid.NewGuid()));
    }

    /// <summary>An unknown user id would otherwise violate the FK and surface as a 500 on Postgres.</summary>
    [Fact]
    public async Task AddMember_UnknownUser_IsNotFound()
    {
        using var db = TestDb.Create();
        var group = new UserGroup { Id = Guid.NewGuid(), Name = "Engineering" };
        db.UserGroups.Add(group);
        await db.SaveChangesAsync();

        var result = await Sut(db).AddMember(group.Id, Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(db.UserGroupMembers.Where(m => m.GroupId == group.Id));
    }

    [Fact]
    public async Task AddMember_KnownUser_Succeeds()
    {
        using var db = TestDb.Create();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = "a@b.test", Email = "a@b.test" };
        db.Users.Add(user);
        var group = new UserGroup { Id = Guid.NewGuid(), Name = "Engineering" };
        db.UserGroups.Add(group);
        await db.SaveChangesAsync();

        Assert.IsType<NoContentResult>(await Sut(db).AddMember(group.Id, user.Id));
        Assert.Single(db.UserGroupMembers.Where(m => m.GroupId == group.Id));
    }
}
