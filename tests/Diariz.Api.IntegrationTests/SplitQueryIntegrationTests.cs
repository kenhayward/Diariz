using Diariz.Api.Contracts;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

/// <summary>Every endpoint that loads a recording's Speakers, Actions and Segments must do it in SEPARATE
/// statements. They are sibling collections on the same root, so in EF's default single-query mode one
/// statement returns their cartesian product - speakers x actions x segments rows, each carrying a full Segment.
///
/// The results are identical either way (EF de-duplicates the product back into the right object graph), so no
/// results-based assertion can catch this; only the emitted SQL shows it. Measured on production with 272
/// segments, 55 speakers and 7 actions: 104,720 rows for 334 rows of real data, a 207 MB external merge sort,
/// and 702 ms against 0.134 ms for the same segments read on their own. It compounds rather than plateauing -
/// the row count is a product, so every action item extracted onto a recording multiplies the whole result
/// again.
///
/// So these tests assert the shape, not the timing: no single statement may join Segments to Speakers or to
/// RecordingActions.</summary>
[Collection(IntegrationCollection.Name)]
public class SplitQueryIntegrationTests(ContainersFixture fx)
{
    /// <summary>The invariant, checked against everything the endpoint ran. Named tables rather than a row count
    /// because the cartesian product is a property of the join shape - two speakers and two actions would still
    /// "work" at four times the rows.
    ///
    /// It matches on JOIN/FROM rather than on the table name alone: EF batches its writes into a single command,
    /// so `INSERT INTO "Segments" ...; INSERT INTO "Speakers" ...` arrives as one statement mentioning both and
    /// would read as a cartesian join to a looser check. Merge writes exactly that batch.</summary>
    private static void AssertSegmentsAreNotJoinedToSiblings(IReadOnlyList<string> statements, string endpoint)
    {
        var offenders = statements
            .Where(s => (s.Contains("JOIN \"Segments\"") || s.Contains("FROM \"Segments\""))
                        && (s.Contains("JOIN \"Speakers\"") || s.Contains("JOIN \"RecordingActions\"")))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"{endpoint} emitted {offenders.Count} statement(s) joining Segments to a sibling collection, " +
            $"which returns their cartesian product:\n\n{string.Join("\n\n", offenders)}");
    }

    /// <summary>Two speakers and two actions over three segments: enough that a cartesian product is a
    /// different SHAPE from the real data, without needing production volumes to prove it.</summary>
    private async Task<(Guid UserId, Guid RecordingId)> SeedAsync()
    {
        await using var db = fx.CreateDbContext();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = $"{Guid.NewGuid()}@x.test",
        };
        var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = $"k/{Guid.NewGuid()}", Title = "Workshop" };
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "m", Version = 1 };
        db.AddRange(user, rec, tr,
            new Segment { Id = Guid.NewGuid(), TranscriptionId = tr.Id, Ordinal = 0, SpeakerLabel = "SPEAKER_00", Original = "one", StartMs = 0, EndMs = 10 },
            new Segment { Id = Guid.NewGuid(), TranscriptionId = tr.Id, Ordinal = 1, SpeakerLabel = "SPEAKER_01", Original = "two", StartMs = 10, EndMs = 20 },
            new Segment { Id = Guid.NewGuid(), TranscriptionId = tr.Id, Ordinal = 2, SpeakerLabel = "SPEAKER_00", Original = "three", StartMs = 20, EndMs = 30 },
            new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "Ada" },
            new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_01", DisplayName = "Grace" },
            new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "ship it", Ordinal = 0 },
            new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "write it up", Ordinal = 1 });
        await db.SaveChangesAsync();
        return (user.Id, rec.Id);
    }

    /// <summary>A context whose every statement is captured, so the assertions can read the emitted shape.</summary>
    private (DiarizDbContext Db, RecordsSql Sql) Recording()
    {
        var sql = new RecordsSql();
        var options = new DbContextOptionsBuilder<DiarizDbContext>()
            .UseNpgsql(fx.PostgresConnectionString, o => o.UseVector())
            .AddInterceptors(sql)
            .Options;
        return (new DiarizDbContext(options), sql);
    }

    [Fact]
    public async Task Get_ReadsSegmentsSeparatelyFromSpeakersAndActions()
    {
        var (userId, recId) = await SeedAsync();

        var (db, sql) = Recording();
        await using (db)
        {
            var dto = (await Recordings.Build(db, userId).Get(recId)).Value!;
            // The behaviour the shape must not cost: the whole graph still arrives, exactly once each.
            Assert.Equal(3, dto.Current!.Segments.Count);
            Assert.Equal(["Ada", "Grace"], dto.Speakers.Select(s => s.DisplayName).Order());
            Assert.Equal(2, dto.Actions.Count);
        }

        AssertSegmentsAreNotJoinedToSiblings(sql.Statements, "GET /api/recordings/{id}");
    }

    [Fact]
    public async Task EmailTranscript_ReadsSegmentsSeparatelyFromSpeakersAndActions()
    {
        var (userId, recId) = await SeedAsync();

        var (db, sql) = Recording();
        await using (db) await Recordings.Build(db, userId).EmailTranscript(recId);

        AssertSegmentsAreNotJoinedToSiblings(sql.Statements, "POST /api/recordings/{id}/email");
    }

    [Fact]
    public async Task TranscriptDownload_ReadsSegmentsSeparatelyFromSpeakersAndActions()
    {
        var (userId, recId) = await SeedAsync();

        var (db, sql) = Recording();
        await using (db) await Recordings.Build(db, userId).TranscriptTxt(recId);

        AssertSegmentsAreNotJoinedToSiblings(sql.Statements, "GET /api/recordings/{id}/transcript.txt");
    }

    [Fact]
    public async Task Merge_ReadsSegmentsSeparatelyFromSpeakersAndActions()
    {
        var (userId, firstId) = await SeedAsync();
        Guid secondId;
        await using (var db = fx.CreateDbContext())
        {
            var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, BlobKey = $"k/{Guid.NewGuid()}", Title = "Part two" };
            var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "m", Version = 1 };
            db.AddRange(rec, tr,
                new Segment { Id = Guid.NewGuid(), TranscriptionId = tr.Id, Ordinal = 0, SpeakerLabel = "SPEAKER_00", Original = "four", StartMs = 0, EndMs = 10 },
                new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "Alan" },
                new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "follow up", Ordinal = 0 });
            await db.SaveChangesAsync();
            secondId = rec.Id;
        }

        var (db2, sql) = Recording();
        await using (db2) await Recordings.Build(db2, userId).Merge(new MergeRecordingsRequest([firstId, secondId]));

        AssertSegmentsAreNotJoinedToSiblings(sql.Statements, "POST /api/recordings/merge");
    }
}
