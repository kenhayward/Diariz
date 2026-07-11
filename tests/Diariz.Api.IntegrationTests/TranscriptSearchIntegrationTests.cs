using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

/// <summary>The pg_trgm-backed lexical arm of transcript search: ownership, current-version filtering, fuzzy
/// phrase matching, speaker filter, and the recording-level <c>contains</c> topic filter. Built with embeddings
/// disabled so the semantic arm is skipped (hybrid ranking is covered in HybridSearchIntegrationTests).</summary>
[Collection(IntegrationCollection.Name)]
public class TranscriptSearchIntegrationTests(ContainersFixture fx)
{
    /// <summary>A lexical-only search service (no embeddings endpoint → semantic arm skipped).</summary>
    private static TranscriptSearch MakeSearch(DiarizDbContext db) =>
        new(db, new FakeEmbeddingClient(), new FakeEmbeddingSettingsResolver
        {
            Config = new EmbeddingRequestConfig("", "", "nomic-embed-text", 768, 60, 32),
        }, new RoomScope(db));

    private static readonly DateTimeOffset June1 = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset June10 = new(2026, 6, 10, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Seeds a recording (current transcription) with (speakerLabel, displayName, text) segments.</summary>
    private async Task<Guid> SeedRecording(
        Guid userId, string name, DateTimeOffset createdAt,
        params (string Label, string Display, string Text)[] segments)
    {
        await using var db = fx.CreateDbContext();
        if (!await db.Users.AnyAsync(u => u.Id == userId))
        {
            db.Users.Add(new ApplicationUser
            {
                Id = userId, UserName = $"{userId}@x.test", Email = $"{userId}@x.test",
            });
            await db.SaveChangesAsync();
        }
        var rec = new Recording
        {
            Id = Guid.NewGuid(), UserId = userId, Title = name, Name = name,
            Source = RecordingSource.Microphone, CreatedAt = createdAt, DurationMs = 60_000,
            Status = RecordingStatus.Transcribed,
        };
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "m", Version = 1 };
        db.Recordings.Add(rec);
        db.Transcriptions.Add(tr);

        var labels = segments.Select(s => (s.Label, s.Display)).Distinct();
        foreach (var (label, display) in labels)
            db.Speakers.Add(new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = label, DisplayName = display });

        var ord = 0;
        foreach (var s in segments)
            db.Segments.Add(new Segment
            {
                Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = s.Label,
                StartMs = ord * 1000, EndMs = ord * 1000 + 900, Original = s.Text, Ordinal = ord++,
            });

        await db.SaveChangesAsync();
        // Search is scoped by room placement now, so give the recording its main placement in the owner's room.
        await new RoomScope(db).PlaceInMainRoomAsync(rec.Id, userId, sectionId: null);
        return rec.Id;
    }

    [Fact]
    public async Task SearchAsync_FuzzyMatchesPhrase_WithSpeakerAndRecording()
    {
        var user = Guid.NewGuid();
        await SeedRecording(user, "Budget Review", June1,
            ("SPEAKER_00", "Alice", "We should cut the marketing budget this quarter."),
            ("SPEAKER_01", "Bob", "I agree with reducing spend."));

        await using var db = fx.CreateDbContext();
        var hits = await MakeSearch(db).SearchAsync(user, "budget", null, null, 20);

        var hit = Assert.Single(hits);
        Assert.Equal("Alice", hit.SpeakerName);
        Assert.Equal("Budget Review", hit.RecordingName);
        Assert.Contains("budget", hit.Text);
        Assert.Equal(June1, hit.RecordingCreatedAt);
    }

    [Fact]
    public async Task SearchAsync_SpeakerFilter_RestrictsToThatPerson()
    {
        var user = Guid.NewGuid();
        await SeedRecording(user, "Planning", June1,
            ("SPEAKER_00", "Alice", "Let's cut the budget."),
            ("SPEAKER_01", "Bob", "We must cut the budget too."));

        await using var db = fx.CreateDbContext();
        var hits = await MakeSearch(db).SearchAsync(user, "cut the budget", "Bob", null, 20);

        var hit = Assert.Single(hits);
        Assert.Equal("Bob", hit.SpeakerName);
    }

    [Fact]
    public async Task SearchAsync_IsScopedToOwner()
    {
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        await SeedRecording(owner, "Secret", June1, ("SPEAKER_00", "Alice", "The password is hunter2."));

        await using var db = fx.CreateDbContext();
        Assert.Empty(await MakeSearch(db).SearchAsync(other, "password", null, null, 20));
    }

    /// <summary>Phase 5: a recording shared into a room the searcher belongs to becomes searchable for them; a
    /// stranger who is in none of its rooms still finds nothing.</summary>
    [Fact]
    public async Task SearchAsync_FindsRecordingsSharedIntoTheSearchersRooms()
    {
        var owner = Guid.NewGuid();
        var member = Guid.NewGuid();
        var recId = await SeedRecording(owner, "Launch", June1, ("SPEAKER_00", "Alice", "The launch date is confirmed."));

        await using (var db = fx.CreateDbContext())
        {
            if (!await db.Users.AnyAsync(u => u.Id == member))
            {
                db.Users.Add(new ApplicationUser { Id = member, UserName = $"{member}@x.test", Email = $"{member}@x.test" });
                await db.SaveChangesAsync();
            }
            var scope = new RoomScope(db);
            var roomId = await scope.CreateSharedRoomAsync("Engineering", null, null, null);
            await scope.SetMemberAsync(roomId, RoomPrincipalType.User, member, RoomPermission.CreateRecording);
            await scope.ShareIntoRoomAsync(recId, roomId, owner, sectionId: null);
        }

        await using var verify = fx.CreateDbContext();
        Assert.NotEmpty(await MakeSearch(verify).SearchAsync(member, "launch date", null, null, 20)); // shared in
        Assert.Empty(await MakeSearch(verify).SearchAsync(Guid.NewGuid(), "launch date", null, null, 20)); // stranger
    }

    [Fact]
    public async Task SearchAsync_OnlySearchesCurrentTranscriptionVersion()
    {
        var user = Guid.NewGuid();
        var recId = await SeedRecording(user, "Versioned", June1,
            ("SPEAKER_00", "Alice", "the magic keyword is pomegranate"));

        // Add a higher version whose segments don't contain the keyword.
        await using (var db = fx.CreateDbContext())
        {
            var tr2 = new Transcription { Id = Guid.NewGuid(), RecordingId = recId, Model = "m", Version = 2 };
            db.Transcriptions.Add(tr2);
            db.Segments.Add(new Segment
            {
                Id = Guid.NewGuid(), TranscriptionId = tr2.Id, SpeakerLabel = "SPEAKER_00",
                StartMs = 0, EndMs = 900, Original = "completely different content", Ordinal = 0,
            });
            await db.SaveChangesAsync();
        }

        await using var verify = fx.CreateDbContext();
        Assert.Empty(await MakeSearch(verify).SearchAsync(user, "pomegranate", null, null, 20));
    }

    [Fact]
    public async Task ListRecordings_ContainsTopic_RanksMatchingRecordings_WithSnippet()
    {
        var user = Guid.NewGuid();
        await SeedRecording(user, "Budget Review", June1,
            ("SPEAKER_00", "Alice", "We should cut the marketing budget this quarter."));
        await SeedRecording(user, "Standup", June10,
            ("SPEAKER_00", "Carol", "Let's talk about the sprint board."));

        await using var db = fx.CreateDbContext();
        var recs = await MakeSearch(db)
            .ListRecordingsAsync(user, null, null, null, null, "budget", 20);

        var rec = Assert.Single(recs);
        Assert.Equal("Budget Review", rec.RecordingName);
        Assert.Contains("budget", rec.BestSnippet!);
        Assert.Contains("Alice", rec.Speakers);
    }

    [Fact]
    public async Task ListRecordings_FiltersBySpeakerAndDate()
    {
        var user = Guid.NewGuid();
        await SeedRecording(user, "Budget Review", June1, ("SPEAKER_00", "Alice", "cut the budget"));
        await SeedRecording(user, "Standup", June10, ("SPEAKER_00", "Carol", "sprint board"));

        await using var db = fx.CreateDbContext();
        var search = MakeSearch(db);

        var bySpeaker = await search.ListRecordingsAsync(user, null, null, null, "Carol", null, 20);
        Assert.Equal("Standup", Assert.Single(bySpeaker).RecordingName);

        var byDate = await search.ListRecordingsAsync(user, June1.AddDays(2), null, null, null, null, 20);
        Assert.Equal("Standup", Assert.Single(byDate).RecordingName);
    }

    [Fact]
    public async Task CountMentionsAsync_ReturnsExactGroupedCount_PastTheOldCap()
    {
        var user = Guid.NewGuid();
        // 25 Alice + 5 Bob mentions of "budget" = 30, well over the previous 20-row cap.
        var segments = Enumerable.Range(0, 25).Select(_ => ("SPEAKER_00", "Alice", "the budget again"))
            .Concat(Enumerable.Range(0, 5).Select(_ => ("SPEAKER_01", "Bob", "the budget too")))
            .ToArray();
        await SeedRecording(user, "Budget Review", June1, segments);

        await using var db = fx.CreateDbContext();
        var counts = await MakeSearch(db).CountMentionsAsync(user, "budget", null, null);

        Assert.Equal(30, counts.Sum(c => c.Count)); // exact - not capped at 20
        Assert.Equal(25, counts.Single(c => c.Speaker == "Alice").Count);
        Assert.Equal(5, counts.Single(c => c.Speaker == "Bob").Count);
    }

    [Fact]
    public async Task SpeakerTalkTimeAsync_SumsAcrossAllRecordings()
    {
        var user = Guid.NewGuid();
        // Each seeded segment is 900ms. Alice: 2 + 1 across two recordings = 2700ms; Bob: 1 = 900ms.
        await SeedRecording(user, "M1", June1,
            ("SPEAKER_00", "Alice", "one"), ("SPEAKER_00", "Alice", "two"), ("SPEAKER_01", "Bob", "hi"));
        await SeedRecording(user, "M2", June10, ("SPEAKER_00", "Alice", "three"));

        await using var db = fx.CreateDbContext();
        var totals = await MakeSearch(db).SpeakerTalkTimeAsync(user, null);

        Assert.Equal(2700, totals.Single(t => t.Speaker == "Alice").Ms); // summed over BOTH recordings
        Assert.Equal(900, totals.Single(t => t.Speaker == "Bob").Ms);
        Assert.Equal("Alice", totals[0].Speaker); // ordered by duration desc
    }
}
