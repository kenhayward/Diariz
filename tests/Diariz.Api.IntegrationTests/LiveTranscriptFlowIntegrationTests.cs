using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

/// <summary>
/// The live transcript against real Postgres.
/// <para>
/// The claim the whole design rests on - that the final pass supersedes the provisional one simply by
/// being a higher version - has only ever been asserted against the in-memory provider, which does not
/// translate relational queries faithfully. If it is wrong, a finished meeting shows its half-finished
/// transcript forever, which is the worst outcome this feature could produce.
/// </para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class LiveTranscriptFlowIntegrationTests(ContainersFixture fx)
{
    private static async Task<Recording> SeedLive(DiarizDbContext db)
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"{Guid.NewGuid()}@x.test",
            Email = $"{Guid.NewGuid()}@x.test",
        };
        db.Users.Add(user);
        var rec = new Recording
        {
            Id = Guid.NewGuid(), UserId = user.Id, Title = "Standup", BlobKey = "",
            Status = RecordingStatus.Live, LiveSessionId = Guid.NewGuid(),
        };
        db.Recordings.Add(rec);
        await db.SaveChangesAsync();
        return rec;
    }

    private static Transcription AddTranscription(DiarizDbContext db, Recording rec, int version, bool provisional)
    {
        var tr = new Transcription
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Version = version,
            Model = provisional ? "whisperx-live" : "whisperx-large-v3", IsProvisional = provisional,
        };
        db.Transcriptions.Add(tr);
        return tr;
    }

    private static void AddSegment(DiarizDbContext db, Transcription tr, long startMs, string text,
        int ordinal, int? chunk = null) =>
        db.Segments.Add(new Segment
        {
            Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00",
            StartMs = startMs, EndMs = startMs + 3000, Original = text, Ordinal = ordinal,
            ChunkSequence = chunk,
        });

    [Fact]
    public async Task TheFinalPass_SupersedesTheProvisionalOne_ByVersion()
    {
        await using var db = fx.CreateDbContext();
        var rec = await SeedLive(db);
        var live = AddTranscription(db, rec, version: 1, provisional: true);
        AddSegment(db, live, 0, "half a meeting", 0, chunk: 0);
        await db.SaveChangesAsync();

        // The meeting ends and the normal pipeline writes the next version.
        var final = AddTranscription(db, rec, version: 2, provisional: false);
        AddSegment(db, final, 0, "the whole meeting", 0);
        await db.SaveChangesAsync();

        // What the detail endpoint does: highest version wins, whatever it is.
        var current = await db.Transcriptions
            .Where(t => t.RecordingId == rec.Id)
            .OrderByDescending(t => t.Version)
            .FirstAsync();

        Assert.False(current.IsProvisional);
        Assert.Equal(2, current.Version);

        // And the provisional one is retained rather than deleted, like any older version.
        Assert.Equal(2, await db.Transcriptions.CountAsync(t => t.RecordingId == rec.Id));
    }

    [Fact]
    public async Task WhileTheMeetingRuns_TheProvisionalPassIsTheHighestVersion()
    {
        // The reason search and the MCP reader had to exclude provisional outright rather than merely
        // out-rank it: until the meeting ends, the live pass IS the newest thing there is.
        await using var db = fx.CreateDbContext();
        var rec = await SeedLive(db);
        AddTranscription(db, rec, version: 1, provisional: true);
        await db.SaveChangesAsync();

        var newest = await db.Transcriptions
            .Where(t => t.RecordingId == rec.Id)
            .OrderByDescending(t => t.Version)
            .FirstAsync();

        Assert.True(newest.IsProvisional);
    }

    [Fact]
    public async Task NewestNonProvisional_FindsNothingWhileOnlyALivePassExists()
    {
        // The query the MCP reader runs. It must return nothing rather than the live pass - an
        // assistant reading half a meeting as though it were the record is worse than reading none.
        await using var db = fx.CreateDbContext();
        var rec = await SeedLive(db);
        AddTranscription(db, rec, version: 1, provisional: true);
        await db.SaveChangesAsync();

        var readable = await db.Transcriptions
            .Where(t => t.RecordingId == rec.Id && !t.IsProvisional)
            .OrderByDescending(t => t.Version)
            .FirstOrDefaultAsync();

        Assert.Null(readable);
    }

    [Fact]
    public async Task ChunkSegments_SurviveARedelivery_WithoutDuplicating()
    {
        // The replace-by-chunk rule against a real database, where the delete and the insert are one
        // transaction rather than an in-memory list operation.
        await using var db = fx.CreateDbContext();
        var rec = await SeedLive(db);
        var live = AddTranscription(db, rec, version: 1, provisional: true);
        AddSegment(db, live, 0, "first attempt", 0, chunk: 0);
        await db.SaveChangesAsync();

        var stale = await db.Segments
            .Where(s => s.TranscriptionId == live.Id && s.ChunkSequence == 0)
            .ToListAsync();
        db.Segments.RemoveRange(stale);
        AddSegment(db, live, 0, "redelivered", 0, chunk: 0);
        await db.SaveChangesAsync();

        var segs = await db.Segments.Where(s => s.TranscriptionId == live.Id).ToListAsync();
        Assert.Single(segs);
        Assert.Equal("redelivered", segs[0].Original);
    }

    [Fact]
    public async Task TheOldestUntranscribedChunk_IsWhatLagIsMeasuredFrom()
    {
        // The lag query, ordered and filtered by real Postgres rather than LINQ-to-objects.
        await using var db = fx.CreateDbContext();
        var rec = await SeedLive(db);
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-5);
        for (var i = 0; i < 4; i++)
            db.RecordingChunks.Add(new RecordingChunk
            {
                Id = Guid.NewGuid(), RecordingId = rec.Id, Sequence = i,
                BlobKey = $"u/{rec.Id}/chunks/{i:D5}.webm",
                StartMs = i * 30_000, EndMs = (i + 1) * 30_000, SizeBytes = 1000,
                ReceivedAt = t0.AddSeconds(i * 30),
                // Chunks 0 and 1 came back; 2 and 3 are still outstanding.
                TranscribedAt = i < 2 ? t0.AddSeconds(i * 30 + 5) : null,
            });
        await db.SaveChangesAsync();

        var oldest = await db.RecordingChunks
            .Where(c => c.RecordingId == rec.Id && c.TranscribedAt == null)
            .OrderBy(c => c.ReceivedAt)
            .Select(c => (DateTimeOffset?)c.ReceivedAt)
            .FirstOrDefaultAsync();

        Assert.NotNull(oldest);
        Assert.Equal(t0.AddSeconds(60).ToUnixTimeSeconds(), oldest!.Value.ToUnixTimeSeconds());
    }

    // ---- the raw SQL that search runs ----

    private static TranscriptSearch LexicalSearch(DiarizDbContext db) =>
        new(db, new FakeEmbeddingClient(), new FakeEmbeddingSettingsResolver
        {
            Config = new EmbeddingRequestConfig("", "", "nomic-embed-text", 768, 60, 32),
        }, new RoomScope(db));

    [Fact]
    public async Task Search_DoesNotReturnAProvisionalTranscript()
    {
        // The predicate lives in raw SQL, so nothing above this exercises it. If the join or the column
        // name were wrong the whole of search would break, not just this case - which is exactly why it
        // is worth one real query rather than reasoning.
        await using var db = fx.CreateDbContext();
        var rec = await SeedLive(db);
        await new RoomScope(db).PlaceInMainRoomAsync(rec.Id, rec.UserId, sectionId: null);
        var live = AddTranscription(db, rec, version: 1, provisional: true);
        AddSegment(db, live, 0, "the warehouse integration is slower than expected", 0, chunk: 0);
        await db.SaveChangesAsync();

        var hits = await LexicalSearch(db).SearchAsync(rec.UserId, "warehouse integration", null, null, 20);

        Assert.Empty(hits);
    }

    [Fact]
    public async Task Search_FindsTheFinalTranscript_OnceTheMeetingHasEnded()
    {
        // The companion, and the one that proves the exclusion did not simply break search. It also
        // covers the subtler half: the MAX(Version) subquery has to skip provisional rows too, or the
        // guard would exclude the real transcript whenever a live pass sits above it.
        await using var db = fx.CreateDbContext();
        var rec = await SeedLive(db);
        await new RoomScope(db).PlaceInMainRoomAsync(rec.Id, rec.UserId, sectionId: null);
        var live = AddTranscription(db, rec, version: 2, provisional: true);
        AddSegment(db, live, 0, "the warehouse integration is slower than expected", 0, chunk: 0);
        var final = AddTranscription(db, rec, version: 1, provisional: false);
        AddSegment(db, final, 0, "the warehouse integration is slower than expected", 0);
        await db.SaveChangesAsync();

        var hits = await LexicalSearch(db).SearchAsync(rec.UserId, "warehouse integration", null, null, 20);

        var hit = Assert.Single(hits);
        Assert.Equal(rec.Id, hit.RecordingId);
    }
}
