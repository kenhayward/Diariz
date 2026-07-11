using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

/// <summary>The semantic (vector) arm and its fusion with the lexical arm, against real pgvector: cosine
/// ranking finds a conceptually-related passage whose keywords don't match the query, results are owner-scoped
/// and scope-filtered, and with embeddings disabled the search degrades to lexical-only.</summary>
[Collection(IntegrationCollection.Name)]
public class HybridSearchIntegrationTests(ContainersFixture fx)
{
    private const int Dim = 768;

    // A 768-d one-hot vector on a chosen axis, so cosine distance between two axes is maximal and between the
    // same axis is 0 - lets a test pin exactly which chunk the query should rank first.
    private static float[] Axis(int axis)
    {
        var v = new float[Dim];
        v[axis] = 1f;
        return v;
    }

    private static TranscriptSearch Search(DiarizDbContext db, float[]? queryVector)
    {
        var resolver = new FakeEmbeddingSettingsResolver
        {
            Config = queryVector is null
                ? new EmbeddingRequestConfig("", "", "m", Dim, 60, 32)                       // disabled
                : new EmbeddingRequestConfig("http://emb.test/v1", "k", "m", Dim, 60, 32),   // enabled
        };
        var client = new FakeEmbeddingClient { Vectors = queryVector is null ? [] : [queryVector] };
        return new TranscriptSearch(db, client, resolver, new RoomScope(db));
    }

    private async Task<(Guid userId, Guid recId, Guid trId)> SeedRecording(Guid? user = null)
    {
        await using var db = fx.CreateDbContext();
        var userId = user ?? Guid.NewGuid();
        if (!await db.Users.AnyAsync(u => u.Id == userId))
        {
            db.Users.Add(new ApplicationUser { Id = userId, UserName = $"{userId}@x.test", Email = $"{userId}@x.test" });
            await db.SaveChangesAsync();
        }
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, Title = "Budget Review", Name = "Budget Review", BlobKey = "k" };
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "m", Version = 1 };
        db.Recordings.Add(rec);
        db.Transcriptions.Add(tr);
        await db.SaveChangesAsync();
        // Search is scoped by room placement now - give the recording its main placement in the owner's room.
        await new RoomScope(db).PlaceInMainRoomAsync(rec.Id, userId, sectionId: null);
        return (userId, rec.Id, tr.Id);
    }

    private async Task AddChunk(Guid userId, Guid recId, Guid trId, int ordinal, string text, float[] embedding)
    {
        await using var db = fx.CreateDbContext();
        db.TranscriptChunks.Add(new TranscriptChunk
        {
            Id = Guid.NewGuid(), TranscriptionId = trId, RecordingId = recId, UserId = userId,
            Ordinal = ordinal, StartMs = ordinal * 10_000, EndMs = ordinal * 10_000 + 9_000,
            SpeakerLabels = "Alice", Text = text, Embedding = new Pgvector.Vector(embedding),
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Search_SemanticArm_RanksConceptuallyClosestChunk_WithoutKeywordOverlap()
    {
        var (userId, recId, trId) = await SeedRecording();
        // Two chunks; neither contains the word "money". The query embedding is aligned with the FIRST chunk's
        // axis, so cosine ranks it top even though it shares no keywords with the query.
        await AddChunk(userId, recId, trId, 0, "We cannot afford this quarter.", Axis(3));
        await AddChunk(userId, recId, trId, 1, "The deployment went smoothly.", Axis(50));

        await using var db = fx.CreateDbContext();
        var hits = await Search(db, Axis(3)).SearchAsync(userId, "money worries", null, null, 20);

        Assert.NotEmpty(hits);
        Assert.Equal("We cannot afford this quarter.", hits[0].Text); // semantic match, no shared keyword
    }

    [Fact]
    public async Task Search_SemanticArm_PrefixesQuery_WithNomicTaskInstruction()
    {
        var (userId, _, _) = await SeedRecording();
        var client = new FakeEmbeddingClient { Vectors = [Axis(1)] };
        var resolver = new FakeEmbeddingSettingsResolver(); // default config carries the nomic prefixes

        await using var db = fx.CreateDbContext();
        await new TranscriptSearch(db, client, resolver, new RoomScope(db)).SearchAsync(userId, "budget worries", null, null, 20);

        var input = Assert.Single(client.LastInputs!);
        Assert.Equal("search_query: budget worries", input); // nomic query prefix applied
    }

    [Fact]
    public async Task Search_SemanticArm_IsOwnerScoped()
    {
        var (owner, recId, trId) = await SeedRecording();
        await AddChunk(owner, recId, trId, 0, "Owner's private passage.", Axis(7));

        await using var db = fx.CreateDbContext();
        var otherUser = Guid.NewGuid();
        var hits = await Search(db, Axis(7)).SearchAsync(otherUser, "anything", null, null, 20);

        Assert.Empty(hits); // another user never sees the owner's chunks
    }

    [Fact]
    public async Task Search_ScopeFilter_RestrictsSemanticArmToGivenRecordings()
    {
        var (userId, recA, trA) = await SeedRecording();
        await AddChunk(userId, recA, trA, 0, "Passage in recording A.", Axis(9));
        // A second recording for the same user, closest to the query - but excluded by the scope filter.
        await using (var db = fx.CreateDbContext())
        {
            var recB = new Recording { Id = Guid.NewGuid(), UserId = userId, Title = "B", Name = "B", BlobKey = "k" };
            var trB = new Transcription { Id = Guid.NewGuid(), RecordingId = recB.Id, Model = "m", Version = 1 };
            db.Recordings.Add(recB);
            db.Transcriptions.Add(trB);
            await db.SaveChangesAsync();
            await AddChunk(userId, recB.Id, trB.Id, 0, "Passage in recording B.", Axis(9));
        }

        await using var verify = fx.CreateDbContext();
        var hits = await Search(verify, Axis(9)).SearchAsync(userId, "passage", null, [recA], 20);

        Assert.All(hits, h => Assert.Equal(recA, h.RecordingId)); // only the scoped recording's chunks
    }

    [Fact]
    public async Task Search_GracefulOff_NoEmbeddingEndpoint_IsLexicalOnly()
    {
        var (userId, recId, trId) = await SeedRecording();
        await AddChunk(userId, recId, trId, 0, "A semantically relevant passage.", Axis(11));
        // Also add a lexical (segment) match so the lexical arm has something to return.
        await using (var db = fx.CreateDbContext())
        {
            db.Speakers.Add(new Speaker { Id = Guid.NewGuid(), RecordingId = recId, Label = "SPEAKER_00", DisplayName = "Alice" });
            db.Segments.Add(new Segment
            {
                Id = Guid.NewGuid(), TranscriptionId = trId, SpeakerLabel = "SPEAKER_00",
                StartMs = 0, EndMs = 900, Original = "We discussed the marketing budget.", Ordinal = 0,
            });
            await db.SaveChangesAsync();
        }

        await using var verify = fx.CreateDbContext();
        // queryVector null → resolver disabled → semantic arm skipped entirely.
        var hits = await Search(verify, null).SearchAsync(userId, "budget", null, null, 20);

        var hit = Assert.Single(hits);
        Assert.Contains("budget", hit.Text);                 // the lexical (segment) hit
        Assert.DoesNotContain(hits, h => h.StartMs == 0 && h.Text.Contains("semantically relevant")); // no chunk hits
    }
}
