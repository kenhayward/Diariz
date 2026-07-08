using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public class TagsIntegrationTests(ContainersFixture fx)
{
    [Fact]
    public async Task RecordingTags_RoundTrip_AndCascadeDeleteWithRecording()
    {
        Guid recId;
        await using (var db = fx.CreateDbContext())
        {
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
            var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = "k1", Title = "Planning" };
            var tag0 = new RecordingTag { Id = Guid.NewGuid(), RecordingId = rec.Id, Tag = "Budget Planning", Weight = 0.9, Ordinal = 0 };
            var tag1 = new RecordingTag { Id = Guid.NewGuid(), RecordingId = rec.Id, Tag = "Vendor Selection", Weight = 0.4, Ordinal = 1 };
            rec.TagsExtractedAt = DateTimeOffset.UtcNow;
            db.AddRange(user, rec, tag0, tag1);
            await db.SaveChangesAsync();
            recId = rec.Id;
        }

        // Round-trip: rows come back ordered by Ordinal with their weights, and the marker persisted.
        await using (var verify = fx.CreateDbContext())
        {
            var rec = await verify.Recordings.Include(r => r.Tags).SingleAsync(r => r.Id == recId);
            Assert.NotNull(rec.TagsExtractedAt);
            var tags = rec.Tags.OrderBy(t => t.Ordinal).ToList();
            Assert.Equal(2, tags.Count);
            Assert.Equal("Budget Planning", tags[0].Tag);
            Assert.Equal(0.9, tags[0].Weight, 3);
            Assert.Equal("Vendor Selection", tags[1].Tag);
        }

        // Deleting the recording cascades to its tags in real Postgres (FK ON DELETE CASCADE).
        await using (var db = fx.CreateDbContext())
        {
            var rec = await db.Recordings.SingleAsync(r => r.Id == recId);
            db.Recordings.Remove(rec);
            await db.SaveChangesAsync();
        }

        await using (var verify = fx.CreateDbContext())
            Assert.Empty(await verify.RecordingTags.Where(t => t.RecordingId == recId).ToListAsync());
    }

    [Fact]
    public async Task TagsEndpoint_AggregatesOwnerScoped_OnRealPostgres_AndReflectsCascade()
    {
        Guid userId, keepId, dropId;
        await using (var db = fx.CreateDbContext())
        {
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
            var other = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "o@x.test" };
            var keep = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = "k1", Title = "A" };
            var drop = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = "k2", Title = "B" };
            var foreign = new Recording { Id = Guid.NewGuid(), UserId = other.Id, BlobKey = "k3", Title = "C" };
            db.AddRange(user, other, keep, drop, foreign,
                new RecordingTag { Id = Guid.NewGuid(), RecordingId = keep.Id, Tag = "Roadmap", Weight = 0.8, Ordinal = 0 },
                new RecordingTag { Id = Guid.NewGuid(), RecordingId = drop.Id, Tag = "Roadmap", Weight = 0.6, Ordinal = 0 },
                new RecordingTag { Id = Guid.NewGuid(), RecordingId = foreign.Id, Tag = "Roadmap", Weight = 0.9, Ordinal = 0 });
            await db.SaveChangesAsync();
            (userId, keepId, dropId) = (user.Id, keep.Id, drop.Id);
        }

        await using (var db = fx.CreateDbContext())
        {
            var controller = new TagsController(db) { ControllerContext = Http.Context(userId) };
            var entry = Assert.Single((await controller.List()).Value!);
            Assert.Equal("Roadmap", entry.Tag);
            Assert.Equal(2, entry.Count); // the other user's recording is excluded
            Assert.Equal(1.4, entry.Weight, 3);
        }

        // Deleting a recording cascades its tags away and the endpoint reflects it.
        await using (var db = fx.CreateDbContext())
        {
            db.Recordings.Remove(await db.Recordings.SingleAsync(r => r.Id == dropId));
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
        {
            var controller = new TagsController(db) { ControllerContext = Http.Context(userId) };
            var entry = Assert.Single((await controller.List()).Value!);
            Assert.Equal(1, entry.Count);
            Assert.Equal(keepId, Assert.Single(entry.RecordingIds));
        }
    }
}
