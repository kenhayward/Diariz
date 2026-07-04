using Diariz.Api.Contracts;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diariz.Api.IntegrationTests;

/// <summary>Exercises the RAG embedding pipeline against real Postgres/pgvector: the <c>vector(768)</c>
/// <c>TranscriptChunk</c> column round-trips, the processor writes non-null vectors, and a re-transcription
/// replaces the recording's chunks (the FK cascade + wholesale replace the in-memory provider can't verify).</summary>
[Collection(IntegrationCollection.Name)]
public class EmbeddingIntegrationTests(ContainersFixture fx)
{
    private const int Dim = 768;

    private static float[] UnitVector(int seed)
    {
        var v = new float[Dim];
        v[seed % Dim] = 1f;
        return v;
    }

    private static FakeEmbeddingSettingsResolver Resolver() => new()
    {
        Config = new EmbeddingRequestConfig("http://emb.test/v1", "k", "nomic-embed-text", Dim, 60, 32),
    };

    private async Task<(Guid userId, Guid recId, Transcription tr)> Seed(int segmentCount, int version)
    {
        await using var db = fx.CreateDbContext();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
        var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = "k" };
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "whisperx", Version = version };
        db.Users.Add(user);
        db.Recordings.Add(rec);
        db.Transcriptions.Add(tr);
        db.Speakers.Add(new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "Alice" });
        for (var i = 0; i < segmentCount; i++)
            db.Segments.Add(new Segment
            {
                Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00",
                StartMs = i * 1000, EndMs = i * 1000 + 900, Original = $"Chunk line number {i}.", Ordinal = i,
            });
        await db.SaveChangesAsync();
        return (user.Id, rec.Id, tr);
    }

    [Fact]
    public async Task Process_WritesNonNull768Vectors_ThatRoundTrip()
    {
        var (userId, recId, tr) = await Seed(segmentCount: 4, version: 1);
        // One 768-d unit vector per chunk (the fake client returns exactly as many as it is asked to embed).
        var client = new FakeEmbeddingClient { Vectors = Enumerable.Range(0, 8).Select(UnitVector).ToArray() };

        await using (var db = fx.CreateDbContext())
            await EmbeddingProcessor.ProcessAsync(db, client, Resolver(), new EmbeddingJob(recId, tr.Id), NullLogger.Instance);

        await using (var verify = fx.CreateDbContext())
        {
            var chunks = await verify.TranscriptChunks.Where(c => c.RecordingId == recId).OrderBy(c => c.Ordinal).ToListAsync();
            Assert.NotEmpty(chunks);
            Assert.All(chunks, c =>
            {
                Assert.NotNull(c.Embedding);                       // vector persisted
                Assert.Equal(Dim, c.Embedding!.ToArray().Length);  // 768-d round-trip
                Assert.Equal(userId, c.UserId);
                Assert.Contains("Alice", c.SpeakerLabels);
            });
        }
    }

    [Fact]
    public async Task Reprocess_ReplacesChunks_AndCascadeDeletesWithTranscription()
    {
        var (_, recId, tr1) = await Seed(segmentCount: 3, version: 1);
        var client = new FakeEmbeddingClient { Vectors = Enumerable.Range(0, 8).Select(UnitVector).ToArray() };

        await using (var db = fx.CreateDbContext())
            await EmbeddingProcessor.ProcessAsync(db, client, Resolver(), new EmbeddingJob(recId, tr1.Id), NullLogger.Instance);

        // A re-transcription: new version + fresh segments, then embed the new transcription.
        Transcription tr2;
        await using (var db = fx.CreateDbContext())
        {
            tr2 = new Transcription { Id = Guid.NewGuid(), RecordingId = recId, Model = "whisperx", Version = 2 };
            db.Transcriptions.Add(tr2);
            db.Segments.Add(new Segment
            {
                Id = Guid.NewGuid(), TranscriptionId = tr2.Id, SpeakerLabel = "SPEAKER_00",
                StartMs = 0, EndMs = 900, Original = "Completely fresh content.", Ordinal = 0,
            });
            await db.SaveChangesAsync();
            await EmbeddingProcessor.ProcessAsync(db, client, Resolver(), new EmbeddingJob(recId, tr2.Id), NullLogger.Instance);
        }

        await using (var verify = fx.CreateDbContext())
        {
            var chunks = await verify.TranscriptChunks.Where(c => c.RecordingId == recId).ToListAsync();
            Assert.NotEmpty(chunks);
            Assert.All(chunks, c => Assert.Equal(tr2.Id, c.TranscriptionId)); // old (v1) chunks are gone
        }

        // Deleting the recording cascades through Transcription → TranscriptChunk.
        await using (var db = fx.CreateDbContext())
        {
            db.Recordings.Remove(await db.Recordings.FirstAsync(r => r.Id == recId));
            await db.SaveChangesAsync();
        }
        await using (var verify = fx.CreateDbContext())
            Assert.Empty(await verify.TranscriptChunks.Where(c => c.RecordingId == recId).ToListAsync());
    }
}
