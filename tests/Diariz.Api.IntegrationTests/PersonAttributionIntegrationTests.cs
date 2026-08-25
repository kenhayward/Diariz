using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace Diariz.Api.IntegrationTests;

/// <summary>Attribution state against real Postgres. The in-memory provider Ignores the <c>vector(192)</c>
/// column and does not enforce FKs, so neither the storage nor the timestamptz behaviour can be proven
/// there.</summary>
[Collection(IntegrationCollection.Name)]
public class PersonAttributionIntegrationTests(ContainersFixture fx)
{
    private static Vector Unit()
    {
        var v = new float[192];
        v[0] = 1f;
        return new Vector(v);
    }

    [Fact]
    public async Task ExcludedAt_round_trips_as_a_timestamptz()
    {
        var sampleId = Guid.NewGuid();
        var when = DateTimeOffset.UtcNow;

        await using (var db = fx.CreateDbContext())
        {
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test" };
            var person = new Person { Id = Guid.NewGuid(), Name = "Alice" };
            var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = "k" };
            var speaker = new Speaker
            {
                Id = Guid.NewGuid(),
                RecordingId = rec.Id,
                Label = "SPEAKER_00",
                DisplayName = "Alice",
                PersonId = person.Id,
                Embedding = Unit(),
            };
            db.AddRange(user, person, rec, speaker, new VoiceSample
            {
                Id = sampleId,
                PersonId = person.Id,
                SpeakerId = speaker.Id,
                RecordingId = rec.Id,
                Embedding = Unit(),
                ExcludedAt = when,
            });
            await db.SaveChangesAsync();
        }

        await using var read = fx.CreateDbContext();
        var stored = await read.VoiceSamples.SingleAsync(v => v.Id == sampleId);
        Assert.NotNull(stored.ExcludedAt);
        Assert.Equal(
            when.ToUniversalTime(), stored.ExcludedAt!.Value.ToUniversalTime(), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ExcludedAt_defaults_to_null_so_existing_samples_keep_training()
    {
        // Every sample enrolled before this column existed must keep contributing. A non-null default would
        // silently drop every voiceprint's training set to nothing on deploy.
        var sampleId = Guid.NewGuid();

        await using (var db = fx.CreateDbContext())
        {
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test" };
            var person = new Person { Id = Guid.NewGuid(), Name = "Bob" };
            var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = "k" };
            var speaker = new Speaker
            {
                Id = Guid.NewGuid(),
                RecordingId = rec.Id,
                Label = "SPEAKER_01",
                DisplayName = "Bob",
                PersonId = person.Id,
                Embedding = Unit(),
            };
            db.AddRange(user, person, rec, speaker, new VoiceSample
            {
                Id = sampleId,
                PersonId = person.Id,
                SpeakerId = speaker.Id,
                RecordingId = rec.Id,
                Embedding = Unit(),
            });
            await db.SaveChangesAsync();
        }

        await using var read = fx.CreateDbContext();
        Assert.Null((await read.VoiceSamples.SingleAsync(v => v.Id == sampleId)).ExcludedAt);
    }
}
