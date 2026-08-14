using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
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
            // The cloud counts Adopted rows only (Task 3) - these three all need to be adopted (with
            // AdoptedAt set) to exercise the ownership-scoping and cascade behaviour this test is about.
            var now = DateTimeOffset.UtcNow;
            db.AddRange(user, other, keep, drop, foreign,
                new RecordingTag { Id = Guid.NewGuid(), RecordingId = keep.Id, Tag = "Roadmap", Weight = 1.0, Ordinal = 0, Status = RecordingTagStatus.Adopted, AdoptedAt = now },
                new RecordingTag { Id = Guid.NewGuid(), RecordingId = drop.Id, Tag = "Roadmap", Weight = 1.0, Ordinal = 0, Status = RecordingTagStatus.Adopted, AdoptedAt = now },
                new RecordingTag { Id = Guid.NewGuid(), RecordingId = foreign.Id, Tag = "Roadmap", Weight = 1.0, Ordinal = 0, Status = RecordingTagStatus.Adopted, AdoptedAt = now });
            await db.SaveChangesAsync();
            (userId, keepId, dropId) = (user.Id, keep.Id, drop.Id);
        }

        await using (var db = fx.CreateDbContext())
        {
            var controller = new TagsController(db, new Diariz.Api.Services.RoomScope(db)) { ControllerContext = Http.Context(userId) };
            var entry = Assert.Single((await controller.List()).Value!);
            Assert.Equal("Roadmap", entry.Tag);
            Assert.Equal(2, entry.Count); // the other user's recording is excluded
            Assert.Equal(2.0, entry.Weight, 3); // adopted weight is always 1.0, so the sum equals the count
        }

        // Deleting a recording cascades its tags away and the endpoint reflects it.
        await using (var db = fx.CreateDbContext())
        {
            db.Recordings.Remove(await db.Recordings.SingleAsync(r => r.Id == dropId));
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
        {
            var controller = new TagsController(db, new Diariz.Api.Services.RoomScope(db)) { ControllerContext = Http.Context(userId) };
            var entry = Assert.Single((await controller.List()).Value!);
            Assert.Equal(1, entry.Count);
            Assert.Equal(keepId, Assert.Single(entry.RecordingIds));
        }
    }

    [Fact]
    public async Task Cloud_AggregatesAdoptedTagsAcrossRecordings_IgnoringSuggestionsOnRealPostgres()
    {
        Guid userId, recA, recB;
        await using (var db = fx.CreateDbContext())
        {
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
            var a = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = $"k/{Guid.NewGuid()}" };
            var b = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = $"k/{Guid.NewGuid()}" };
            db.AddRange(user, a, b,
                new RecordingTag
                {
                    Id = Guid.NewGuid(), RecordingId = a.Id, Tag = "roadmap", Weight = 1.0,
                    Status = RecordingTagStatus.Adopted, AdoptedAt = DateTimeOffset.UtcNow,
                },
                new RecordingTag
                {
                    // Same tag, a different case variant, on a different recording - proves the cloud's
                    // grouping is case-insensitive across recordings, not just within one.
                    Id = Guid.NewGuid(), RecordingId = b.Id, Tag = "Roadmap", Weight = 1.0,
                    Status = RecordingTagStatus.Adopted, AdoptedAt = DateTimeOffset.UtcNow,
                },
                new RecordingTag
                {
                    // A suggestion nobody acted on - must not count towards the cloud.
                    Id = Guid.NewGuid(), RecordingId = b.Id, Tag = "noise", Weight = 0.9,
                    Status = RecordingTagStatus.Suggested,
                });
            await db.SaveChangesAsync();
            userId = user.Id;
            recA = a.Id;
            recB = b.Id;
        }

        await using (var db = fx.CreateDbContext())
        {
            var controller = new TagsController(db, new Diariz.Api.Services.RoomScope(db)) { ControllerContext = Http.Context(userId) };
            var list = (await controller.List()).Value!;
            var entry = Assert.Single(list); // the suggestion never forms a second entry
            Assert.Equal(2, entry.Count); // case variants merge across recordings
            Assert.Equal(2.0, entry.Weight, 3); // adopted weight 1.0 each -> sum == recording count
            Assert.Equal(2, entry.RecordingIds.Count);
            Assert.Contains(recA, entry.RecordingIds);
            Assert.Contains(recB, entry.RecordingIds);
        }
    }

    [Fact]
    public async Task AddTag_PromotesACaseVariantSuggestion_WithoutViolatingTheUniqueIndex()
    {
        Guid userId, recId;
        await using (var db = fx.CreateDbContext())
        {
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u2@x.test" };
            var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = $"k/{Guid.NewGuid()}" };
            db.AddRange(user, rec,
                // Suggestions are stored normalised now (Task 6), so a real extraction would never produce
                // spaced text - this seeds a legacy-shaped spaced value on purpose, to prove the promotion
                // path's normalised lookup still finds and converges an older, pre-normalisation row.
                new RecordingTag
                {
                    Id = Guid.NewGuid(), RecordingId = rec.Id, Tag = "Data Collection", Weight = 0.8,
                    Status = RecordingTagStatus.Suggested,
                });
            await db.SaveChangesAsync();
            userId = user.Id;
            recId = rec.Id;
        }

        await using (var db = fx.CreateDbContext())
        {
            var controller = Recordings.Build(db, userId);
            var result = await controller.AddTag(recId, new SetRecordingTagRequest("data collection"));
            Assert.IsType<NoContentResult>(result);
        }

        await using (var db = fx.CreateDbContext())
        {
            // Exactly one row survives (no unique-index violation from a transient duplicate), it is
            // Adopted, and its text is the normalised form of the REQUEST ("data collection" -> hyphenated,
            // case preserved as typed) - not the suggestion's original spaced, title-cased text. AddTag
            // always rewrites to the normalised request text on promotion (see its doc comment).
            var tag = Assert.Single(await db.RecordingTags.Where(t => t.RecordingId == recId).ToListAsync());
            Assert.Equal(RecordingTagStatus.Adopted, tag.Status);
            Assert.Equal("data-collection", tag.Tag);
            Assert.NotNull(tag.AdoptedAt);
        }
    }
}
