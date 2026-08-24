using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pgvector;

namespace Diariz.Api.IntegrationTests;

/// <summary>The re-embed callback against real Postgres. The <c>vector(192)</c> column is Ignore'd under
/// the in-memory provider, so the unit tests cannot prove the embedding was actually stored, nor that the
/// person's centroid moved with it.</summary>
[Collection(IntegrationCollection.Name)]
public class VoiceprintCallbackIntegrationTests(ContainersFixture fx)
{
    private const string Secret = "shared-secret";

    private static float[] Unit(float first)
    {
        var v = new float[192];
        v[0] = first;
        v[1] = (float)Math.Sqrt(1 - (first * first));
        return v;
    }

    [Fact]
    public async Task Result_StoresTheVectorAndMovesThePersonsCentroid()
    {
        Guid personId, sampleId;
        Vector before;

        await using (var db = fx.CreateDbContext())
        {
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
            var person = new Person { Id = Guid.NewGuid(), Name = "Alice", SampleCount = 1, Embedding = new Vector(Unit(1f)) };
            var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = "k" };
            var speaker = new Speaker
            {
                Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "Alice",
                PersonId = person.Id, EmbeddingStale = true,
            };
            var sample = new VoiceSample
            {
                Id = Guid.NewGuid(), PersonId = person.Id, SpeakerId = speaker.Id, RecordingId = rec.Id,
                Embedding = new Vector(Unit(1f)),
                SpansJson = VoiceprintSpans.Serialize([new VoiceprintSpan(1000, 3000)]),
                UsedMs = null,
            };
            db.AddRange(user, person, rec, speaker, sample);
            await db.SaveChangesAsync();
            personId = person.Id;
            sampleId = sample.Id;
            before = person.Embedding!;
        }

        await using (var db = fx.CreateDbContext())
        {
            var controller = new WorkerVoiceprintCallbackController(
                db, new PeopleDirectory(db), Options.Create(new WorkerOptions { CallbackSecret = Secret }),
                NullLogger<WorkerVoiceprintCallbackController>.Instance)
            {
                ControllerContext = Http.Context(headers: ("X-Worker-Secret", Secret)),
            };

            // A clearly different unit vector, so a centroid that did not move is unmistakable.
            await controller.Result(new VoiceprintResult(sampleId, Unit(0f), 120000, 200000));
        }

        await using (var read = fx.CreateDbContext())
        {
            var sample = await read.VoiceSamples.SingleAsync(v => v.Id == sampleId);
            Assert.Equal(120000, sample.UsedMs);
            // The vector really round-tripped through pgvector, not just through the change tracker.
            Assert.Equal(0f, sample.Embedding.ToArray()[0], 3);

            var person = await read.People.SingleAsync(p => p.Id == personId);
            Assert.NotEqual(before.ToArray()[0], person.Embedding!.ToArray()[0], 3);

            Assert.False((await read.Speakers.SingleAsync(s => s.PersonId == personId)).EmbeddingStale);
        }
    }
}
