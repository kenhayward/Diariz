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
}
