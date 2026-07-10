using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests.Infrastructure;

/// <summary>Seeds a bare <see cref="ApplicationUser"/> so <c>RoomScope.PersonalRoomIdAsync</c> (which names the
/// personal room after the user) can resolve. Controllers that scope by room now mint that room on first write,
/// so create-path tests must have a real user row - a claims principal alone is not enough.</summary>
public static class Users
{
    /// <summary>Ensures a user row exists for <paramref name="userId"/>. Idempotent.</summary>
    public static Guid Ensure(DiarizDbContext db, Guid userId, string? fullName = null)
    {
        if (!db.Users.Any(u => u.Id == userId))
        {
            var email = $"{userId:N}@x.test";
            db.Users.Add(new ApplicationUser
            {
                Id = userId,
                UserName = email, NormalizedUserName = email.ToUpperInvariant(),
                Email = email, NormalizedEmail = email.ToUpperInvariant(),
                FullName = fullName,
            });
            db.SaveChanges();
        }
        return userId;
    }
}
