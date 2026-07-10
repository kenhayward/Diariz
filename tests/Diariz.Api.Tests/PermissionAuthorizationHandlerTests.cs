using System.Security.Claims;
using Diariz.Api.Auth;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;

namespace Diariz.Api.Tests;

public class PermissionAuthorizationHandlerTests
{
    private static ClaimsPrincipal User(Guid id) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString())], "test"));

    private static async Task<AuthorizationHandlerContext> Evaluate(
        DiarizDbContext db, ClaimsPrincipal user, PlatformPermission required)
    {
        var requirement = new PermissionRequirement(required);
        var ctx = new AuthorizationHandlerContext([requirement], user, resource: null);
        await new PermissionAuthorizationHandler(new UserPermissions(db)).HandleAsync(ctx);
        return ctx;
    }

    private static async Task GrantAsync(DiarizDbContext db, Guid userId, PlatformPermission perms, string name)
    {
        var group = new UserGroup { Id = Guid.NewGuid(), Name = name, Permissions = perms };
        db.UserGroups.Add(group);
        db.UserGroupMembers.Add(new UserGroupMember { GroupId = group.Id, UserId = userId });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Succeeds_WhenUserHoldsTheFlag()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await GrantAsync(db, userId, PlatformPermission.ManageUsers, "A");

        Assert.True((await Evaluate(db, User(userId), PlatformPermission.ManageUsers)).HasSucceeded);
    }

    [Fact]
    public async Task Succeeds_WhenUserHoldsAnyOfSeveralFlags()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await GrantAsync(db, userId, PlatformPermission.ManageUsers, "A");

        var ctx = await Evaluate(db, User(userId), PlatformPermission.ManageUsers | PlatformPermission.ManagePlatform);
        Assert.True(ctx.HasSucceeded);
    }

    [Fact]
    public async Task Fails_WhenUserHoldsNone()
    {
        using var db = TestDb.Create();
        Assert.False((await Evaluate(db, User(Guid.NewGuid()), PlatformPermission.ManagePlatform)).HasSucceeded);
    }

    [Fact]
    public async Task Fails_ForAnonymousCaller()
    {
        using var db = TestDb.Create();
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        Assert.False((await Evaluate(db, anonymous, PlatformPermission.ManageUsers)).HasSucceeded);
    }

    /// <summary>Regression guard on the role-to-group mapping. Seeding ONE group for holders of both the
    /// Administrator and PlatformAdministrator roles would hand every Administrator ManagePlatform, and with
    /// it backup/restore (MaintenanceController) - authority today's role check denies them.</summary>
    [Fact]
    public async Task Administrator_CannotReachManagePlatform()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await GrantAsync(db, userId, PlatformPermission.ManageRooms | PlatformPermission.ManageUsers, "Administrators");

        Assert.True((await Evaluate(db, User(userId), PlatformPermission.ManageUsers)).HasSucceeded);
        Assert.False((await Evaluate(db, User(userId), PlatformPermission.ManagePlatform)).HasSucceeded);
    }
}
