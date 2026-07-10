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

    private static async Task<Guid> NewRecordingAsync(DiarizDbContext db, Guid userId)
    {
        var rec = new Recording
        {
            Id = Guid.NewGuid(), UserId = userId, Title = "Rec", BlobKey = $"{userId}/{Guid.NewGuid():N}.webm",
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

    // The two backfill tests that ran RecordingPlacementBackfill.Sql directly were removed here: that SQL reads
    // Recordings.SectionId, which this phase's DropRecordingSectionId migration removes, so it can no longer be
    // executed against the fully-migrated test schema. The backfill is a one-shot migration - its logic is
    // frozen in AddRoomRecordings (verified when written, at commit d1c8f40) and re-verified on a copy of dev in
    // this phase's manual deployment checks. The personal-room mint it depends on is covered by
    // RoomsIntegrationTests (PersonalRoomBackfill) and the RoomScope find-or-create tests.

    /// <summary>The column is gone: the folder is a property of the placement, and there is no second, stale
    /// place to write it.</summary>
    [Fact]
    public async Task Recordings_NoLongerHasASectionIdColumn()
    {
        await using var db = fx.CreateDbContext();

        var count = await db.Database
            .SqlQuery<int>($"""
                SELECT count(*)::int AS "Value" FROM information_schema.columns
                WHERE table_name = 'Recordings' AND column_name = 'SectionId'
                """)
            .SingleAsync();

        Assert.Equal(0, count);
    }
}
