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
    private static SectionsController Build(DiarizDbContext db, Guid userId) =>
        new(db) { ControllerContext = Http.Context(userId) };

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
        var section = new Section { Id = Guid.NewGuid(), UserId = userId, Name = "Old" };
        db.Sections.Add(section);
        await db.SaveChangesAsync();

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
        var section = new Section { Id = Guid.NewGuid(), UserId = userId, Name = "Work" };
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, BlobKey = "k", SectionId = section.Id };
        db.Sections.Add(section);
        db.Recordings.Add(rec);
        await db.SaveChangesAsync();

        var result = await Build(db, userId).Delete(section.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.Null(await db.Sections.FindAsync(section.Id));
        Assert.Null((await db.Recordings.FindAsync(rec.Id))!.SectionId); // moved to Ungrouped
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
        db.Sections.AddRange(
            new Section { Id = Guid.NewGuid(), UserId = userId, Name = "Zeta", Position = 0 },
            new Section { Id = Guid.NewGuid(), UserId = userId, Name = "Alpha", Position = 0 },
            new Section { Id = Guid.NewGuid(), UserId = userId, Name = "Beta", Position = 1 },
            new Section { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Name = "Other" });
        await db.SaveChangesAsync();

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
        var parent = new Section { Id = Guid.NewGuid(), UserId = userId, Name = "Customers" };
        db.Sections.Add(parent);
        await db.SaveChangesAsync();

        var result = await Build(db, userId).Create(new CreateSectionRequest("Acme", parent.Id));

        var dto = Assert.IsType<SectionDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(parent.Id, dto.ParentId);
    }

    [Fact]
    public async Task Create_UnderASubSection_RejectsThirdLevel()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var parent = new Section { Id = Guid.NewGuid(), UserId = userId, Name = "Customers" };
        var child = new Section { Id = Guid.NewGuid(), UserId = userId, Name = "Acme", ParentId = parent.Id };
        db.Sections.AddRange(parent, child);
        await db.SaveChangesAsync();

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
        var parent = new Section { Id = Guid.NewGuid(), UserId = userId, Name = "Customers" };
        var a = new Section { Id = Guid.NewGuid(), UserId = userId, Name = "Acme" };
        var b = new Section { Id = Guid.NewGuid(), UserId = userId, Name = "Beta" };
        db.Sections.AddRange(parent, a, b);
        await db.SaveChangesAsync();

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
        var parent = new Section { Id = Guid.NewGuid(), UserId = userId, Name = "Customers" };
        var child = new Section { Id = Guid.NewGuid(), UserId = userId, Name = "Acme", ParentId = parent.Id };
        db.Sections.AddRange(parent, child);
        await db.SaveChangesAsync();

        await Build(db, userId).Reorder(new ReorderSectionsRequest(null, [child.Id]));

        Assert.Null((await db.Sections.FindAsync(child.Id))!.ParentId);
    }

    [Fact]
    public async Task Reorder_UnderASubSection_RejectsThirdLevel()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var parent = new Section { Id = Guid.NewGuid(), UserId = userId, Name = "Customers" };
        var child = new Section { Id = Guid.NewGuid(), UserId = userId, Name = "Acme", ParentId = parent.Id };
        var loose = new Section { Id = Guid.NewGuid(), UserId = userId, Name = "Loose" };
        db.Sections.AddRange(parent, child, loose);
        await db.SaveChangesAsync();

        Assert.IsType<BadRequestObjectResult>(
            await Build(db, userId).Reorder(new ReorderSectionsRequest(child.Id, [loose.Id])));
    }

    [Fact]
    public async Task Reorder_MovingAParentWithChildren_UnderAnother_IsRejected()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var top = new Section { Id = Guid.NewGuid(), UserId = userId, Name = "Top" };
        var hasChild = new Section { Id = Guid.NewGuid(), UserId = userId, Name = "HasChild" };
        var grandchild = new Section { Id = Guid.NewGuid(), UserId = userId, Name = "Kid", ParentId = hasChild.Id };
        db.Sections.AddRange(top, hasChild, grandchild);
        await db.SaveChangesAsync();

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
