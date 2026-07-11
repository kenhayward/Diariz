using Diariz.Api.Controllers;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

public class TagsControllerTests
{
    private static TagsController Build(DiarizDbContext db, Guid userId) =>
        new(db, new Diariz.Api.Services.RoomScope(db)) { ControllerContext = Http.Context(userId) };

    private static Recording AddRecording(DiarizDbContext db, Guid userId)
    {
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, BlobKey = "k" };
        db.Recordings.Add(rec);
        return rec;
    }

    private static void AddTag(DiarizDbContext db, Guid recId, string tag, double weight, int ordinal = 0) =>
        db.RecordingTags.Add(new RecordingTag
        {
            Id = Guid.NewGuid(), RecordingId = recId, Tag = tag, Weight = weight, Ordinal = ordinal,
        });

    [Fact]
    public async Task List_ByRoom_AggregatesOnlyTheRoomsRecordings_ForAMember()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var owner = Guid.NewGuid();
        Users.Ensure(db, me);
        Users.Ensure(db, owner);
        var scope = new Diariz.Api.Services.RoomScope(db);

        var shared = AddRecording(db, owner); // shared into the room
        var mine = AddRecording(db, me);       // my personal recording, not in the room
        AddTag(db, shared.Id, "Roadmap", 0.8);
        AddTag(db, mine.Id, "Personal", 0.9);
        await db.SaveChangesAsync();
        await scope.PlaceInMainRoomAsync(shared.Id, owner, sectionId: null);
        await scope.PlaceInMainRoomAsync(mine.Id, me, sectionId: null);
        var roomId = await scope.CreateSharedRoomAsync("Eng", null, null, null);
        await scope.SetMemberAsync(roomId, RoomPrincipalType.User, me, RoomPermission.CreateRecording);
        await scope.ShareIntoRoomAsync(shared.Id, roomId, owner, sectionId: null);

        var list = (await Build(db, me).List(roomId)).Value!;
        Assert.Equal("Roadmap", Assert.Single(list).Tag);
    }

    [Fact]
    public async Task List_ByRoom_404_ForANonMember()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var roomId = await new Diariz.Api.Services.RoomScope(db).CreateSharedRoomAsync("Eng", null, null, null);
        var result = await Build(db, me).List(roomId);
        Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task List_AggregatesAcrossRecordings_SummingWeights_WithRecordingIds()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec1 = AddRecording(db, userId);
        var rec2 = AddRecording(db, userId);
        AddTag(db, rec1.Id, "Budget Planning", 0.9);
        AddTag(db, rec2.Id, "Budget Planning", 0.5);
        AddTag(db, rec2.Id, "Vendor Selection", 0.4, ordinal: 1);
        await db.SaveChangesAsync();

        var list = (await Build(db, userId).List()).Value!;

        Assert.Equal(2, list.Count);
        var budget = list[0]; // weight 1.4 sorts above 0.4
        Assert.Equal("Budget Planning", budget.Tag);
        Assert.Equal(2, budget.Count);
        Assert.Equal(1.4, budget.Weight, 3);
        Assert.Equal(2, budget.RecordingIds.Count);
        Assert.Contains(rec1.Id, budget.RecordingIds);
        Assert.Contains(rec2.Id, budget.RecordingIds);
        var vendor = list[1];
        Assert.Equal("Vendor Selection", vendor.Tag);
        Assert.Equal(1, vendor.Count);
        Assert.Equal(rec2.Id, Assert.Single(vendor.RecordingIds));
    }

    [Fact]
    public async Task List_ExcludesOtherUsersRecordings()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var mine = AddRecording(db, userId);
        var theirs = AddRecording(db, Guid.NewGuid());
        AddTag(db, mine.Id, "Mine", 0.5);
        AddTag(db, theirs.Id, "Theirs", 0.9);
        await db.SaveChangesAsync();

        var list = (await Build(db, userId).List()).Value!;

        var entry = Assert.Single(list);
        Assert.Equal("Mine", entry.Tag);
    }

    [Fact]
    public async Task List_MergesCasingVariants_DisplayingTheMostFrequent()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var r1 = AddRecording(db, userId);
        var r2 = AddRecording(db, userId);
        var r3 = AddRecording(db, userId);
        AddTag(db, r1.Id, "AI Enablement", 0.6);
        AddTag(db, r2.Id, "AI Enablement", 0.5);
        AddTag(db, r3.Id, "Ai enablement", 0.4); // LLM casing drift must not split the cloud entry
        await db.SaveChangesAsync();

        var list = (await Build(db, userId).List()).Value!;

        var entry = Assert.Single(list);
        Assert.Equal("AI Enablement", entry.Tag); // most frequent variant wins the display
        Assert.Equal(3, entry.Count);
        Assert.Equal(1.5, entry.Weight, 3);
    }

    [Fact]
    public async Task List_SortsByWeightDescending_ThenTag()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = AddRecording(db, userId);
        AddTag(db, rec.Id, "Zeta", 0.5, 0);
        AddTag(db, rec.Id, "Alpha", 0.5, 1);
        AddTag(db, rec.Id, "Heavy", 0.9, 2);
        await db.SaveChangesAsync();

        var list = (await Build(db, userId).List()).Value!;

        Assert.Equal(["Heavy", "Alpha", "Zeta"], list.Select(t => t.Tag).ToArray());
    }

    [Fact]
    public async Task List_NoTags_ReturnsEmpty()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        AddRecording(db, userId);
        await db.SaveChangesAsync();

        Assert.Empty((await Build(db, userId).List()).Value!);
    }
}
