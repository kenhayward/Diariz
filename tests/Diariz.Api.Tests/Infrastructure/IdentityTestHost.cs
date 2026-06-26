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
    public DiarizDbContext Db { get; }

    public IdentityTestHost()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<DiarizDbContext>(o => o.UseInMemoryDatabase($"diariz-identity-{Guid.NewGuid()}"));
        services.AddIdentityCore<ApplicationUser>(o =>
            {
                o.Password.RequiredLength = 8;
                o.User.RequireUniqueEmail = true;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<DiarizDbContext>();

        _sp = services.BuildServiceProvider();
        Users = _sp.GetRequiredService<UserManager<ApplicationUser>>();
        Db = _sp.GetRequiredService<DiarizDbContext>();
    }

    public void Dispose() => _sp.Dispose();
}
