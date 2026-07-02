using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Diariz.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public class RbacIntegrationTests(ContainersFixture fx)
{
    /// <summary>Builds a real UserManager/RoleManager + token providers over the Postgres container.</summary>
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

    private static TokenService Tokens() => new(Options.Create(new JwtOptions
    {
        Key = "integration-signing-key-at-least-32-bytes!!", AccessTokenMinutes = 60,
    }));

    [Fact]
    public async Task Seeding_MakesSeedUserPlatformAdmin_Active_Idempotent()
    {
        await using var sp = BuildIdentity();
        var email = $"seed-{Guid.NewGuid():N}@x.test";
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Seed:Email"] = email,
            ["Seed:Password"] = "ChangeMe123!",
            ["Seed:FullName"] = "Platform Administrator",
        }).Build();

        await Seeder.SeedRolesAsync(sp);
        await Seeder.SeedDefaultUserAsync(sp, config);
        await Seeder.SeedDefaultUserAsync(sp, config); // idempotent: second run must not duplicate or break

        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var seed = await users.FindByEmailAsync(email);
        Assert.NotNull(seed);
        Assert.Equal(UserStatus.Active, seed!.Status);
        Assert.True(seed.IsEnabled);
        Assert.True(seed.EmailConfirmed);
        Assert.Contains(Roles.PlatformAdministrator, await users.GetRolesAsync(seed));
        Assert.Equal(1, await users.Users.CountAsync(u => u.Email == email));
    }

    [Fact]
    public async Task FullLifecycle_RequestGrantSetupLogin_OverPostgres()
    {
        await using var sp = BuildIdentity();
        await Seeder.SeedRolesAsync(sp);
        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var db = sp.GetRequiredService<DiarizDbContext>();
        var platform = new PlatformSettingsService(db);
        var auth = new AuthController(users, Tokens(), platform,
            new GoogleAuthService(new HttpClient(), Options.Create(new GoogleAuthOptions())),
            new GoogleSignInHandler(users, platform),
            Options.Create(new GoogleAuthOptions()), Options.Create(new AppPublicOptions()),
            sp.GetRequiredService<IDataProtectionProvider>());

        var email = $"life-{Guid.NewGuid():N}@x.test";

        // 1) request access
        await auth.RequestAccess(new RequestAccessRequest(email));
        Assert.Equal(UserStatus.Requested, (await users.FindByEmailAsync(email))!.Status);

        // 2) admin grants (SMTP off → setup link returned)
        var admin = new ApplicationUser { UserName = $"adm-{Guid.NewGuid():N}@x.test", Email = $"adm-{Guid.NewGuid():N}@x.test" };
        await users.CreateAsync(admin);
        await users.AddToRoleAsync(admin, Roles.Administrator);
        var adminCtrl = new AdminUsersController(users, new FakeEmailSender { Sent = false }, db, platform,
            Options.Create(new AppPublicOptions { PublicUrl = "http://localhost:8081" }))
        {
            ControllerContext = Http.Context(admin.Id, [Roles.Administrator]),
        };
        var requested = await users.FindByEmailAsync(email);
        var grant = (await adminCtrl.Grant(requested!.Id)).Value!;
        Assert.False(grant.Emailed);
        var token = Uri.UnescapeDataString(new Uri(grant.SetupUrl!).Query.Split("token=")[1]);

        // 3) setup with the real token (round-trips through Identity's default provider over Postgres)
        var setup = await auth.Setup(new SetupRequest(email, token, "Real User", "ChangeMe123!"));
        Assert.IsType<OkObjectResult>(setup);
        Assert.Equal(UserStatus.Active, (await users.FindByEmailAsync(email))!.Status);

        // 4) login now succeeds; the reused setup token is rejected (single-use)
        Assert.IsType<OkObjectResult>(await auth.Login(new LoginRequest(email, "ChangeMe123!")));
        Assert.IsType<BadRequestObjectResult>(await auth.Setup(new SetupRequest(email, token, "Real User", "ChangeMe123!")));
    }

    [Fact]
    public async Task DeletingUser_CascadesRecordingsSectionsAndChat()
    {
        var userId = Guid.NewGuid();
        var recId = Guid.NewGuid();
        var trId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var chatId = Guid.NewGuid();

        await using (var db = fx.CreateDbContext())
        {
            db.Users.Add(new ApplicationUser { Id = userId, UserName = $"{userId}@x.test", Email = $"{userId}@x.test" });
            var rec = new Recording { Id = recId, UserId = userId, BlobKey = "k", Title = "R" };
            var tr = new Transcription { Id = trId, RecordingId = recId, Model = "m", Version = 1 };
            var seg = new Segment { Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "S0", Original = "hi", Ordinal = 0 };
            db.AddRange(rec, tr, seg,
                new Section { Id = sectionId, UserId = userId, Name = "Sec" },
                new ChatSession { Id = chatId, UserId = userId, Title = "C" });
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
        {
            db.Users.Remove((await db.Users.FindAsync(userId))!);
            await db.SaveChangesAsync();
        }

        await using (var verify = fx.CreateDbContext())
        {
            Assert.False(await verify.Recordings.AnyAsync(r => r.Id == recId));
            Assert.False(await verify.Sections.AnyAsync(s => s.Id == sectionId)); // the newly-added cascade
            Assert.False(await verify.ChatSessions.AnyAsync(c => c.Id == chatId));
            Assert.False(await verify.Segments.AnyAsync(s => s.TranscriptionId == trId)); // via Recording→Transcription cascade
        }
    }
}
