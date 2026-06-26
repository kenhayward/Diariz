using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Options;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Pgvector;

namespace Diariz.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public class DatabaseIntegrationTests(ContainersFixture fx)
{
    private async Task<ApplicationUser> SeedUser()
    {
        await using var db = fx.CreateDbContext();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task Migrations_Apply_AndVectorColumnRoundTrips()
    {
        var user = await SeedUser();
        var segId = Guid.NewGuid();

        await using (var db = fx.CreateDbContext())
        {
            var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = "k" };
            var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "m", Version = 1 };
            var seg = new Segment
            {
                Id = segId,
                TranscriptionId = tr.Id,
                SpeakerLabel = "SPEAKER_00",
                Text = "hello",
                Ordinal = 0,
                Embedding = new Vector(Enumerable.Range(0, 768).Select(i => (float)i).ToArray())
            };
            db.AddRange(rec, tr, seg);
            await db.SaveChangesAsync();
        }

        // Re-read on a fresh context to prove the vector(768) column persisted and maps back.
        await using var db2 = fx.CreateDbContext();
        var loaded = await db2.Segments.SingleAsync(s => s.Id == segId);
        Assert.NotNull(loaded.Embedding);
        Assert.Equal(768, loaded.Embedding!.ToArray().Length);
        Assert.Equal(5f, loaded.Embedding.ToArray()[5]);
    }

    [Fact]
    public async Task ForeignKey_RejectsRecordingWithUnknownUser()
    {
        await using var db = fx.CreateDbContext();
        db.Recordings.Add(new Recording { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), BlobKey = "k" });

        // Real Postgres enforces the FK to AspNetUsers — the in-memory provider does not.
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Get_ReturnsOnlyHighestVersionTranscription()
    {
        var user = await SeedUser();
        var recId = Guid.NewGuid();

        await using (var seed = fx.CreateDbContext())
        {
            seed.Recordings.Add(new Recording { Id = recId, UserId = user.Id, BlobKey = "k" });
            foreach (var v in new[] { 1, 2, 3 })
                seed.Transcriptions.Add(new Transcription { Id = Guid.NewGuid(), RecordingId = recId, Model = "m", Version = v });
            await seed.SaveChangesAsync();
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Transcription:DefaultModel"] = "whisperx-large-v3" })
            .Build();

        await using var db = fx.CreateDbContext();
        var controller = new RecordingsController(db, new FakeAudioStorage(), new FakeJobQueue(), new FakeHubContext(), config,
            Options.Create(new SummarizationOptions()))
        {
            ControllerContext = Http.Context(user.Id)
        };

        var result = await controller.Get(recId);

        // The filtered Include (OrderByDescending(Version).Take(1)) is translated by Postgres here,
        // unlike the in-memory provider where this assertion cannot hold.
        var dto = Assert.IsType<RecordingDetailDto>(result.Value);
        Assert.Equal(3, dto.Current!.Version);
    }
}
