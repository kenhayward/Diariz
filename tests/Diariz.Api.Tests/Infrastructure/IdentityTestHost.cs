using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Diariz.Api.Tests.Infrastructure;

/// <summary>
/// Builds a real <see cref="UserManager{ApplicationUser}"/> backed by the EF in-memory provider,
/// mirroring the Identity configuration in <c>Program.cs</c> (8-char passwords, unique email).
/// Lets <see cref="Diariz.Api.Controllers.AuthController"/> be tested with genuine password hashing
/// and validation instead of mocks. Dispose to release the service provider / in-memory database.
/// </summary>
public sealed class IdentityTestHost : IDisposable
{
    private readonly ServiceProvider _sp;
    public UserManager<ApplicationUser> Users { get; }
    public RoleManager<IdentityRole<Guid>> Roles { get; }
    public DiarizDbContext Db { get; }

    public IdentityTestHost()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection(); // required by the default token providers (setup token)
        services.AddDbContext<DiarizDbContext>(o => o.UseInMemoryDatabase($"diariz-identity-{Guid.NewGuid()}"));
        services.AddIdentityCore<ApplicationUser>(o =>
            {
                // Mirror the strengthened production policy so tests exercise the real validators.
                o.Password.RequiredLength = 8;
                o.Password.RequireUppercase = true;
                o.Password.RequireLowercase = true;
                o.Password.RequireDigit = true;
                o.Password.RequireNonAlphanumeric = true;
                o.User.RequireUniqueEmail = true;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<DiarizDbContext>()
            .AddDefaultTokenProviders(); // needed to generate/verify the one-time setup token

        _sp = services.BuildServiceProvider();
        Users = _sp.GetRequiredService<UserManager<ApplicationUser>>();
        Roles = _sp.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        Db = _sp.GetRequiredService<DiarizDbContext>();
    }

    /// <summary>Ensure the app's roles exist (idempotent) so role assignment works in tests.</summary>
    public async Task SeedRolesAsync()
    {
        foreach (var name in new[] { Domain.Entities.Roles.Standard, Domain.Entities.Roles.Administrator, Domain.Entities.Roles.PlatformAdministrator })
            if (!await Roles.RoleExistsAsync(name))
                await Roles.CreateAsync(new IdentityRole<Guid>(name));
    }

    public void Dispose() => _sp.Dispose();
}
