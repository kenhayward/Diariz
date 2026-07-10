using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

public class RoomScopePlacementTests
{
    private static async Task<Guid> NewUserAsync(DiarizDbContext db)
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid():N}@x.test", Email = $"{Guid.NewGuid():N}@x.test",
            FullName = "Ada",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<Guid> NewRecordingAsync(DiarizDbContext db, Guid userId, string title = "Rec")
    {
        var rec = new Recording
        {
            Id = Guid.NewGuid(), UserId = userId, Title = title, BlobKey = $"{userId}/{Guid.NewGuid():N}.webm",
        };
        db.Recordings.Add(rec);
        await db.SaveChangesAsync();
        return rec.Id;
    }

    [Fact]
    public async Task RecordingsIn_ReturnsOnlyThatRoomsRecordings()
    {
        using var db = TestDb.Create();
        var alice = await NewUserAsync(db);
        var bob = await NewUserAsync(db);
        var sut = new RoomScope(db);
        var aliceRoom = await sut.PersonalRoomIdAsync(alice);
        var bobRoom = await sut.PersonalRoomIdAsync(bob);

        var aliceRec = await NewRecordingAsync(db, alice, "Alice's");
        var bobRec = await NewRecordingAsync(db, bob, "Bob's");
        await sut.PlaceInMainRoomAsync(aliceRec, alice, sectionId: null);
        await sut.PlaceInMainRoomAsync(bobRec, bob, sectionId: null);

        var titles = await sut.RecordingsIn(aliceRoom).Select(r => r.Title).ToListAsync();

        Assert.Equal(["Alice's"], titles);
        Assert.Equal(["Bob's"], await sut.RecordingsIn(bobRoom).Select(r => r.Title).ToListAsync());
    }

    [Fact]
    public async Task PlaceInMainRoom_PutsItInTheRecordersPersonalRoom_WithTheFolder()
    {
        using var db = TestDb.Create();
        var userId = await NewUserAsync(db);
        var sut = new RoomScope(db);
        var roomId = await sut.PersonalRoomIdAsync(userId);
        var section = new Section { Id = Guid.NewGuid(), UserId = userId, Name = "Q3" };
        db.Sections.Add(section);
        await db.SaveChangesAsync();
        var recId = await NewRecordingAsync(db, userId);

        await sut.PlaceInMainRoomAsync(recId, userId, section.Id);

        var placement = db.RoomRecordings.Single();
        Assert.Equal(roomId, placement.RoomId);
        Assert.True(placement.IsMainRoom);
        Assert.Equal(section.Id, placement.SectionId);
        Assert.Null(placement.SharedByUserId);
    }

    [Fact]
    public async Task SectionIdAsync_ReadsTheFolderForThatRoom()
    {
        using var db = TestDb.Create();
        var userId = await NewUserAsync(db);
        var sut = new RoomScope(db);
        var roomId = await sut.PersonalRoomIdAsync(userId);
        var section = new Section { Id = Guid.NewGuid(), UserId = userId, Name = "Q3" };
        db.Sections.Add(section);
        await db.SaveChangesAsync();
        var recId = await NewRecordingAsync(db, userId);
        await sut.PlaceInMainRoomAsync(recId, userId, section.Id);

        Assert.Equal(section.Id, await sut.SectionIdAsync(roomId, recId));
        Assert.Null(await sut.SectionIdAsync(Guid.NewGuid(), recId)); // a room it is not placed in
    }

    [Fact]
    public async Task SetSectionAsync_MovesAndUngroups()
    {
        using var db = TestDb.Create();
        var userId = await NewUserAsync(db);
        var sut = new RoomScope(db);
        var roomId = await sut.PersonalRoomIdAsync(userId);
        var section = new Section { Id = Guid.NewGuid(), UserId = userId, Name = "Q3" };
        db.Sections.Add(section);
        await db.SaveChangesAsync();
        var recId = await NewRecordingAsync(db, userId);
        await sut.PlaceInMainRoomAsync(recId, userId, sectionId: null);

        Assert.True(await sut.SetSectionAsync(roomId, recId, section.Id));
        Assert.Equal(section.Id, await sut.SectionIdAsync(roomId, recId));

        Assert.True(await sut.SetSectionAsync(roomId, recId, null)); // ungroup
        Assert.Null(await sut.SectionIdAsync(roomId, recId));
    }

    [Fact]
    public async Task SetSectionAsync_ReturnsFalse_WhenTheRecordingIsNotInThatRoom()
    {
        using var db = TestDb.Create();
        var userId = await NewUserAsync(db);
        var sut = new RoomScope(db);
        await sut.PersonalRoomIdAsync(userId);
        var recId = await NewRecordingAsync(db, userId);

        Assert.False(await sut.SetSectionAsync(Guid.NewGuid(), recId, null));
    }
}
