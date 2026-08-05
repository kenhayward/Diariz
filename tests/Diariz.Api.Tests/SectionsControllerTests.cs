using Diariz.Api.Services;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

public class SectionsControllerTests
{
    // Ensures the caller exists (RoomScope.PersonalRoomIdAsync needs a real user) and injects the scope.
    private static SectionsController Build(DiarizDbContext db, Guid userId)
    {
        if (db.Users.Find(userId) is null)
        {
            db.Users.Add(new ApplicationUser { Id = userId, UserName = $"{userId}@x.test", Email = $"{userId}@x.test" });
            db.SaveChanges();
        }
        return new(db, new RoomScope(db)) { ControllerContext = Http.Context(userId) };
    }

    // Seeds a section in the owner's personal room (folders are room-scoped now).
    private static async Task<Section> SeedSection(
        DiarizDbContext db, Guid userId, string name = "F", Guid? parentId = null, int position = 0)
    {
        Build(db, userId); // ensure the user
        var roomId = await new RoomScope(db).PersonalRoomIdAsync(userId);
        var s = new Section { Id = Guid.NewGuid(), UserId = userId, RoomId = roomId, Name = name, ParentId = parentId, Position = position };
        db.Sections.Add(s);
        await db.SaveChangesAsync();
        return s;
    }

    [Fact]
    public async Task Create_AddsSection_AndReturnsIt()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();

        var result = await Build(db, userId).Create(new CreateSectionRequest("  Work  "));

        var dto = Assert.IsType<SectionDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("Work", dto.Name); // trimmed
        Assert.Equal("Work", (await db.Sections.SingleAsync(s => s.UserId == userId)).Name);
    }

    [Fact]
    public async Task Create_InSharedRoom_WithManageContents_ScopesToThatRoom()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        Build(db, me); // ensure the user
        var scope = new RoomScope(db);
        var roomId = await scope.CreateSharedRoomAsync("Eng", null, null, null);
        await scope.SetMemberAsync(roomId, RoomPrincipalType.User, me, RoomPermission.ManageContents);

        var result = await Build(db, me).Create(new CreateSectionRequest("Topics", RoomId: roomId));

        var dto = Assert.IsType<SectionDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        var stored = await db.Sections.SingleAsync(s => s.Id == dto.Id);
        Assert.Equal(roomId, stored.RoomId);

        // And a subsection nests under it, still in the room.
        var sub = await Build(db, me).Create(new CreateSectionRequest("Sub", ParentId: dto.Id, RoomId: roomId));
        var subDto = Assert.IsType<SectionDto>(Assert.IsType<OkObjectResult>(sub.Result).Value);
        Assert.Equal(dto.Id, subDto.ParentId);
    }

    [Fact]
    public async Task Create_InSharedRoom_WithoutManageContents_Is403()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        Build(db, me);
        var scope = new RoomScope(db);
        var roomId = await scope.CreateSharedRoomAsync("Eng", null, null, null);
        await scope.SetMemberAsync(roomId, RoomPrincipalType.User, me, RoomPermission.CreateRecording); // no ManageContents

        var result = await Build(db, me).Create(new CreateSectionRequest("Topics", RoomId: roomId));
        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task Create_InRoom_TheCallerIsNotAMemberOf_Is404()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        Build(db, me);
        var roomId = await new RoomScope(db).CreateSharedRoomAsync("Eng", null, null, null); // never joined

        var result = await Build(db, me).Create(new CreateSectionRequest("Topics", RoomId: roomId));
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Create_SameName_ReturnsExisting_WithoutDuplicating()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var first = await Build(db, userId).Create(new CreateSectionRequest("Work"));
        var firstId = ((SectionDto)((OkObjectResult)first.Result!).Value!).Id;

        var second = await Build(db, userId).Create(new CreateSectionRequest("Work"));
        var secondId = ((SectionDto)((OkObjectResult)second.Result!).Value!).Id;

        Assert.Equal(firstId, secondId);
        Assert.Equal(1, await db.Sections.CountAsync(s => s.UserId == userId));
    }

    [Fact]
    public async Task Create_BlankName_ReturnsBadRequest()
    {
        using var db = TestDb.Create();
        var result = await Build(db, Guid.NewGuid()).Create(new CreateSectionRequest("   "));
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Rename_UpdatesName_OnOwnedSection()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var section = await SeedSection(db, userId, "Old");

        var result = await Build(db, userId).Rename(section.Id, new RenameSectionRequest("  New  "));

        Assert.IsType<NoContentResult>(result);
        Assert.Equal("New", (await db.Sections.FindAsync(section.Id))!.Name);
    }

    [Fact]
    public async Task Rename_OtherUsersSection_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var section = new Section { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Name = "Theirs" };
        db.Sections.Add(section);
        await db.SaveChangesAsync();

        var result = await Build(db, Guid.NewGuid()).Rename(section.Id, new RenameSectionRequest("Mine"));

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Delete_RemovesSection_AndUngroupsItsRecordings()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var section = await SeedSection(db, userId, "Work");
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, BlobKey = "k" };
        db.Recordings.Add(rec);
        await db.SaveChangesAsync();
        var scope = new RoomScope(db);
        await scope.PlaceInMainRoomAsync(rec.Id, userId, section.Id); // filed under the folder

        var result = await Build(db, userId).Delete(section.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.Null(await db.Sections.FindAsync(section.Id));
        // The folder lives on the placement; deleting the section ungroups it (RoomRecording.SectionId -> null).
        var roomId = await scope.PersonalRoomIdAsync(userId);
        Assert.Null(await scope.SectionIdAsync(roomId, rec.Id));
    }

    [Fact]
    public async Task Delete_OtherUsersSection_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var section = new Section { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Name = "Theirs" };
        db.Sections.Add(section);
        await db.SaveChangesAsync();

        Assert.IsType<NotFoundResult>(await Build(db, Guid.NewGuid()).Delete(section.Id));
    }

    [Fact]
    public async Task List_ReturnsOwnSections_OrderedByPositionThenName()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await SeedSection(db, userId, "Zeta", position: 0);
        await SeedSection(db, userId, "Alpha", position: 0);
        await SeedSection(db, userId, "Beta", position: 1);
        await SeedSection(db, Guid.NewGuid(), "Other"); // another user's - excluded

        var list = (await Build(db, userId).List()).Value!;

        // Position first (so Beta@1 sorts after the @0 pair), Name as the tiebreak within a position.
        Assert.Equal(["Alpha", "Zeta", "Beta"], list.Select(s => s.Name));
    }

    // ---- Sub-grouping (two levels) ----

    [Fact]
    public async Task Create_UnderParent_SetsParentId()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var parent = await SeedSection(db, userId, "Customers");

        var result = await Build(db, userId).Create(new CreateSectionRequest("Acme", parent.Id));

        var dto = Assert.IsType<SectionDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(parent.Id, dto.ParentId);
    }

    [Fact]
    public async Task Create_UnderASubSection_NowNestsAThirdLevel()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var parent = await SeedSection(db, userId, "Customers");
        var child = await SeedSection(db, userId, "Acme", parentId: parent.Id);

        var result = await Build(db, userId).Create(new CreateSectionRequest("Project Falcon", child.Id));

        var dto = Assert.IsType<SectionDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(child.Id, dto.ParentId);
    }

    [Fact]
    public async Task Create_BeyondMaxDepth_ReturnsBadRequest()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();

        // Build a chain exactly MaxDepth deep, then try to add one more under the deepest.
        Guid? parentId = null;
        for (var i = 0; i < SectionTree.MaxDepth; i++)
            parentId = (await SeedSection(db, userId, $"L{i}", parentId)).Id;

        var result = await Build(db, userId).Create(new CreateSectionRequest("TooDeep", parentId));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Create_UnderAnotherUsersParent_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var parent = new Section { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Name = "Theirs" };
        db.Sections.Add(parent);
        await db.SaveChangesAsync();

        Assert.IsType<NotFoundResult>(
            (await Build(db, Guid.NewGuid()).Create(new CreateSectionRequest("Mine", parent.Id))).Result);
    }

    [Fact]
    public async Task Reorder_SetsParentAndPosition()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var parent = await SeedSection(db, userId, "Customers");
        var a = await SeedSection(db, userId, "Acme");
        var b = await SeedSection(db, userId, "Beta");

        var result = await Build(db, userId).Reorder(new ReorderSectionsRequest(parent.Id, [b.Id, a.Id]));

        Assert.IsType<NoContentResult>(result);
        Assert.Equal((parent.Id, 0), ((await db.Sections.FindAsync(b.Id))!.ParentId, (await db.Sections.FindAsync(b.Id))!.Position));
        Assert.Equal((parent.Id, 1), ((await db.Sections.FindAsync(a.Id))!.ParentId, (await db.Sections.FindAsync(a.Id))!.Position));
    }

    [Fact]
    public async Task Reorder_ToTopLevel_ClearsParent()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var parent = await SeedSection(db, userId, "Customers");
        var child = await SeedSection(db, userId, "Acme", parentId: parent.Id);

        await Build(db, userId).Reorder(new ReorderSectionsRequest(null, [child.Id]));

        Assert.Null((await db.Sections.FindAsync(child.Id))!.ParentId);
    }

    [Fact]
    public async Task Reorder_UnderASubSection_NowNestsAThirdLevel()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var parent = await SeedSection(db, userId, "Customers");
        var child = await SeedSection(db, userId, "Acme", parentId: parent.Id);
        var loose = await SeedSection(db, userId, "Loose");

        var result = await Build(db, userId).Reorder(new ReorderSectionsRequest(child.Id, [loose.Id]));

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(child.Id, (await db.Sections.FindAsync(loose.Id))!.ParentId);
    }

    [Fact]
    public async Task Reorder_MovingAParentWithChildren_NowCarriesItsBranch()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var top = await SeedSection(db, userId, "Top");
        var hasChild = await SeedSection(db, userId, "HasChild");
        var grandchild = await SeedSection(db, userId, "Kid", parentId: hasChild.Id);

        var result = await Build(db, userId).Reorder(new ReorderSectionsRequest(top.Id, [hasChild.Id]));

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(top.Id, (await db.Sections.FindAsync(hasChild.Id))!.ParentId);
        // The branch travels with it - Kid is untouched and is now three levels down.
        Assert.Equal(hasChild.Id, (await db.Sections.FindAsync(grandchild.Id))!.ParentId);
    }

    [Fact]
    public async Task Reorder_IntoItsOwnDescendant_IsRejected()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var parent = await SeedSection(db, userId, "Customers");
        var child = await SeedSection(db, userId, "Acme", parentId: parent.Id);

        // Moving Customers under its own child would orphan the whole branch from the tree.
        var result = await Build(db, userId).Reorder(new ReorderSectionsRequest(child.Id, [parent.Id]));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null((await db.Sections.FindAsync(parent.Id))!.ParentId); // unchanged
    }

    [Fact]
    public async Task Reorder_WhenTheBranchWouldNotFit_IsRejected()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();

        // A target chain MaxDepth-1 deep, and a separate 2-level branch. 7 + 2 > 8, so the move is refused
        // even though the target itself is a legal place for a leaf.
        Guid? targetId = null;
        for (var i = 0; i < SectionTree.MaxDepth - 1; i++)
            targetId = (await SeedSection(db, userId, $"L{i}", targetId)).Id;
        var branch = await SeedSection(db, userId, "Branch");
        await SeedSection(db, userId, "BranchKid", parentId: branch.Id);

        var result = await Build(db, userId).Reorder(new ReorderSectionsRequest(targetId, [branch.Id]));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null((await db.Sections.FindAsync(branch.Id))!.ParentId); // unchanged
    }

    [Fact]
    public async Task Reorder_AnotherUsersSection_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var theirs = new Section { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Name = "Theirs" };
        db.Sections.Add(theirs);
        await db.SaveChangesAsync();

        Assert.IsType<NotFoundResult>(
            await Build(db, Guid.NewGuid()).Reorder(new ReorderSectionsRequest(null, [theirs.Id])));
    }
}
