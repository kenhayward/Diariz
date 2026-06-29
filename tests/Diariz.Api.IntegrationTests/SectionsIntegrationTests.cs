using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public class SectionsIntegrationTests(ContainersFixture fx)
{
    private static SectionsController Build(Diariz.Domain.DiarizDbContext db, Guid userId) =>
        new(db) { ControllerContext = Http.Context(userId) };

    [Fact]
    public async Task DeletingParent_CascadesToSubSections_AndUngroupsTheirRecordings()
    {
        Guid userId, parentId, childId, recId;
        await using (var db = fx.CreateDbContext())
        {
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
            var parent = new Section { Id = Guid.NewGuid(), UserId = user.Id, Name = "Customers" };
            var child = new Section { Id = Guid.NewGuid(), UserId = user.Id, Name = "Acme", ParentId = parent.Id };
            var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = "k", SectionId = child.Id };
            db.AddRange(user, parent, child, rec);
            await db.SaveChangesAsync();
            (userId, parentId, childId, recId) = (user.Id, parent.Id, child.Id, rec.Id);
        }

        await using (var db = fx.CreateDbContext())
            Assert.IsType<NoContentResult>(await Build(db, userId).Delete(parentId));

        await using var verify = fx.CreateDbContext();
        Assert.Null(await verify.Sections.FindAsync(parentId));
        Assert.Null(await verify.Sections.FindAsync(childId));         // sub-section cascade-deleted
        Assert.Null((await verify.Recordings.FindAsync(recId))!.SectionId); // its recording → Ungrouped
    }

    [Fact]
    public async Task Reorder_ReparentsAndOrders_ThroughRealDb()
    {
        Guid userId, parentId, aId, bId;
        await using (var db = fx.CreateDbContext())
        {
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
            var parent = new Section { Id = Guid.NewGuid(), UserId = user.Id, Name = "Customers" };
            var a = new Section { Id = Guid.NewGuid(), UserId = user.Id, Name = "Acme" };
            var b = new Section { Id = Guid.NewGuid(), UserId = user.Id, Name = "Beta" };
            db.AddRange(user, parent, a, b);
            await db.SaveChangesAsync();
            (userId, parentId, aId, bId) = (user.Id, parent.Id, a.Id, b.Id);
        }

        await using (var db = fx.CreateDbContext())
            Assert.IsType<NoContentResult>(
                await Build(db, userId).Reorder(new ReorderSectionsRequest(parentId, [bId, aId])));

        await using var verify = fx.CreateDbContext();
        var list = await Build(verify, userId).List();
        var subs = list.Where(s => s.ParentId == parentId).OrderBy(s => s.Position).Select(s => s.Id).ToList();
        Assert.Equal([bId, aId], subs); // reparented under Customers, in the requested order
    }
}
