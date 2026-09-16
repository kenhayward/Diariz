using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>Whether an account may still use the platform: enabled by an administrator and finished setting up.
/// The same rule password sign-in applies, asked again wherever a long-lived credential is presented - a personal
/// API token, an MCP token, an OAuth access token, or a refresh - so disabling a user takes effect on every path,
/// not only at their next password sign-in.</summary>
public interface IActiveAccounts
{
    Task<bool> IsActiveAsync(Guid userId, CancellationToken ct = default);
}

public sealed class ActiveAccounts(DiarizDbContext db) : IActiveAccounts
{
    public Task<bool> IsActiveAsync(Guid userId, CancellationToken ct = default) =>
        db.Users.AnyAsync(u => u.Id == userId && u.IsEnabled && u.Status == UserStatus.Active, ct);
}
