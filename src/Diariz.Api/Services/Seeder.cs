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
    /// Administrator (backfills users created before this feature). Returns its id, or null when seeding was
    /// skipped or failed, so the caller can guarantee the Platform Administrators group is never empty.</summary>
    public static async Task<Guid?> SeedDefaultUserAsync(IServiceProvider sp, IConfiguration config)
    {
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Seed");
        var email = config["Seed:Email"];
        var password = config["Seed:Password"];
        var fullName = config["Seed:FullName"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning("Seed skipped: Seed:Email / Seed:Password not configured.");
            return null;
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
                return null;
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

        return user.Id;
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
            PlatformPermission.ManageRooms | PlatformPermission.ManageUsers | PlatformPermission.ManagePlatform
                | PlatformPermission.ManageFormulas);
        await EnsureGroup(db, AdminsGroup, isSystem: false,
            PlatformPermission.ManageRooms | PlatformPermission.ManageUsers | PlatformPermission.ManageFormulas);
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

    /// <summary>Everything the API does at boot to keep platform authority sound: both groups exist with their
    /// flags, and the seed user is a Platform Administrator (mirroring the role SeedDefaultUserAsync grants).
    ///
    /// It deliberately does NOT reconcile Identity roles into groups. That move happens exactly once per
    /// database, in the AddUserGroups migration (see <see cref="Migrations.RoleToGroupBackfill"/>). Doing it on
    /// every boot would silently re-promote any user demoted since, because their legacy AspNetUserRoles row
    /// still names them an Administrator.</summary>
    public static async Task SeedPlatformAuthorityAsync(DiarizDbContext db, Guid? seedUserId)
    {
        await SeedGroupsAsync(db);
        if (seedUserId is not { } userId) return;

        var group = await db.UserGroups.FirstAsync(g => g.Name == PlatformAdminsGroup);
        if (!await db.UserGroupMembers.AnyAsync(m => m.GroupId == group.Id && m.UserId == userId))
        {
            db.UserGroupMembers.Add(new UserGroupMember { GroupId = group.Id, UserId = userId });
            await db.SaveChangesAsync();
        }
    }

    /// <summary>Seed the Diariz-provided starter formulas. Create-only: if a formula with the same
    /// Name already exists (there is no separate key column, so Name is the stable identity for seeds)
    /// it is left untouched, so an admin's edit to a built-in's prompt survives a reboot - mirroring
    /// the EnsureGroup pattern in <see cref="SeedGroupsAsync"/>.</summary>
    public static async Task SeedFormulasAsync(DiarizDbContext db)
    {
        await EnsureFormula(db, "Follow-up email",
            "Draft a follow-up email summarising the meeting and next steps.",
            """
            Write a concise, professional follow-up email in Markdown based on the meeting context provided.

            Structure it as:
            - A brief greeting
            - A 2-4 sentence recap of what the meeting covered
            - A bulleted list of the agreed actions, each with its owner
            - A short closing line

            Keep the tone warm but businesslike, and do not invent actions or owners that are not
            supported by the context.
            """,
            FormulaContext.Transcript | FormulaContext.Summary | FormulaContext.Actions);

        await EnsureFormula(db, "Meeting recap",
            "A short shareable recap of the meeting.",
            """
            Write a crisp Markdown recap of the meeting based on the context provided.

            Start with a one-line TL;DR, then 3-6 bullet points covering the highlights. Keep each
            bullet to a single sentence and favor concrete outcomes over general description.
            """,
            FormulaContext.Transcript | FormulaContext.Summary);

        await EnsureFormula(db, "Decisions & risks",
            "Extract the decisions made and the risks or open questions raised.",
            """
            Read the meeting context provided and produce two Markdown sections:

            ## Decisions
            A bulleted list of the concrete decisions that were made.

            ## Risks & open questions
            A bulleted list of the risks, concerns, or unresolved questions that were raised.

            If either section has nothing to report, write "None identified" under that heading
            instead of leaving it empty.
            """,
            FormulaContext.Transcript | FormulaContext.Minutes | FormulaContext.Actions);

        await EnsureFormula(db, "Tone & sentiment read",
            "A read on the emotional tone and sentiment of the meeting.",
            """
            Read the transcript provided and assess the overall tone and sentiment of the meeting in
            a few short Markdown paragraphs.

            Cover the general mood, any notable shifts in tone over the course of the conversation, and
            any moments of tension, disagreement, or enthusiasm. Be measured and avoid over-claiming -
            note where the tone is ambiguous rather than forcing a conclusion.
            """,
            FormulaContext.Transcript);

        await db.SaveChangesAsync();

        static async Task EnsureFormula(DiarizDbContext db, string name, string description, string prompt,
            FormulaContext context)
        {
            var exists = await db.Formulas.AnyAsync(f => f.Name == name);
            if (exists) return; // create-only: never overwrite an admin's edit to a built-in.

            db.Formulas.Add(new Formula
            {
                Id = Guid.NewGuid(),
                Scope = FormulaScope.Diariz,
                OwnerUserId = null,
                Name = name,
                Description = description,
                Prompt = prompt,
                Context = context,
                Enabled = true,
                IsBuiltIn = true,
            });
        }
    }
}
