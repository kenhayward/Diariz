using Diariz.Api.Contracts;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace Diariz.Api.IntegrationTests;

/// <summary>The <c>VoiceSample.SpansJson</c> / <c>UsedMs</c> columns against real Postgres. The unit
/// provider stores jsonb as plain text and ignores the vector column entirely, so neither the reformatting
/// nor the nullability is observable there.</summary>
[Collection(IntegrationCollection.Name)]
public class VoiceprintSpansIntegrationTests(ContainersFixture fx)
{
    private async Task<Guid> SeedSampleAsync(string? spansJson, int? usedMs)
    {
        await using var db = fx.CreateDbContext();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
        var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = "k" };
        var person = new Person { Id = Guid.NewGuid(), Name = "Ada", CreatedByUserId = user.Id };
        var speaker = new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "Ada" };
        var sample = new VoiceSample
        {
            Id = Guid.NewGuid(),
            PersonId = person.Id,
            SpeakerId = speaker.Id,
            RecordingId = rec.Id,
            Embedding = new Vector(Enumerable.Repeat(0.1f, 192).ToArray()),
            SpansJson = spansJson,
            UsedMs = usedMs,
        };
        db.AddRange(user, rec, person, speaker, sample);
        await db.SaveChangesAsync();
        return sample.Id;
    }

    [Fact]
    public async Task SpansJson_SurvivesRealPostgresRoundTrip()
    {
        // Postgres reformats jsonb on write, so byte-comparing the column text never matches. Compare the
        // parsed value.
        List<VoiceprintSpan> written = [new(1000, 2000), new(5000, 6500)];
        var sampleId = await SeedSampleAsync(VoiceprintSpans.Serialize(written), usedMs: 6500);

        await using var read = fx.CreateDbContext();
        var stored = await read.VoiceSamples.Where(v => v.Id == sampleId)
            .Select(v => v.SpansJson).SingleAsync();

        Assert.Equal(written, VoiceprintSpans.Parse(stored));
    }

    [Fact]
    public async Task SpansJson_IsStoredAsJsonbNotText()
    {
        await using var db = fx.CreateDbContext();
        var type = await db.Database
            .SqlQuery<string>($"""
                SELECT data_type AS "Value" FROM information_schema.columns
                WHERE table_name = 'ProfileContributions' AND column_name = 'SpansJson'
                """)
            .SingleAsync();

        Assert.Equal("jsonb", type);
    }

    [Fact]
    public async Task ASampleWithNoSelection_StoresNullMeaningTheWholeSpeaker()
    {
        // Null is what every sample enrolled before selection existed carries, and what the migration left
        // them at. If it could not survive a write, every pre-existing voiceprint would change meaning.
        var sampleId = await SeedSampleAsync(spansJson: null, usedMs: null);

        await using var read = fx.CreateDbContext();
        var sample = await read.VoiceSamples.SingleAsync(v => v.Id == sampleId);

        Assert.Null(sample.SpansJson);
        Assert.Empty(VoiceprintSpans.Parse(sample.SpansJson));
        Assert.Null(sample.UsedMs);
    }
}
