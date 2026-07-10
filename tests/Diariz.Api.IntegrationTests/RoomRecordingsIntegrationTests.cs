using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Diariz.Domain.Migrations;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

/// <summary>Placement against real Postgres: the filtered unique index on IsMainRoom, the check constraint, the
/// SET NULL on folder delete, and the backfill - none of which the in-memory provider honours.</summary>
[Collection(IntegrationCollection.Name)]
public class RoomRecordingsIntegrationTests(ContainersFixture fx)
{
    private static async Task<Guid> NewUserAsync(DiarizDbContext db, string? fullName = null)
    {
        var name = $"u{Guid.NewGuid():N}@x.test";
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = name, NormalizedUserName = name.ToUpperInvariant(),
            Email = name, NormalizedEmail = name.ToUpperInvariant(),
            FullName = fullName,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<Guid> NewRecordingAsync(DiarizDbContext db, Guid userId, Guid? sectionId = null)
    {
        var rec = new Recording
        {
            Id = Guid.NewGuid(), UserId = userId, Title = "Rec", BlobKey = $"{userId}/{Guid.NewGuid():N}.webm",
            SectionId = sectionId,
        };
        db.Recordings.Add(rec);
        await db.SaveChangesAsync();
        return rec.Id;
    }

    private static async Task<Guid> NewSectionAsync(DiarizDbContext db, Guid userId)
    {
        var s = new Section { Id = Guid.NewGuid(), UserId = userId, Name = $"Folder {Guid.NewGuid():N}" };
        db.Sections.Add(s);
        await db.SaveChangesAsync();
        return s.Id;
    }

    private static async Task<Guid> PersonalRoomAsync(DiarizDbContext db, Guid userId)
    {
        await db.Database.ExecuteSqlRawAsync(PersonalRoomBackfill.Sql);
        return await db.Rooms.Where(r => r.OwnerUserId == userId).Select(r => r.Id).SingleAsync();
    }

    [Fact]
    public async Task ARecordingHasAtMostOneMainRoom()
    {
        await using var db = fx.CreateDbContext();
        var userId = await NewUserAsync(db);
        var recId = await NewRecordingAsync(db, userId);
        var roomA = await PersonalRoomAsync(db, userId);
        var roomB = new Room { Id = Guid.NewGuid(), Name = $"Shared {Guid.NewGuid():N}", Kind = RoomKind.Shared };
        db.Rooms.Add(roomB);
        await db.SaveChangesAsync();

        db.RoomRecordings.Add(new RoomRecording { RoomId = roomA, RecordingId = recId, IsMainRoom = true });
        await db.SaveChangesAsync();

        db.RoomRecordings.Add(new RoomRecording { RoomId = roomB.Id, RecordingId = recId, IsMainRoom = true });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task ARecordingMayBeSharedIntoManyRooms()
    {
        await using var db = fx.CreateDbContext();
        var userId = await NewUserAsync(db);
        var recId = await NewRecordingAsync(db, userId);
        var main = await PersonalRoomAsync(db, userId);
        var shared = new Room { Id = Guid.NewGuid(), Name = $"Shared {Guid.NewGuid():N}", Kind = RoomKind.Shared };
        db.Rooms.Add(shared);
        await db.SaveChangesAsync();

        db.RoomRecordings.Add(new RoomRecording { RoomId = main, RecordingId = recId, IsMainRoom = true });
        db.RoomRecordings.Add(new RoomRecording
        {
            RoomId = shared.Id, RecordingId = recId, IsMainRoom = false,
            SharedByUserId = userId, SharedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.RoomRecordings.CountAsync(p => p.RecordingId == recId));
    }

    /// <summary>Nobody shared a recording into its own home: the check constraint says so.</summary>
    [Fact]
    public async Task AMainPlacementCannotCarryASharer()
    {
        await using var db = fx.CreateDbContext();
        var userId = await NewUserAsync(db);
        var recId = await NewRecordingAsync(db, userId);
        var main = await PersonalRoomAsync(db, userId);

        db.RoomRecordings.Add(new RoomRecording
        {
            RoomId = main, RecordingId = recId, IsMainRoom = true,
            SharedByUserId = userId, SharedAt = DateTimeOffset.UtcNow,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    /// <summary>Deleting a folder ungroups its recordings; it must never remove them from the room.</summary>
    [Fact]
    public async Task DeletingASection_UngroupsThePlacement_ButKeepsIt()
    {
        await using var db = fx.CreateDbContext();
        var userId = await NewUserAsync(db);
        var sectionId = await NewSectionAsync(db, userId);
        var recId = await NewRecordingAsync(db, userId);
        var main = await PersonalRoomAsync(db, userId);
        db.RoomRecordings.Add(new RoomRecording
        {
            RoomId = main, RecordingId = recId, IsMainRoom = true, SectionId = sectionId,
        });
        await db.SaveChangesAsync();

        db.Sections.Remove(await db.Sections.FindAsync(sectionId) ?? throw new InvalidOperationException("gone"));
        await db.SaveChangesAsync();

        var placement = await db.RoomRecordings.SingleAsync(p => p.RecordingId == recId);
        Assert.Null(placement.SectionId);
        Assert.Equal(main, placement.RoomId);
    }

    [Fact]
    public async Task DeletingARecording_CascadesItsPlacements()
    {
        await using var db = fx.CreateDbContext();
        var userId = await NewUserAsync(db);
        var recId = await NewRecordingAsync(db, userId);
        var main = await PersonalRoomAsync(db, userId);
        db.RoomRecordings.Add(new RoomRecording { RoomId = main, RecordingId = recId, IsMainRoom = true });
        await db.SaveChangesAsync();

        db.Recordings.Remove(await db.Recordings.FindAsync(recId) ?? throw new InvalidOperationException("gone"));
        await db.SaveChangesAsync();

        Assert.Empty(await db.RoomRecordings.Where(p => p.RecordingId == recId).ToListAsync());
    }

    /// <summary>The backfill: one main placement per recording, in its recorder's personal room, carrying the
    /// folder the recording was in. Idempotent.</summary>
    [Fact]
    public async Task Backfill_PlacesEveryRecordingInItsRecordersPersonalRoom_KeepingItsFolder()
    {
        await using var db = fx.CreateDbContext();
        var userId = await NewUserAsync(db, "Ada Lovelace");
        var sectionId = await NewSectionAsync(db, userId);
        var filed = await NewRecordingAsync(db, userId, sectionId);
        var ungrouped = await NewRecordingAsync(db, userId);

        await db.Database.ExecuteSqlRawAsync(RecordingPlacementBackfill.Sql);
        await db.Database.ExecuteSqlRawAsync(RecordingPlacementBackfill.Sql); // twice: must not duplicate

        var room = await db.Rooms.Where(r => r.OwnerUserId == userId).Select(r => r.Id).SingleAsync();

        var a = await db.RoomRecordings.SingleAsync(p => p.RecordingId == filed);
        Assert.Equal(room, a.RoomId);
        Assert.True(a.IsMainRoom);
        Assert.Equal(sectionId, a.SectionId);
        Assert.Null(a.SharedByUserId);

        var b = await db.RoomRecordings.SingleAsync(p => p.RecordingId == ungrouped);
        Assert.True(b.IsMainRoom);
        Assert.Null(b.SectionId);
    }

    /// <summary>THE TRAP. Phase 2a gave rooms only to users who existed then, and nothing calls RoomScope yet,
    /// so a user created since has NO personal room - and may already own recordings. The backfill must mint
    /// the missing room first, or their recordings are silently left unplaced and vanish from their list.</summary>
    [Fact]
    public async Task Backfill_MintsAMissingPersonalRoom_ForAUserCreatedAfterPhase2a()
    {
        await using var db = fx.CreateDbContext();
        var userId = await NewUserAsync(db, "Grace Hopper");
        var recId = await NewRecordingAsync(db, userId);
        Assert.Empty(await db.Rooms.Where(r => r.OwnerUserId == userId).ToListAsync()); // no room yet

        await db.Database.ExecuteSqlRawAsync(RecordingPlacementBackfill.Sql);

        var room = await db.Rooms.SingleAsync(r => r.OwnerUserId == userId);
        var placement = await db.RoomRecordings.SingleAsync(p => p.RecordingId == recId);
        Assert.Equal(room.Id, placement.RoomId);
        Assert.True(placement.IsMainRoom);
    }
}
