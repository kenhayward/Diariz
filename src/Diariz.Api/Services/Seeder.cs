using Diariz.Domain;
using Microsoft.EntityFrameworkCore;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Diariz.Api.Services;

/// <summary>Startup seeding of roles and the Platform Administrator. Public + idempotent so it runs
/// safely on every boot (backfilling existing deployments) and can be exercised by integration tests.</summary>
public static class Seeder
{
    /// <summary>Ensure the three application roles exist.</summary>
    public static async Task SeedRolesAsync(IServiceProvider sp)
    {
        var roles = sp.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var name in new[] { Roles.Standard, Roles.Administrator, Roles.PlatformAdministrator })
            if (!await roles.RoleExistsAsync(name))
                await roles.CreateAsync(new IdentityRole<Guid>(name));
    }

    /// <summary>Find-or-create the seed user and enforce that it is the active, enabled Platform
    /// Administrator (backfills users created before this feature).</summary>
    public static async Task SeedDefaultUserAsync(IServiceProvider sp, IConfiguration config)
    {
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Seed");
        var email = config["Seed:Email"];
        var password = config["Seed:Password"];
        var fullName = config["Seed:FullName"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning("Seed skipped: Seed:Email / Seed:Password not configured.");
            return;
        }

        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            user = new ApplicationUser { UserName = email, Email = email, FullName = fullName };
            var result = await users.CreateAsync(user, password);
            if (!result.Succeeded)
            {
                logger.LogError("Seed user {Email} creation FAILED: {Errors}", email,
                    string.Join("; ", result.Errors.Select(e => e.Description)));
                return;
            }
            logger.LogInformation("Seed user {Email} created.", email);
        }

        // Backfill / enforce platform-admin status (covers users created before this feature).
        user.Status = UserStatus.Active;
        user.IsEnabled = true;
        user.EmailConfirmed = true;
        if (string.IsNullOrWhiteSpace(user.FullName) && !string.IsNullOrWhiteSpace(fullName))
            user.FullName = fullName;
        await users.UpdateAsync(user);

        if (!await users.IsInRoleAsync(user, Roles.PlatformAdministrator))
            await users.AddToRoleAsync(user, Roles.PlatformAdministrator);
    }

    /// <summary>The two seeded groups, mirroring the roles they replace.</summary>
    public const string PlatformAdminsGroup = "Platform Administrators";
    public const string AdminsGroup = "Administrators";

    /// <summary>Ensure both seeded groups exist with the right flags. Idempotent; runs on every boot.
    /// Administrators deliberately does NOT carry ManagePlatform: that flag confers backup/restore and
    /// platform-settings writes, which the Administrator role has never had.</summary>
    public static async Task SeedGroupsAsync(DiarizDbContext db)
    {
        await EnsureGroup(db, PlatformAdminsGroup, isSystem: true,
            PlatformPermission.ManageRooms | PlatformPermission.ManageUsers | PlatformPermission.ManagePlatform);
        await EnsureGroup(db, AdminsGroup, isSystem: false,
            PlatformPermission.ManageRooms | PlatformPermission.ManageUsers);
        await db.SaveChangesAsync();

        static async Task EnsureGroup(DiarizDbContext db, string name, bool isSystem, PlatformPermission perms)
        {
            var group = await db.UserGroups.FirstOrDefaultAsync(g => g.Name == name);
            if (group is null)
            {
                db.UserGroups.Add(new UserGroup
                {
                    Id = Guid.NewGuid(), Name = name, IsSystem = isSystem, Permissions = perms,
                });
                return;
            }
            // Backfill on an existing deployment. Only ever ADD the flags we own, so an operator who granted
            // the group something extra does not have it silently revoked on the next boot.
            group.IsSystem = isSystem;
            group.Permissions |= perms;
        }
    }

    /// <summary>One-way move of Identity role holders into the seeded groups. Idempotent: a user already in
    /// the group is skipped. The roles remain in the database, unused, until a later chore removes them.</summary>
    public static async Task MigrateRolesToGroupsAsync(DiarizDbContext db)
    {
        await Move(db, Roles.PlatformAdministrator, PlatformAdminsGroup);
        await Move(db, Roles.Administrator, AdminsGroup);
        await db.SaveChangesAsync();

        static async Task Move(DiarizDbContext db, string roleName, string groupName)
        {
            var group = await db.UserGroups.FirstOrDefaultAsync(g => g.Name == groupName);
            var role = await db.Roles.FirstOrDefaultAsync(r => r.Name == roleName);
            if (group is null || role is null) return;

            var holders = await db.UserRoles.Where(ur => ur.RoleId == role.Id).Select(ur => ur.UserId).ToListAsync();
            var existing = await db.UserGroupMembers.Where(m => m.GroupId == group.Id)
                .Select(m => m.UserId).ToListAsync();

            foreach (var userId in holders.Except(existing))
                db.UserGroupMembers.Add(new UserGroupMember { GroupId = group.Id, UserId = userId });
        }
    }
}
