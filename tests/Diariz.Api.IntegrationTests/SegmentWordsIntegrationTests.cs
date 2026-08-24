using Diariz.Api.Contracts;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

/// <summary>The <c>Segment.WordsJson</c> column against real Postgres.
///
/// <para>Postgres reformats jsonb on write - key order and whitespace are not preserved - so a byte
/// comparison of the column text never matches, and the in-memory unit provider stores plain text and
/// hides the difference entirely. These assert on the <em>parsed</em> value.</para></summary>
[Collection(IntegrationCollection.Name)]
public class SegmentWordsIntegrationTests(ContainersFixture fx)
{
    private async Task<Guid> SeedSegmentAsync(string? wordsJson)
    {
        await using var db = fx.CreateDbContext();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
        var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = "k" };
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "m", Version = 1 };
        var seg = new Segment
        {
            Id = Guid.NewGuid(),
            TranscriptionId = tr.Id,
            SpeakerLabel = "SPEAKER_00",
            Original = "Hello world",
            Ordinal = 0,
            WordsJson = wordsJson,
        };
        db.AddRange(user, rec, tr, seg);
        await db.SaveChangesAsync();
        return seg.Id;
    }

    [Fact]
    public async Task WordsJson_SurvivesRealPostgresRoundTrip()
    {
        List<SegmentWord> written = [new("Hello", 1200, 1600), new("world", 1700, 2500)];
        var segmentId = await SeedSegmentAsync(SegmentWords.Serialize(written));

        await using var read = fx.CreateDbContext();
        var stored = await read.Segments.Where(s => s.Id == segmentId).Select(s => s.WordsJson).SingleAsync();

        Assert.Equal(written, SegmentWords.Parse(stored));
    }

    [Fact]
    public async Task WordsJson_IsStoredAsJsonbNotText()
    {
        // If the column were plain text this would still round-trip, and the migration would be silently
        // wrong. jsonb reports its own type, so ask the catalogue rather than trusting the round-trip.
        await using var db = fx.CreateDbContext();
        var type = await db.Database
            .SqlQuery<string>($"""
                SELECT data_type AS "Value" FROM information_schema.columns
                WHERE table_name = 'Segments' AND column_name = 'WordsJson'
                """)
            .SingleAsync();

        Assert.Equal("jsonb", type);
    }

    [Fact]
    public async Task WordsJson_StaysNullForASegmentWithNoWordTimings()
    {
        // Null is the state every pre-existing recording is in and the one the split endpoint refuses on.
        // It has to survive a real write, not just the in-memory provider.
        var segmentId = await SeedSegmentAsync(null);

        await using var read = fx.CreateDbContext();
        var stored = await read.Segments.Where(s => s.Id == segmentId).Select(s => s.WordsJson).SingleAsync();

        Assert.Null(stored);
    }
}
