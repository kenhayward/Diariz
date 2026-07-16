using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

public class RoomsControllerTests
{
    private static RoomsController Build(DiarizDbContext db, Guid userId)
    {
        Users.Ensure(db, userId);
        return new(new Diariz.Api.Services.RoomScope(db), db) { ControllerContext = Http.Context(userId) };
    }

    [Fact]
    public async Task List_ReturnsThePersonalRoom_WithFullPermissions()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var controller = Build(db, me);

        var result = Assert.IsType<OkObjectResult>(await controller.List());
        var rooms = Assert.IsAssignableFrom<IEnumerable<RoomListItemDto>>(result.Value);
        var only = Assert.Single(rooms);
        Assert.True(only.IsPersonal);
        Assert.Equal(RoomKind.Personal, only.Kind);
        Assert.Equal((int)RoomPermission.CreateRecording, only.Permissions & (int)RoomPermission.CreateRecording);
        Assert.Equal((int)RoomPermission.ManageRoom, only.Permissions & (int)RoomPermission.ManageRoom);
    }

    [Fact]
    public async Task List_DoesNotReturnRoomsTheCallerIsNotAMemberOf()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var controller = Build(db, me);
        db.Rooms.Add(new Room { Id = Guid.NewGuid(), Name = "Someone else", Kind = RoomKind.Shared });
        await db.SaveChangesAsync();

        var result = Assert.IsType<OkObjectResult>(await controller.List());
        var rooms = Assert.IsAssignableFrom<IEnumerable<RoomListItemDto>>(result.Value);
        Assert.Single(rooms); // only the personal room, minted on demand
    }

    [Fact]
    public async Task List_IncludesASharedRoomTheCallerIsAMemberOf()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var controller = Build(db, me);
        var shared = new Room { Id = Guid.NewGuid(), Name = "Engineering", Kind = RoomKind.Shared };
        db.Rooms.Add(shared);
        db.RoomMembers.Add(new RoomMember
        {
            RoomId = shared.Id, PrincipalType = RoomPrincipalType.User, PrincipalId = me,
            Permissions = RoomPermission.CreateRecording,
        });
        await db.SaveChangesAsync();

        var result = Assert.IsType<OkObjectResult>(await controller.List());
        var rooms = Assert.IsAssignableFrom<IEnumerable<RoomListItemDto>>(result.Value).ToList();
        Assert.Equal(2, rooms.Count);
        Assert.True(rooms[0].IsPersonal); // personal first
        var eng = rooms.Single(r => !r.IsPersonal);
        Assert.Equal("Engineering", eng.Name);
        Assert.Equal((int)RoomPermission.CreateRecording, eng.Permissions);
    }

    // ---- Writes (Phase 4) ----

    [Fact]
    public async Task Create_MakesASharedRoom()
    {
        using var db = TestDb.Create();
        var controller = Build(db, Guid.NewGuid());

        var created = Assert.IsType<CreatedAtActionResult>(
            await controller.Create(new RoomInput("Engineering", "the eng team", "users", "#123456")));
        var id = (Guid)created.RouteValues!["id"]!;

        var room = db.Rooms.Single(r => r.Id == id);
        Assert.Equal(RoomKind.Shared, room.Kind);
        Assert.Equal("Engineering", room.Name);
    }

    [Fact]
    public async Task Create_RejectsADuplicateSharedName()
    {
        using var db = TestDb.Create();
        var controller = Build(db, Guid.NewGuid());
        await controller.Create(new RoomInput("Engineering", null, null, null));

        Assert.IsType<ConflictObjectResult>(await controller.Create(new RoomInput("Engineering", null, null, null)));
    }

    [Fact]
    public async Task Update_RenamesASharedRoom()
    {
        using var db = TestDb.Create();
        var controller = Build(db, Guid.NewGuid());
        var created = (CreatedAtActionResult)await controller.Create(new RoomInput("Eng", null, null, null));
        var id = (Guid)created.RouteValues!["id"]!;

        Assert.IsType<NoContentResult>(await controller.Update(id, new RoomInput("Engineering", "d", "star", "#abcdef")));
        Assert.Equal("Engineering", db.Rooms.Single(r => r.Id == id).Name);
    }

    [Fact]
    public async Task Update_RefusesThePersonalRoom()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var controller = Build(db, me);
        var personalId = await new Diariz.Api.Services.RoomScope(db).PersonalRoomIdAsync(me);

        Assert.IsType<BadRequestObjectResult>(await controller.Update(personalId, new RoomInput("Hacked", null, null, null)));
    }

    [Fact]
    public async Task Delete_RemovesASharedRoom()
    {
        using var db = TestDb.Create();
        var controller = Build(db, Guid.NewGuid());
        var created = (CreatedAtActionResult)await controller.Create(new RoomInput("Eng", null, null, null));
        var id = (Guid)created.RouteValues!["id"]!;

        Assert.IsType<NoContentResult>(await controller.Delete(id));
        Assert.False(db.Rooms.Any(r => r.Id == id));
    }

    [Fact]
    public async Task Delete_RefusesThePersonalRoom()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var controller = Build(db, me);
        var personalId = await new Diariz.Api.Services.RoomScope(db).PersonalRoomIdAsync(me);

        Assert.IsType<BadRequestObjectResult>(await controller.Delete(personalId));
        Assert.True(db.Rooms.Any(r => r.Id == personalId));
    }

    [Fact]
    public async Task SetMember_ThenGet_ReturnsTheMembership()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var controller = Build(db, me);
        var created = (CreatedAtActionResult)await controller.Create(new RoomInput("Eng", null, null, null));
        var id = (Guid)created.RouteValues!["id"]!;
        var member = Guid.NewGuid();

        Assert.IsType<NoContentResult>(await controller.SetMember(id,
            new RoomMemberInput(RoomPrincipalType.User, member, (int)RoomPermission.CreateRecording)));

        // Add the caller as a member so they can read the detail.
        await controller.SetMember(id, new RoomMemberInput(RoomPrincipalType.User, me, (int)RoomPermission.ManageRoom));
        var detail = Assert.IsType<RoomDetailDto>(Assert.IsType<OkObjectResult>(await controller.Get(id)).Value);
        Assert.Contains(detail.Members, m => m.PrincipalId == member && m.Permissions == (int)RoomPermission.CreateRecording);
    }

    [Fact]
    public async Task Get_ResolvesMemberDisplayNames_ForUsersAndGroups()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var controller = Build(db, me);
        var created = (CreatedAtActionResult)await controller.Create(new RoomInput("Eng", null, null, null));
        var id = (Guid)created.RouteValues!["id"]!;

        var alice = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = alice, UserName = "alice@x.test", Email = "alice@x.test", FullName = "Alice Smith" });
        var groupId = Guid.NewGuid();
        db.UserGroups.Add(new UserGroup { Id = groupId, Name = "Editors" });
        await db.SaveChangesAsync();
        await controller.SetMember(id, new RoomMemberInput(RoomPrincipalType.User, alice, (int)RoomPermission.CreateRecording));
        await controller.SetMember(id, new RoomMemberInput(RoomPrincipalType.Group, groupId, (int)RoomPermission.CreateRecording));

        var detail = Assert.IsType<RoomDetailDto>(Assert.IsType<OkObjectResult>(await controller.Get(id)).Value);
        Assert.Equal("Alice Smith", detail.Members.Single(m => m.PrincipalId == alice).DisplayName);
        Assert.Equal("Editors", detail.Members.Single(m => m.PrincipalId == groupId).DisplayName);
    }

    [Fact]
    public async Task Get_HidesARoomTheCallerIsNotAMemberOf()
    {
        using var db = TestDb.Create();
        // The creator joins as a member, so view the room as a DIFFERENT user who never joined it.
        var created = (CreatedAtActionResult)await Build(db, Guid.NewGuid()).Create(new RoomInput("Eng", null, null, null));
        var id = (Guid)created.RouteValues!["id"]!;

        Assert.IsType<NotFoundResult>(await Build(db, Guid.NewGuid()).Get(id)); // a non-member
    }

    [Fact]
    public async Task Create_AddsTheCreatorAsAMember_SoTheRoomAppearsInTheirList()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var controller = Build(db, me);
        await controller.Create(new RoomInput("Engineering", null, null, null));

        // The room shows up in the creator's own list (they were made a member).
        var result = Assert.IsType<OkObjectResult>(await controller.List());
        var rooms = Assert.IsAssignableFrom<IEnumerable<RoomListItemDto>>(result.Value);
        Assert.Contains(rooms, r => !r.IsPersonal && r.Name == "Engineering");
    }

    // ---- Unshare (Phase 5) ----

    [Fact]
    public async Task RemoveRecording_Unshares_ForARoomMemberWhoRecordedIt()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var controller = Build(db, me);
        var scope = new Diariz.Api.Services.RoomScope(db);
        var recId = Guid.NewGuid();
        db.Recordings.Add(new Recording { Id = recId, UserId = me, Title = "Standup" });
        await db.SaveChangesAsync();
        await scope.PlaceInMainRoomAsync(recId, me, sectionId: null);
        var roomId = await scope.CreateSharedRoomAsync("Eng", null, null, null);
        await scope.SetMemberAsync(roomId, RoomPrincipalType.User, me, RoomPermission.CreateRecording);
        await scope.ShareIntoRoomAsync(recId, roomId, me, sectionId: null);

        Assert.IsType<NoContentResult>(await controller.RemoveRecording(roomId, recId));
        Assert.False(await db.RoomRecordings.AnyAsync(p => p.RoomId == roomId && p.RecordingId == recId));
        Assert.True(await db.Recordings.AnyAsync(r => r.Id == recId)); // recording survives
    }

    [Fact]
    public async Task RemoveRecording_RefusesTheMainRoom()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var controller = Build(db, me);
        var scope = new Diariz.Api.Services.RoomScope(db);
        var recId = Guid.NewGuid();
        db.Recordings.Add(new Recording { Id = recId, UserId = me, Title = "Standup" });
        await db.SaveChangesAsync();
        var personalRoomId = await scope.PersonalRoomIdAsync(me);
        await scope.PlaceInMainRoomAsync(recId, me, sectionId: null);

        // The home room can't unshare - that would be a delete, from the recording detail.
        Assert.IsType<BadRequestObjectResult>(await controller.RemoveRecording(personalRoomId, recId));
        Assert.True(await db.RoomRecordings.AnyAsync(p => p.RoomId == personalRoomId && p.RecordingId == recId));
    }

    [Fact]
    public async Task RemoveRecording_403_ForANonRecorderWithoutRemoveOthers()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        Users.Ensure(db, owner);
        var controller = Build(db, other); // a member, but not the recorder, without RemoveOthersRecordings
        var scope = new Diariz.Api.Services.RoomScope(db);
        var recId = Guid.NewGuid();
        db.Recordings.Add(new Recording { Id = recId, UserId = owner, Title = "Standup" });
        await db.SaveChangesAsync();
        await scope.PlaceInMainRoomAsync(recId, owner, sectionId: null);
        var roomId = await scope.CreateSharedRoomAsync("Eng", null, null, null);
        await scope.ShareIntoRoomAsync(recId, roomId, owner, sectionId: null);
        await scope.SetMemberAsync(roomId, RoomPrincipalType.User, other, RoomPermission.CreateRecording); // no RemoveOthers

        Assert.Equal(403, ((ObjectResult)await controller.RemoveRecording(roomId, recId)).StatusCode);
        Assert.True(await db.RoomRecordings.AnyAsync(p => p.RoomId == roomId && p.RecordingId == recId));
    }

    // ---- Counts (the room switcher's "N sections . M recordings" line) ----
    // These are per-room and cross-room, so unlike the drill-in list's counts they cannot be derived on the
    // client without one fetch per room. Plain EF, no raw SQL, so the in-memory provider is enough.

    [Fact]
    public async Task List_ReportsSectionAndRecordingCountsPerRoom()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var controller = Build(db, me);
        var scope = new Diariz.Api.Services.RoomScope(db);
        var personal = await scope.PersonalRoomIdAsync(me);

        db.Sections.Add(new Section { Id = Guid.NewGuid(), UserId = me, RoomId = personal, Name = "A" });
        db.Sections.Add(new Section { Id = Guid.NewGuid(), UserId = me, RoomId = personal, Name = "B" });
        for (var i = 0; i < 3; i++)
        {
            var rec = new Recording { Id = Guid.NewGuid(), UserId = me, Title = $"r{i}", Source = RecordingSource.Upload };
            db.Recordings.Add(rec);
            db.RoomRecordings.Add(new RoomRecording { RoomId = personal, RecordingId = rec.Id });
        }
        await db.SaveChangesAsync();

        var result = Assert.IsType<OkObjectResult>(await controller.List());
        var only = Assert.Single(Assert.IsAssignableFrom<IEnumerable<RoomListItemDto>>(result.Value));
        Assert.Equal(2, only.SectionCount);
        Assert.Equal(3, only.RecordingCount);
    }

    /// An empty room must report zeroes, not be omitted or left null - the switcher shows the line either way.
    [Fact]
    public async Task List_ReportsZeroCountsForAnEmptyRoom()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var controller = Build(db, me);

        var result = Assert.IsType<OkObjectResult>(await controller.List());
        var only = Assert.Single(Assert.IsAssignableFrom<IEnumerable<RoomListItemDto>>(result.Value));
        Assert.Equal(0, only.SectionCount);
        Assert.Equal(0, only.RecordingCount);
    }

    /// The counts describe each room, not the caller's totals: a recording in my personal room must not be
    /// counted against a shared room I am also in.
    [Fact]
    public async Task List_CountsAreScopedToEachRoom_NotTheCallersTotals()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var controller = Build(db, me);
        var scope = new Diariz.Api.Services.RoomScope(db);
        var personal = await scope.PersonalRoomIdAsync(me);
        var shared = await scope.CreateSharedRoomAsync("Eng", null, null, null);
        await scope.SetMemberAsync(shared, RoomPrincipalType.User, me, RoomPermission.ManageContents);

        db.Sections.Add(new Section { Id = Guid.NewGuid(), UserId = me, RoomId = personal, Name = "Mine" });
        var mine = new Recording { Id = Guid.NewGuid(), UserId = me, Title = "mine", Source = RecordingSource.Upload };
        db.Recordings.Add(mine);
        db.RoomRecordings.Add(new RoomRecording { RoomId = personal, RecordingId = mine.Id });
        await db.SaveChangesAsync();

        var result = Assert.IsType<OkObjectResult>(await controller.List());
        var rooms = Assert.IsAssignableFrom<IEnumerable<RoomListItemDto>>(result.Value).ToList();

        var p = rooms.Single(r => r.IsPersonal);
        var s = rooms.Single(r => !r.IsPersonal);
        Assert.Equal(1, p.SectionCount);
        Assert.Equal(1, p.RecordingCount);
        Assert.Equal(0, s.SectionCount);
        Assert.Equal(0, s.RecordingCount);
    }

    /// A recording shared into two rooms counts once in each - the count answers "what will I find in here",
    /// not "how many distinct recordings exist".
    [Fact]
    public async Task List_CountsASharedRecordingInEveryRoomItIsPlacedIn()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var controller = Build(db, me);
        var scope = new Diariz.Api.Services.RoomScope(db);
        var personal = await scope.PersonalRoomIdAsync(me);
        var shared = await scope.CreateSharedRoomAsync("Eng", null, null, null);
        await scope.SetMemberAsync(shared, RoomPrincipalType.User, me, RoomPermission.ManageContents);

        var rec = new Recording { Id = Guid.NewGuid(), UserId = me, Title = "both", Source = RecordingSource.Upload };
        db.Recordings.Add(rec);
        db.RoomRecordings.Add(new RoomRecording { RoomId = personal, RecordingId = rec.Id });
        db.RoomRecordings.Add(new RoomRecording { RoomId = shared, RecordingId = rec.Id });
        await db.SaveChangesAsync();

        var result = Assert.IsType<OkObjectResult>(await controller.List());
        var rooms = Assert.IsAssignableFrom<IEnumerable<RoomListItemDto>>(result.Value).ToList();
        Assert.Equal(1, rooms.Single(r => r.IsPersonal).RecordingCount);
        Assert.Equal(1, rooms.Single(r => !r.IsPersonal).RecordingCount);
    }
}
