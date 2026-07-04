using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diariz.Api.Tests;

public class EmbeddingProcessorTests
{
    private static async Task<(Recording rec, Transcription tr)> Seed(
        DiarizDbContext db, Guid userId, int segmentCount = 3, int version = 1)
    {
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, BlobKey = "k" };
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "whisperx", Version = version };
        db.Recordings.Add(rec);
        db.Transcriptions.Add(tr);
        db.Speakers.Add(new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "Alice" });
        for (var i = 0; i < segmentCount; i++)
            db.Segments.Add(new Segment
            {
                Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00",
                StartMs = i * 1000, EndMs = i * 1000 + 900, Original = $"Line {i} content.", Ordinal = i,
            });
        await db.SaveChangesAsync();
        return (rec, tr);
    }

    [Fact]
    public async Task Process_WritesChunks_WithDenormalizedOwnerAndRecording_AndSpeakerLabels()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, tr) = await Seed(db, userId);
        var client = new FakeEmbeddingClient();
        var resolver = new FakeEmbeddingSettingsResolver();

        await EmbeddingProcessor.ProcessAsync(db, client, resolver, new EmbeddingJob(rec.Id, tr.Id), NullLogger.Instance);

        var chunks = await db.TranscriptChunks.Where(c => c.RecordingId == rec.Id).ToListAsync();
        Assert.NotEmpty(chunks);
        Assert.All(chunks, c =>
        {
            Assert.Equal(userId, c.UserId);
            Assert.Equal(tr.Id, c.TranscriptionId);
            Assert.Contains("Alice", c.SpeakerLabels);   // display name, not the raw SPEAKER_00 label
            Assert.Contains("Alice:", c.Text);
        });
        Assert.Equal(userId, resolver.LastUserId);
        Assert.Equal(chunks.Count, client.LastInputs!.Count); // one embed input per chunk
    }

    [Fact]
    public async Task Process_ReplacesExistingChunks_OnReRun()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, tr) = await Seed(db, userId);
        var client = new FakeEmbeddingClient();
        var resolver = new FakeEmbeddingSettingsResolver();

        await EmbeddingProcessor.ProcessAsync(db, client, resolver, new EmbeddingJob(rec.Id, tr.Id), NullLogger.Instance);
        var firstIds = await db.TranscriptChunks.Where(c => c.RecordingId == rec.Id).Select(c => c.Id).ToListAsync();

        // A re-transcription produces a new transcription version; embedding it must replace the old chunks.
        var tr2 = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "whisperx", Version = 2 };
        db.Transcriptions.Add(tr2);
        db.Segments.Add(new Segment
        {
            Id = Guid.NewGuid(), TranscriptionId = tr2.Id, SpeakerLabel = "SPEAKER_00",
            StartMs = 0, EndMs = 900, Original = "Fresh content.", Ordinal = 0,
        });
        await db.SaveChangesAsync();

        await EmbeddingProcessor.ProcessAsync(db, client, resolver, new EmbeddingJob(rec.Id, tr2.Id), NullLogger.Instance);

        var after = await db.TranscriptChunks.Where(c => c.RecordingId == rec.Id).ToListAsync();
        Assert.All(after, c => Assert.Equal(tr2.Id, c.TranscriptionId)); // all point at the latest transcription
        Assert.DoesNotContain(after, c => firstIds.Contains(c.Id)); // none of the old chunk rows survive
    }

    [Fact]
    public async Task Process_NoOp_WhenEmbeddingDisabled()
    {
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, Guid.NewGuid());
        var client = new FakeEmbeddingClient();
        var resolver = new FakeEmbeddingSettingsResolver
        {
            Config = new EmbeddingRequestConfig("", "", "nomic-embed-text", 768, 60, 32), // no endpoint
        };

        await EmbeddingProcessor.ProcessAsync(db, client, resolver, new EmbeddingJob(rec.Id, tr.Id), NullLogger.Instance);

        Assert.Empty(await db.TranscriptChunks.ToListAsync());
        Assert.Equal(0, client.Calls); // never hit the embedding endpoint
    }

    [Fact]
    public async Task Process_MissingRecording_IsNoOp()
    {
        using var db = TestDb.Create();
        var client = new FakeEmbeddingClient();

        await EmbeddingProcessor.ProcessAsync(db, client, new FakeEmbeddingSettingsResolver(),
            new EmbeddingJob(Guid.NewGuid(), Guid.NewGuid()), NullLogger.Instance);

        Assert.Empty(await db.TranscriptChunks.ToListAsync());
        Assert.Equal(0, client.Calls);
    }
}
