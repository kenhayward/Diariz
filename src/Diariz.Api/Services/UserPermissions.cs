using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>Resolves a user's platform permissions as the union of the flags on every group they belong to.
/// Read from the database per request rather than from a JWT claim: a claim would go stale the moment a user
/// is added to or removed from a group, and would keep working until their token expired.</summary>
public interface IUserPermissions
{
    Task<PlatformPermission> ForAsync(Guid userId, CancellationToken ct = default);

    /// <summary>True when the user holds ANY of the requested flags. "Any", not "all", so a single policy can
    /// express "manage users OR manage platform".</summary>
    Task<bool> HasAsync(Guid userId, PlatformPermission anyOf, CancellationToken ct = default);
}

public class UserPermissions(DiarizDbContext db) : IUserPermissions
{
    public async Task<PlatformPermission> ForAsync(Guid userId, CancellationToken ct = default)
    {
        // Queried from the group side (rather than Select(m => m.Group!.Permissions)) so it translates on both
        // Npgsql and the in-memory test provider, which does not fix up the navigation for an untracked query.
        var flags = await db.UserGroups
            .Where(g => g.Members.Any(m => m.UserId == userId))
            .Select(g => g.Permissions)
            .ToListAsync(ct);

        var result = PlatformPermission.None;
        foreach (var f in flags) result |= f;
        return result;
    }

    public async Task<bool> HasAsync(Guid userId, PlatformPermission anyOf, CancellationToken ct = default) =>
        (await ForAsync(userId, ct) & anyOf) != PlatformPermission.None;
}
