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
    public async Task List_ReturnsOwnSections_OrderedByName()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.Sections.AddRange(
            new Section { Id = Guid.NewGuid(), UserId = userId, Name = "Zeta" },
            new Section { Id = Guid.NewGuid(), UserId = userId, Name = "Alpha" },
            new Section { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Name = "Other" });
        await db.SaveChangesAsync();

        var list = await Build(db, userId).List();

        Assert.Equal(["Alpha", "Zeta"], list.Select(s => s.Name));
    }
}
