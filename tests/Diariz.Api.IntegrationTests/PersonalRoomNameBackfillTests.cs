using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Diariz.Domain.Migrations;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

/// <summary>The one-off correction for personal rooms that drifted before the rename sync existed. Asserted
/// against the SQL the migration runs, rather than against the shared container's accumulated state.</summary>
[Collection(IntegrationCollection.Name)]
public class PersonalRoomNameBackfillTests(ContainersFixture fx)
{
    [Fact]
    public async Task Backfill_RenamesADriftedPersonalRoom_AndIsIdempotent()
    {
        await using var db = fx.CreateDbContext();
        var user = await SeedUserAsync(db, "Ada Lovelace");
        var room = new Room
        {
            Id = Guid.NewGuid(), Name = "Platform Administrator", Kind = RoomKind.Personal, OwnerUserId = user.Id,
        };
        db.Rooms.Add(room);
        await db.SaveChangesAsync();

        // Run it twice: re-running must be safe.
        await db.Database.ExecuteSqlRawAsync(PersonalRoomNameBackfill.Sql);
        await db.Database.ExecuteSqlRawAsync(PersonalRoomNameBackfill.Sql);

        db.ChangeTracker.Clear(); // the tracked entity still holds the pre-UPDATE name
        Assert.Equal("Ada Lovelace", (await db.Rooms.SingleAsync(r => r.Id == room.Id)).Name);
    }

    [Fact]
    public async Task Backfill_LeavesSharedRoomsAlone()
    {
        await using var db = fx.CreateDbContext();
        var user = await SeedUserAsync(db, "Ada Lovelace");
        var room = new Room
        {
            // Unique: Rooms.Name carries a filtered unique index for SHARED rooms, and every class in this
            // collection shares one database - a fixed name collides with whatever another test picked.
            Id = Guid.NewGuid(), Name = $"Engineering-{Guid.NewGuid():N}", Kind = RoomKind.Shared,
            OwnerUserId = user.Id,
        };
        db.Rooms.Add(room);
        var originalName = room.Name;
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync(PersonalRoomNameBackfill.Sql);

        db.ChangeTracker.Clear();
        Assert.Equal(originalName, (await db.Rooms.SingleAsync(r => r.Id == room.Id)).Name);
    }

    /// <summary>Mirrors RoomScope.Display: a blank display name falls back to the email, never to blank -
    /// the Name column is required.</summary>
    [Fact]
    public async Task Backfill_FallsBackToEmail_ForAUserWithNoDisplayName()
    {
        await using var db = fx.CreateDbContext();
        var user = await SeedUserAsync(db, null);
        var room = new Room
        {
            Id = Guid.NewGuid(), Name = "Stale", Kind = RoomKind.Personal, OwnerUserId = user.Id,
        };
        db.Rooms.Add(room);
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync(PersonalRoomNameBackfill.Sql);

        db.ChangeTracker.Clear();
        Assert.Equal(user.Email, (await db.Rooms.SingleAsync(r => r.Id == room.Id)).Name);
    }

    private static async Task<ApplicationUser> SeedUserAsync(DiarizDbContext db, string? fullName)
    {
        var email = $"{Guid.NewGuid():N}@x.test";
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(), UserName = email, Email = email, FullName = fullName, Status = UserStatus.Active,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }
}
