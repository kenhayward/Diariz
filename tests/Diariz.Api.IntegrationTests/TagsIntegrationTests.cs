using System.Data.Common;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

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
    // deliberately persists the removal BEFORE the winner's text is rewritten. Command ordering within a
    // single SaveChangesAsync batch is NOT a guarantee EF/Npgsql make in either direction - a mutation check
    // against this test confirmed that forcing the update to land first (its own round trip, ahead of the
    // delete) reproduces a real Npgsql.PostgresException 23505 on IX_RecordingTags_RecordingId_TagLower in
    // 4 of 5 runs (the 5th happened to pick the one non-colliding "winner", since the underlying query has
    // no ORDER BY). That is exactly why the code must not depend on the provider's batching order either
    // way: deleting first and persisting it removes the dependency entirely. The in-memory provider has no
    // such index, so this order only matters on real Postgres.
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

    // The detail query single-queries three sibling collections already (Speakers, Actions, and
    // Transcriptions -> Segments). When this test was written EF was in single-query mode here, so including
    // a fourth collection multiplied the cartesian product by the tag count, and EVERY row of that product
    // carried a Segment.Embedding (vector(768)). Since 0.228.4 the app-wide default is SplitQuery, so that
    // multiplication can no longer happen - the assertion is kept because the tags still belong in their own
    // query on their own merits, and this pins that they are not folded back in. Measured on a 57-minute recording (11 speakers, 7 actions, 670 segments, 13 tags):
    // 670,670 rows and a 1.8 GB external sort spill at 10.6 s WITH the tags joined in, against 51,590 rows,
    // 133 MB and 0.6 s without - while reading the tags separately costs 0.036 ms off the index. So this test
    // asserts the shape, not the timing: the tags must not ride on the segment query.
    [Fact]
    public async Task Get_ReadsTheTagsInTheirOwnQuery_NotJoinedOntoTheSegments()
    {
        Guid userId, recId;
        await using (var db = fx.CreateDbContext())
        {
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u6@x.test" };
            var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = $"k/{Guid.NewGuid()}" };
            var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "m", Version = 1 };
            var adoptedFirst = DateTimeOffset.UtcNow.AddMinutes(-5);
            db.AddRange(user, rec, tr,
                new Segment { Id = Guid.NewGuid(), TranscriptionId = tr.Id, Ordinal = 0, SpeakerLabel = "SPEAKER_00", Original = "one", StartMs = 0, EndMs = 10 },
                new Segment { Id = Guid.NewGuid(), TranscriptionId = tr.Id, Ordinal = 1, SpeakerLabel = "SPEAKER_01", Original = "two", StartMs = 10, EndMs = 20 },
                new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "A" },
                new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_01", DisplayName = "B" },
                new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "do it", Ordinal = 0 },
                new RecordingTag { Id = Guid.NewGuid(), RecordingId = rec.Id, Tag = "first", Weight = 1.0, Ordinal = 0, Status = RecordingTagStatus.Adopted, AdoptedAt = adoptedFirst },
                new RecordingTag { Id = Guid.NewGuid(), RecordingId = rec.Id, Tag = "second", Weight = 1.0, Ordinal = 1, Status = RecordingTagStatus.Adopted, AdoptedAt = DateTimeOffset.UtcNow },
                new RecordingTag { Id = Guid.NewGuid(), RecordingId = rec.Id, Tag = "hint", Weight = 0.6, Ordinal = 2, Status = RecordingTagStatus.Suggested },
                new RecordingTag { Id = Guid.NewGuid(), RecordingId = rec.Id, Tag = "rejected", Weight = 0.9, Ordinal = 3, Status = RecordingTagStatus.Dismissed });
            await db.SaveChangesAsync();
            userId = user.Id;
            recId = rec.Id;
        }

        var sql = new RecordsSql();
        var options = new DbContextOptionsBuilder<DiarizDbContext>()
            .UseNpgsql(fx.PostgresConnectionString, o => o.UseVector())
            .AddInterceptors(sql)
            .Options;
        await using (var db = new DiarizDbContext(options))
        {
            var dto = (await Recordings.Build(db, userId).Get(recId)).Value!;
            // The behaviour the shape must not cost: adopted in adoption order, suggestions by weight, and
            // the dismissal invisible.
            Assert.Equal(["first", "second"], dto.Tags!);
            Assert.Equal(["hint"], dto.SuggestedTags!);
        }

        var segmentQuery = Assert.Single(sql.Statements, s => s.Contains("\"Segments\""));
        Assert.DoesNotContain("\"RecordingTags\"", segmentQuery);
        Assert.Contains(sql.Statements, s => s.Contains("\"RecordingTags\"") && !s.Contains("\"Segments\""));
    }

    /// <summary>Inserts <paramref name="tag"/> on <paramref name="recordingId"/> from its OWN connection, the
    /// first time the context it is attached to tries to save. That is the deterministic stand-in for the real
    /// race: two room members add the same tag at once, both read the rows and find no adopted match, and both
    /// insert. Real concurrency cannot be reproduced reliably in a test, but the DATABASE sees exactly this -
    /// another transaction's row already committed under the unique index when ours arrives.</summary>
    private sealed class InsertsTheSameTagFirst(string connectionString, Guid recordingId, string tag)
        : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Fired) return result;
            Fired = true;

            var options = new DbContextOptionsBuilder<DiarizDbContext>()
                .UseNpgsql(connectionString, o => o.UseVector())
                .Options;
            await using var rival = new DiarizDbContext(options);
            rival.RecordingTags.Add(new RecordingTag
            {
                Id = Guid.NewGuid(),
                RecordingId = recordingId,
                Tag = tag,
                Weight = 1.0,
                Ordinal = 0,
                Status = RecordingTagStatus.Adopted,
                AdoptedAt = DateTimeOffset.UtcNow,
            });
            await rival.SaveChangesAsync(ct);
            return result;
        }
    }

    // AddTag calls itself idempotent, and it is - for anything it can SEE. Two members adding the same tag at
    // the same time both find no adopted row and both insert, and IX_RecordingTags_RecordingId_TagLower
    // rejects the second one. DiarizDbContext's own comment on that index predicts this ("a duplicate here
    // means a race between two room members"). Postgres-only: the in-memory provider has no unique index, so
    // nothing there can reproduce the violation.
    [Fact]
    public async Task AddTag_WhenAnotherMemberWinsTheRace_IsStillIdempotentRatherThanA500()
    {
        Guid userId, recId;
        await using (var db = fx.CreateDbContext())
        {
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u4@x.test" };
            var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = $"k/{Guid.NewGuid()}" };
            db.AddRange(user, rec);
            await db.SaveChangesAsync();
            userId = user.Id;
            recId = rec.Id;
        }

        var rival = new InsertsTheSameTagFirst(fx.PostgresConnectionString, recId, "metadata");
        var options = new DbContextOptionsBuilder<DiarizDbContext>()
            .UseNpgsql(fx.PostgresConnectionString, o => o.UseVector())
            .AddInterceptors(rival)
            .Options;
        await using (var db = new DiarizDbContext(options))
        {
            // No adopted match exists when AddTag reads, so it takes the insert branch; the rival's row lands
            // in between. The caller asked for a tag that is now there, which is the outcome they wanted.
            var result = await Recordings.Build(db, userId).AddTag(recId, new SetRecordingTagRequest("metadata"));
            Assert.IsType<NoContentResult>(result);
        }
        Assert.True(rival.Fired);   // the race really was staged, so NoContent is not a false pass

        await using (var verify = fx.CreateDbContext())
        {
            // Exactly one adopted row - the rival's. Ours never landed, and nothing was corrupted.
            var tag = Assert.Single(await verify.RecordingTags.Where(t => t.RecordingId == recId).ToListAsync());
            Assert.Equal(RecordingTagStatus.Adopted, tag.Status);
            Assert.Equal("metadata", tag.Tag);
        }
    }

    /// <summary>Deletes the recording out from under the context it is attached to, the first time that context
    /// saves - so the pending INSERT fails on the RecordingId foreign key rather than on the unique index. The
    /// other side of the coin from <see cref="InsertsTheSameTagFirst"/>.</summary>
    private sealed class DeletesTheRecordingFirst(string connectionString, Guid recordingId) : SaveChangesInterceptor
    {
        private bool _fired;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (_fired) return result;
            _fired = true;

            var options = new DbContextOptionsBuilder<DiarizDbContext>()
                .UseNpgsql(connectionString, o => o.UseVector())
                .Options;
            await using var rival = new DiarizDbContext(options);
            await rival.Recordings.Where(r => r.Id == recordingId).ExecuteDeleteAsync(ct);
            return result;
        }
    }

    // The guard on the catch above: AddTag reports success for a losing race ONLY because the tag really is
    // adopted afterwards. Any other write failure has to keep failing, or the catch would be a blanket
    // swallow that turns a lost tag into a silent 204.
    [Fact]
    public async Task AddTag_WhenTheWriteFailsForAnyOtherReason_StillFails()
    {
        Guid userId, recId;
        await using (var db = fx.CreateDbContext())
        {
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u5@x.test" };
            var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = $"k/{Guid.NewGuid()}" };
            db.AddRange(user, rec);
            await db.SaveChangesAsync();
            userId = user.Id;
            recId = rec.Id;
        }

        var options = new DbContextOptionsBuilder<DiarizDbContext>()
            .UseNpgsql(fx.PostgresConnectionString, o => o.UseVector())
            .AddInterceptors(new DeletesTheRecordingFirst(fx.PostgresConnectionString, recId))
            .Options;
        await using var ctx = new DiarizDbContext(options);
        await Assert.ThrowsAsync<DbUpdateException>(
            () => Recordings.Build(ctx, userId).AddTag(recId, new SetRecordingTagRequest("metadata")));
    }
}
