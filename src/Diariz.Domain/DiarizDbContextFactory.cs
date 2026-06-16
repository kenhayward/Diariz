using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Diariz.Domain;

/// <summary>
/// Design-time factory so `dotnet ef migrations` can build the model without booting the
/// web host (which would try to connect to Postgres/Redis/MinIO). The connection string
/// here is only used to pick the Npgsql provider; no connection is opened to add a migration.
/// </summary>
public class DiarizDbContextFactory : IDesignTimeDbContextFactory<DiarizDbContext>
{
    public DiarizDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<DiarizDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=diariz;Username=diariz;Password=diariz",
                o => o.UseVector())
            .Options;
        return new DiarizDbContext(options);
    }
}
