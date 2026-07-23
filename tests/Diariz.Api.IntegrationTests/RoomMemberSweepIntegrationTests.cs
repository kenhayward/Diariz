using Diariz.Api.Configuration;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Diariz.Api.IntegrationTests;

/// <summary>RoomMember.PrincipalId carries no FK (it points at either AspNetUsers or UserGroups), so the
/// database cannot cascade. Deleting a user or a group must therefore sweep their RoomMember rows explicitly,
/// or a stale row survives - inert on a personal room today, but a live grant once shared rooms have members.
/// Verified against real Postgres: the in-memory provider enforces no FK and would hide the whole question.</summary>
[Collection(IntegrationCollection.Name)]
public class RoomMemberSweepIntegrationTests(ContainersFixture fx)
{
    private ServiceProvider BuildIdentity()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddDbContext<DiarizDbContext>(o => o.UseNpgsql(fx.PostgresConnectionString, x => x.UseVector()));
        services.AddIdentityCore<ApplicationUser>(o =>
            {
                o.Password.RequiredLength = 8;
                o.Password.RequireUppercase = true;
                o.Password.RequireLowercase = true;
                o.Password.RequireDigit = true;
                o.Password.RequireNonAlphanumeric = true;
                o.User.RequireUniqueEmail = true;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<DiarizDbContext>()
            .AddDefaultTokenProviders();
        return services.BuildServiceProvider();
    }

    private static AdminUsersController AdminUsers(UserManager<ApplicationUser> users, DiarizDbContext db, Guid callerId) =>
        new(users, new FakeEmailSender(), db, new PlatformSettingsService(db),
            Options.Create(new AppPublicOptions { PublicUrl = "http://localhost:8081" }), new UserPermissions(db),
            new FakeAudioStorage(), NullLogger<AdminUsersController>.Instance)
        {
            ControllerContext = Http.Context(callerId),
        };

    [Fact]
    public async Task DeletingAUser_SweepsTheirRoomMemberRows()
    {
        await using var sp = BuildIdentity();
        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var db = sp.GetRequiredService<DiarizDbContext>();

        // The caller: an administrator who may delete users.
        var admin = new ApplicationUser { UserName = $"adm-{Guid.NewGuid():N}@x.test", Email = $"adm-{Guid.NewGuid():N}@x.test" };
        await users.CreateAsync(admin);
        Perms.Grant(db, admin.Id, Perms.Administrator);

        // The victim: a user with a personal room, its owner member row, and membership of a shared room.
        var victim = new ApplicationUser { UserName = $"v-{Guid.NewGuid():N}@x.test", Email = $"v-{Guid.NewGuid():N}@x.test" };
        await users.CreateAsync(victim);
        var personal = new Room { Id = Guid.NewGuid(), Name = "Victim", Kind = RoomKind.Personal, OwnerUserId = victim.Id };
        var shared = new Room { Id = Guid.NewGuid(), Name = $"Shared {Guid.NewGuid():N}", Kind = RoomKind.Shared };
        db.Rooms.AddRange(personal, shared);
        db.RoomMembers.Add(new RoomMember { RoomId = personal.Id, PrincipalType = RoomPrincipalType.User, PrincipalId = victim.Id, Permissions = RoomPermission.ManageRoom });
        db.RoomMembers.Add(new RoomMember { RoomId = shared.Id, PrincipalType = RoomPrincipalType.User, PrincipalId = victim.Id, Permissions = RoomPermission.CreateRecording });
        await db.SaveChangesAsync();

        var result = await AdminUsers(users, db, admin.Id).Delete(victim.Id);

        Assert.IsType<Microsoft.AspNetCore.Mvc.NoContentResult>(result);
        await using var verify = fx.CreateDbContext();
        Assert.Empty(await verify.RoomMembers
            .Where(m => m.PrincipalType == RoomPrincipalType.User && m.PrincipalId == victim.Id)
            .ToListAsync());
    }

    [Fact]
    public async Task DeletingAGroup_SweepsItsRoomMemberRows()
    {
        await using var db = fx.CreateDbContext();

        var group = new UserGroup { Id = Guid.NewGuid(), Name = $"Eng {Guid.NewGuid():N}" };
        var shared = new Room { Id = Guid.NewGuid(), Name = $"Shared {Guid.NewGuid():N}", Kind = RoomKind.Shared };
        db.UserGroups.Add(group);
        db.Rooms.Add(shared);
        db.RoomMembers.Add(new RoomMember { RoomId = shared.Id, PrincipalType = RoomPrincipalType.Group, PrincipalId = group.Id, Permissions = RoomPermission.CreateRecording });
        await db.SaveChangesAsync();

        var result = await new GroupsController(db).Delete(group.Id);

        Assert.IsType<Microsoft.AspNetCore.Mvc.NoContentResult>(result);
        await using var verify = fx.CreateDbContext();
        Assert.Empty(await verify.RoomMembers
            .Where(m => m.PrincipalType == RoomPrincipalType.Group && m.PrincipalId == group.Id)
            .ToListAsync());
    }
}
