using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Diariz.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public class GoogleSignInIntegrationTests(ContainersFixture fx)
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

    [Fact]
    public async Task GoogleSubject_HasUniqueIndex_TwoUsersSameSubjectRejected()
    {
        var sub = $"sub-{Guid.NewGuid():N}";
        await using var db = fx.CreateDbContext();
        db.Users.Add(new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid():N}@x.test", Email = $"{Guid.NewGuid():N}@x.test", GoogleSubject = sub });
        await db.SaveChangesAsync();

        db.Users.Add(new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid():N}@x.test", Email = $"{Guid.NewGuid():N}@x.test", GoogleSubject = sub });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task MultipleNullGoogleSubjects_Allowed()
    {
        await using var db = fx.CreateDbContext();
        db.Users.Add(new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid():N}@x.test", Email = $"{Guid.NewGuid():N}@x.test", GoogleSubject = null });
        db.Users.Add(new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid():N}@x.test", Email = $"{Guid.NewGuid():N}@x.test", GoogleSubject = null });
        await db.SaveChangesAsync(); // must not violate the unique index
    }

    [Fact]
    public async Task NewGoogleUser_CreatedPending_OverPostgres()
    {
        await using var sp = BuildIdentity();
        await Seeder.SeedRolesAsync(sp);
        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var handler = new GoogleSignInHandler(users, new PlatformSettingsService(sp.GetRequiredService<DiarizDbContext>()));
        var email = $"g-{Guid.NewGuid():N}@x.test";
        var sub = $"sub-{Guid.NewGuid():N}";

        var result = await handler.SignInAsync(new GoogleUserInfo(sub, email, true, "Ada", "https://pic/a.png", null));

        Assert.Equal(GoogleSignInOutcome.AwaitingApproval, result.Outcome);
        var user = await users.FindByEmailAsync(email);
        Assert.Equal(UserStatus.Requested, user!.Status);
        Assert.Equal(sub, user.GoogleSubject);
        Assert.Contains(Roles.Standard, await users.GetRolesAsync(user));
    }

    [Fact]
    public async Task ExistingEmail_AutoLinked_OverPostgres()
    {
        await using var sp = BuildIdentity();
        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var handler = new GoogleSignInHandler(users, new PlatformSettingsService(sp.GetRequiredService<DiarizDbContext>()));
        var email = $"link-{Guid.NewGuid():N}@x.test";
        var existing = new ApplicationUser { UserName = email, Email = email, Status = UserStatus.Active, IsEnabled = true, EmailConfirmed = true };
        await users.CreateAsync(existing);
        var sub = $"sub-{Guid.NewGuid():N}";

        var result = await handler.SignInAsync(new GoogleUserInfo(sub, email, true, "Ada", null, null));

        Assert.Equal(GoogleSignInOutcome.SignedIn, result.Outcome);
        Assert.Equal(sub, (await users.FindByIdAsync(existing.Id.ToString()))!.GoogleSubject);
    }

    [Fact]
    public async Task AdminGrant_ActivatesGoogleUserDirectly_NoSetupLink()
    {
        await using var sp = BuildIdentity();
        await Seeder.SeedRolesAsync(sp);
        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var db = sp.GetRequiredService<DiarizDbContext>();
        var platform = new PlatformSettingsService(db);

        // A pending Google account.
        var handler = new GoogleSignInHandler(users, platform);
        var email = $"grant-{Guid.NewGuid():N}@x.test";
        await handler.SignInAsync(new GoogleUserInfo($"sub-{Guid.NewGuid():N}", email, true, "Ada", null, null));
        var pending = await users.FindByEmailAsync(email);

        var admin = new ApplicationUser { UserName = $"adm-{Guid.NewGuid():N}@x.test", Email = $"adm-{Guid.NewGuid():N}@x.test" };
        await users.CreateAsync(admin);
        await users.AddToRoleAsync(admin, Roles.Administrator);
        Perms.Grant(db, admin.Id, Perms.Administrator); // authority is group membership, not a role claim
        var adminCtrl = new AdminUsersController(users, new FakeEmailSender { Sent = false }, db, platform,
            Options.Create(new AppPublicOptions { PublicUrl = "http://localhost:8081" }),
            new UserPermissions(db))
        {
            ControllerContext = Http.Context(admin.Id),
        };

        var grant = (await adminCtrl.Grant(pending!.Id)).Value!;

        Assert.False(grant.Emailed);
        Assert.Null(grant.SetupUrl); // no setup link for a Google account
        Assert.Equal(UserStatus.Active, (await users.FindByIdAsync(pending.Id.ToString()))!.Status);
    }
}
