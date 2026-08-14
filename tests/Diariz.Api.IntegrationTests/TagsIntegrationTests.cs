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

    // Renamed from AddTag_PromotesACaseVariantSuggestion_WithoutViolatingTheUniqueIndex on review: a
    // suggestion "Data Collection" and a request "data collection" differ in literal characters (space vs
    // hyphen would result), not merely case, so there is only ever one matching row here - nothing here can
    // put two rows' raw text at risk of the same lower(Tag). This proves promotion + the normalised-lookup,
    // not index safety; see AddTag_ConvergesTwoNonAdoptedCaseVariants_WithoutATransientUniqueIndexViolation
    // below for the test that actually exercises the index-collision-avoiding write order.
    [Fact]
    public async Task AddTag_PromotesALegacySpacedSuggestion_RewritingItToTheNormalisedForm()
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
            // Exactly one row survives (the suggestion was flipped in place, not duplicated), it is
            // Adopted, and its text is the normalised form of the REQUEST ("data collection" -> hyphenated,
            // case preserved as typed) - not the suggestion's original spaced, title-cased text. AddTag
            // always rewrites to the normalised request text on promotion (see its doc comment).
            var tag = Assert.Single(await db.RecordingTags.Where(t => t.RecordingId == recId).ToListAsync());
            Assert.Equal(RecordingTagStatus.Adopted, tag.Status);
            Assert.Equal("data-collection", tag.Tag);
            Assert.NotNull(tag.AdoptedAt);
        }
    }

    // The genuinely Postgres-only case: TWO non-adopted rows on one recording that both normalise to the
    // same tag but differ in literal text (one still spaced, one already hyphenated) - legal under the
    // unique index because their raw lower(Tag) values differ. AddTag's "no adopted match" branch has to
    // pick a winner, rewrite ITS text to the normalised form, and remove the other - and a prior fix
    // deliberately persists the removal BEFORE the winner's text is rewritten, because within one
    // SaveChangesAsync EF sends updates before deletes: writing the winner's text first would transiently
    // give two rows the same lower(Tag) and trip the index even though the end state is fine. The in-memory
    // provider has no such index, so this order only matters on real Postgres.
    [Fact]
    public async Task AddTag_ConvergesTwoNonAdoptedCaseVariants_WithoutATransientUniqueIndexViolation()
    {
        Guid userId, recId;
        await using (var db = fx.CreateDbContext())
        {
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u3@x.test" };
            var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = $"k/{Guid.NewGuid()}" };
            db.AddRange(user, rec,
                // Still spaced (legacy shape) - whichever row AddTag treats as the "winner", rewriting THIS
                // one to the normalised form is a genuine value change (space -> hyphen).
                new RecordingTag
                {
                    Id = Guid.NewGuid(), RecordingId = rec.Id, Tag = "Data Collection", Weight = 0.8,
                    Ordinal = 0, Status = RecordingTagStatus.Suggested,
                },
                // Already hyphenated and, case-folded, IS the exact text the request will normalise to -
                // this is the row a buggy write-before-delete order would collide with.
                new RecordingTag
                {
                    Id = Guid.NewGuid(), RecordingId = rec.Id, Tag = "data-collection", Weight = 0.4,
                    Ordinal = 1, Status = RecordingTagStatus.Dismissed,
                });
            await db.SaveChangesAsync();
            userId = user.Id;
            recId = rec.Id;
        }

        await using (var db = fx.CreateDbContext())
        {
            var controller = Recordings.Build(db, userId);
            // Correct order never throws, regardless of which row AddTag treats as the winner - see the
            // report for the mutation check that proves this test fails if that order regresses.
            var result = await controller.AddTag(recId, new SetRecordingTagRequest("Data Collection"));
            Assert.IsType<NoContentResult>(result);
        }

        await using (var db = fx.CreateDbContext())
        {
            var tag = Assert.Single(await db.RecordingTags.Where(t => t.RecordingId == recId).ToListAsync());
            Assert.Equal(RecordingTagStatus.Adopted, tag.Status);
            Assert.Equal("Data-Collection", tag.Tag);
        }
    }
}
