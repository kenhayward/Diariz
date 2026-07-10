using Pgvector;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Diariz.Domain.Migrations;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

/// <summary>Rooms against real Postgres: the two filtered unique indexes, the orphan-on-user-delete rule, and
/// the personal-room backfill - none of which the in-memory provider honours.</summary>
[Collection(IntegrationCollection.Name)]
public class RoomsIntegrationTests(ContainersFixture fx)
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

    private static Room Personal(Guid ownerId, string name) =>
        new() { Id = Guid.NewGuid(), Name = name, Kind = RoomKind.Personal, OwnerUserId = ownerId };

    [Fact]
    public async Task OneOwnedPersonalRoom_PerUser()
    {
        await using var db = fx.CreateDbContext();
        var userId = await NewUserAsync(db);
        db.Rooms.Add(Personal(userId, "Ada"));
        await db.SaveChangesAsync();

        db.Rooms.Add(Personal(userId, "Ada again"));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    /// <summary>Personal room names are display labels: two users may share one. Only shared rooms have unique names.</summary>
    [Fact]
    public async Task TwoUsersMayShareAPersonalRoomName()
    {
        await using var db = fx.CreateDbContext();
        var a = await NewUserAsync(db);
        var b = await NewUserAsync(db);
        var name = $"Ken Hayward {Guid.NewGuid():N}";

        db.Rooms.Add(Personal(a, name));
        db.Rooms.Add(Personal(b, name));
        await db.SaveChangesAsync(); // must not throw

        Assert.Equal(2, await db.Rooms.CountAsync(r => r.Name == name));
    }

    [Fact]
    public async Task SharedRoomNames_AreUnique()
    {
        await using var db = fx.CreateDbContext();
        var name = $"Engineering {Guid.NewGuid():N}";
        db.Rooms.Add(new Room { Id = Guid.NewGuid(), Name = name, Kind = RoomKind.Shared });
        await db.SaveChangesAsync();

        db.Rooms.Add(new Room { Id = Guid.NewGuid(), Name = name, Kind = RoomKind.Shared });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    /// <summary>Deleting a user ORPHANS their personal room rather than destroying it: the recordings inside it
    /// are shared into other people's rooms, and must survive their author's departure.</summary>
    [Fact]
    public async Task DeletingUser_OrphansTheirPersonalRoom()
    {
        await using var db = fx.CreateDbContext();
        var userId = await NewUserAsync(db);
        var room = Personal(userId, $"Ada {Guid.NewGuid():N}");
        db.Rooms.Add(room);
        db.RoomMembers.Add(new RoomMember
        {
            RoomId = room.Id, PrincipalType = RoomPrincipalType.User, PrincipalId = userId,
            Permissions = RoomPermission.ManageRoom,
        });
        await db.SaveChangesAsync();

        db.Users.Remove(await db.Users.FindAsync(userId) ?? throw new InvalidOperationException("user vanished"));
        await db.SaveChangesAsync();

        var orphan = await db.Rooms.FindAsync(room.Id);
        Assert.NotNull(orphan);
        Assert.Null(orphan!.OwnerUserId);
        Assert.Equal(RoomKind.Personal, orphan.Kind);
    }

    [Fact]
    public async Task DeletingRoom_CascadesItsMembers()
    {
        await using var db = fx.CreateDbContext();
        var userId = await NewUserAsync(db);
        var room = Personal(userId, $"Ada {Guid.NewGuid():N}");
        db.Rooms.Add(room);
        db.RoomMembers.Add(new RoomMember
        {
            RoomId = room.Id, PrincipalType = RoomPrincipalType.User, PrincipalId = userId,
            Permissions = RoomPermission.ManageRoom,
        });
        await db.SaveChangesAsync();

        db.Rooms.Remove(room);
        await db.SaveChangesAsync();

        Assert.Empty(await db.RoomMembers.Where(m => m.RoomId == room.Id).ToListAsync());
    }

    /// <summary>The migration's backfill: one personal room per pre-existing user, named after them, with the
    /// owner holding every permission. Idempotent (ON CONFLICT DO NOTHING) so re-running cannot duplicate.</summary>
    [Fact]
    public async Task Backfill_GivesEveryUserAPersonalRoom_WithFullPermissions()
    {
        await using var db = fx.CreateDbContext();
        var withName = await NewUserAsync(db, "Ada Lovelace");
        var withoutName = await NewUserAsync(db);

        await db.Database.ExecuteSqlRawAsync(PersonalRoomBackfill.Sql);
        await db.Database.ExecuteSqlRawAsync(PersonalRoomBackfill.Sql); // twice: must not duplicate

        var named = await db.Rooms.SingleAsync(r => r.OwnerUserId == withName);
        Assert.Equal("Ada Lovelace", named.Name);
        Assert.Equal(RoomKind.Personal, named.Kind);

        // No display name: fall back to the email, never to an empty string.
        var unnamed = await db.Rooms.SingleAsync(r => r.OwnerUserId == withoutName);
        Assert.False(string.IsNullOrWhiteSpace(unnamed.Name));

        var member = await db.RoomMembers.SingleAsync(m => m.RoomId == named.Id);
        Assert.Equal(RoomPrincipalType.User, member.PrincipalType);
        Assert.Equal(withName, member.PrincipalId);
        foreach (var p in new[]
                 {
                     RoomPermission.ManageRoom, RoomPermission.CreateRecording, RoomPermission.RemoveOthersRecordings,
                     RoomPermission.ShareOut, RoomPermission.ManageContents, RoomPermission.EditOthersRecordings,
                 })
            Assert.True(member.Permissions.HasFlag(p));
    }

    /// <summary>Two concurrent first-requests for the same user must yield ONE room. The filtered unique index
    /// makes the loser's insert fail, and RoomScope re-reads the winner's row. The in-memory provider enforces
    /// no such index, so this is the only place that `catch (DbUpdateException)` is exercised.</summary>
    [Fact]
    public async Task PersonalRoomIdAsync_IsSafeUnderConcurrency()
    {
        await using var setup = fx.CreateDbContext();
        var userId = await NewUserAsync(setup, "Ada Lovelace");

        // Separate DbContexts: two in-flight requests, as ASP.NET would scope them.
        await using var dbA = fx.CreateDbContext();
        await using var dbB = fx.CreateDbContext();

        var results = await Task.WhenAll(
            new RoomScope(dbA).PersonalRoomIdAsync(userId),
            new RoomScope(dbB).PersonalRoomIdAsync(userId));

        Assert.Equal(results[0], results[1]);

        await using var check = fx.CreateDbContext();
        Assert.Single(await check.Rooms.Where(r => r.OwnerUserId == userId).ToListAsync());
        Assert.Single(await check.RoomMembers.Where(m => m.PrincipalId == userId).ToListAsync());
    }

    /// <summary>A user the migration already gave a room to must not get a second one on first request.</summary>
    [Fact]
    public async Task PersonalRoomIdAsync_ReturnsTheRoomTheMigrationBackfilled()
    {
        await using var db = fx.CreateDbContext();
        var userId = await NewUserAsync(db, "Grace Hopper");
        await db.Database.ExecuteSqlRawAsync(PersonalRoomBackfill.Sql);
        var backfilled = await db.Rooms.SingleAsync(r => r.OwnerUserId == userId);

        var resolved = await new RoomScope(db).PersonalRoomIdAsync(userId);

        Assert.Equal(backfilled.Id, resolved);
        Assert.Single(await db.Rooms.Where(r => r.OwnerUserId == userId).ToListAsync());
    }

    /// <summary>Phase 2c: every section moves into its owner's personal room, minting a missing one first. The
    /// section is seeded in a throwaway valid room (the RoomId FK forbids an empty Guid); the backfill overwrites
    /// it with the owner's personal room, keyed on the still-present UserId.</summary>
    [Fact]
    public async Task SectionBackfill_PutsEachSectionInItsOwnersPersonalRoom()
    {
        await using var db = fx.CreateDbContext();
        var userId = await NewUserAsync(db, "Ada");
        var placeholder = new Room { Id = Guid.NewGuid(), Name = $"Placeholder {Guid.NewGuid():N}", Kind = RoomKind.Shared };
        db.Rooms.Add(placeholder);
        var sectionId = Guid.NewGuid();
        db.Sections.Add(new Section { Id = sectionId, UserId = userId, RoomId = placeholder.Id, Name = "F" });
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync(SectionRoomBackfill.Sql);
        await db.Database.ExecuteSqlRawAsync(SectionRoomBackfill.Sql); // idempotent

        db.ChangeTracker.Clear(); // the raw UPDATE bypassed the tracker; re-read from the database
        var room = await db.Rooms.Where(r => r.OwnerUserId == userId).Select(r => r.Id).SingleAsync();
        Assert.Equal(room, (await db.Sections.FindAsync(sectionId))!.RoomId);
    }

    /// <summary>Phase 2d: voiceprints, chats and personal meeting types move into the owner's personal room;
    /// platform meeting types (UserId null) keep RoomId null.</summary>
    [Fact]
    public async Task RoomScopedEntitiesBackfill_MovesThemIntoThePersonalRoom()
    {
        await using var db = fx.CreateDbContext();
        var userId = await NewUserAsync(db, "Ada");
        var profileId = Guid.NewGuid();
        var chatId = Guid.NewGuid();
        var personalTypeId = Guid.NewGuid();
        var platformTypeId = Guid.NewGuid();
        db.SpeakerProfiles.Add(new SpeakerProfile { Id = profileId, UserId = userId, Name = "V", Embedding = new Vector(new float[192]), SampleCount = 1 });
        db.ChatSessions.Add(new ChatSession { Id = chatId, UserId = userId, Title = "C", MessagesJson = "[]" });
        db.MeetingTypes.Add(new MeetingType { Id = personalTypeId, UserId = userId, Title = "Mine", ContentJson = "{}" });
        db.MeetingTypes.Add(new MeetingType { Id = platformTypeId, UserId = null, Title = "Platform", ContentJson = "{}", Key = $"k{Guid.NewGuid():N}" });
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync(RoomScopedEntitiesBackfill.Sql);
        await db.Database.ExecuteSqlRawAsync(RoomScopedEntitiesBackfill.Sql); // idempotent

        db.ChangeTracker.Clear();
        var room = await db.Rooms.Where(r => r.OwnerUserId == userId).Select(r => r.Id).SingleAsync();
        Assert.Equal(room, (await db.SpeakerProfiles.FindAsync(profileId))!.RoomId);
        Assert.Equal(room, (await db.ChatSessions.FindAsync(chatId))!.RoomId);
        Assert.Equal(room, (await db.MeetingTypes.FindAsync(personalTypeId))!.RoomId);
        Assert.Null((await db.MeetingTypes.FindAsync(platformTypeId))!.RoomId);
    }
}
