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

        var list = await Build(db, userId).List();

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
    public async Task Create_UnderASubSection_RejectsThirdLevel()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var parent = await SeedSection(db, userId, "Customers");
        var child = await SeedSection(db, userId, "Acme", parentId: parent.Id);

        var result = await Build(db, userId).Create(new CreateSectionRequest("Project X", child.Id));

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
    public async Task Reorder_UnderASubSection_RejectsThirdLevel()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var parent = await SeedSection(db, userId, "Customers");
        var child = await SeedSection(db, userId, "Acme", parentId: parent.Id);
        var loose = await SeedSection(db, userId, "Loose");

        Assert.IsType<BadRequestObjectResult>(
            await Build(db, userId).Reorder(new ReorderSectionsRequest(child.Id, [loose.Id])));
    }

    [Fact]
    public async Task Reorder_MovingAParentWithChildren_UnderAnother_IsRejected()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var top = await SeedSection(db, userId, "Top");
        var hasChild = await SeedSection(db, userId, "HasChild");
        var grandchild = await SeedSection(db, userId, "Kid", parentId: hasChild.Id);
        _ = grandchild;

        // Moving HasChild under Top would make Kid a third level → rejected.
        Assert.IsType<BadRequestObjectResult>(
            await Build(db, userId).Reorder(new ReorderSectionsRequest(top.Id, [hasChild.Id])));
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
