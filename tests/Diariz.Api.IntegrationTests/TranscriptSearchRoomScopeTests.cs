using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

/// <summary>The one part of the new search endpoint that touches raw Postgres: narrowing
/// <see cref="TranscriptSearch.SearchAsync"/> to a single room. Everything else the controller decides is
/// unit-tested against the in-memory provider - this is here because the room gate lives inside the pg_trgm
/// SQL (<c>rr."RoomId" = ANY(@roomIds)</c>) and cannot be exercised without a real database.
///
/// The narrowing is an *intersection* of the caller's rooms, never a replacement, so it must fail closed for a
/// room the caller isn't in. That is the security-relevant case below.</summary>
[Collection(IntegrationCollection.Name)]
public class TranscriptSearchRoomScopeTests(ContainersFixture fx)
{
    /// <summary>Lexical-only (no embeddings endpoint), so the semantic arm is skipped and the assertions are
    /// about the room gate rather than ranking.</summary>
    private static TranscriptSearch MakeSearch(DiarizDbContext db) =>
        new(db, new FakeEmbeddingClient(), new FakeEmbeddingSettingsResolver
        {
            Config = new EmbeddingRequestConfig("", "", "nomic-embed-text", 768, 60, 32),
        }, new RoomScope(db));

    private static readonly DateTimeOffset When = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    private async Task<Guid> SeedUser(Guid userId)
    {
        await using var db = fx.CreateDbContext();
        if (!await db.Users.AnyAsync(u => u.Id == userId))
        {
            db.Users.Add(new ApplicationUser { Id = userId, UserName = $"{userId}@x.test", Email = $"{userId}@x.test" });
            await db.SaveChangesAsync();
        }
        return await new RoomScope(db).PersonalRoomIdAsync(userId);
    }

    /// <summary>A transcribed recording whose only placement is the given room.</summary>
    private async Task<Guid> SeedRecordingInRoom(Guid userId, Guid roomId, string name, string text)
    {
        await using var db = fx.CreateDbContext();
        var rec = new Recording
        {
            Id = Guid.NewGuid(), UserId = userId, Title = name, Name = name,
            Source = RecordingSource.Microphone, CreatedAt = When, DurationMs = 60_000,
            Status = RecordingStatus.Transcribed,
        };
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "m", Version = 1 };
        db.Recordings.Add(rec);
        db.Transcriptions.Add(tr);
        db.Speakers.Add(new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "Alice" });
        db.Segments.Add(new Segment
        {
            Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00",
            StartMs = 0, EndMs = 900, Original = text, Ordinal = 0,
        });
        db.RoomRecordings.Add(new RoomRecording { RoomId = roomId, RecordingId = rec.Id });
        await db.SaveChangesAsync();
        return rec.Id;
    }

    /// <summary>A shared room the user is a member of.</summary>
    private async Task<Guid> SeedSharedRoom(Guid userId, string name)
    {
        await using var db = fx.CreateDbContext();
        var scope = new RoomScope(db);
        var roomId = await scope.CreateSharedRoomAsync(name, null, null, null);
        await scope.SetMemberAsync(roomId, RoomPrincipalType.User, userId, RoomPermission.ManageContents);
        return roomId;
    }

    [Fact]
    public async Task Search_WithoutRoomId_SpansEveryRoomTheCallerCanSee()
    {
        var user = Guid.NewGuid();
        var personal = await SeedUser(user);
        var shared = await SeedSharedRoom(user, $"Shared {Guid.NewGuid():N}");
        await SeedRecordingInRoom(user, personal, "Personal one", "the quarterly budget review");
        await SeedRecordingInRoom(user, shared, "Shared one", "the quarterly budget review");

        await using var db = fx.CreateDbContext();
        var hits = await MakeSearch(db).SearchAsync(user, "quarterly budget", null, null, 20);

        Assert.Equal(2, hits.Select(h => h.RecordingId).Distinct().Count());
    }

    [Fact]
    public async Task Search_WithRoomId_ReturnsOnlyThatRoomsRecordings()
    {
        var user = Guid.NewGuid();
        var personal = await SeedUser(user);
        var shared = await SeedSharedRoom(user, $"Shared {Guid.NewGuid():N}");
        var inPersonal = await SeedRecordingInRoom(user, personal, "Personal two", "the annual roadmap plan");
        var inShared = await SeedRecordingInRoom(user, shared, "Shared two", "the annual roadmap plan");

        await using var db = fx.CreateDbContext();
        var search = MakeSearch(db);

        var personalHits = await search.SearchAsync(user, "annual roadmap", null, null, 20, roomId: personal);
        Assert.Contains(inPersonal, personalHits.Select(h => h.RecordingId));
        Assert.DoesNotContain(inShared, personalHits.Select(h => h.RecordingId));

        var sharedHits = await search.SearchAsync(user, "annual roadmap", null, null, 20, roomId: shared);
        Assert.Contains(inShared, sharedHits.Select(h => h.RecordingId));
        Assert.DoesNotContain(inPersonal, sharedHits.Select(h => h.RecordingId));
    }

    /// The security case: passing a room you are not a member of must return nothing, not that room's contents.
    /// The narrowing intersects the caller's own rooms, so a foreign id leaves an empty set and matches nothing.
    [Fact]
    public async Task Search_WithARoomTheCallerIsNotIn_ReturnsEmpty_NotThatRoomsContents()
    {
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        await SeedUser(mine);
        var theirRoom = await SeedUser(theirs);
        await SeedRecordingInRoom(theirs, theirRoom, "Their secret", "the confidential merger terms");

        await using var db = fx.CreateDbContext();
        var hits = await MakeSearch(db).SearchAsync(mine, "confidential merger", null, null, 20, roomId: theirRoom);

        Assert.Empty(hits);
    }

    /// A non-member room id must not be an error either - the caller learns nothing about whether it exists.
    [Fact]
    public async Task Search_WithAnUnknownRoomId_ReturnsEmpty_WithoutThrowing()
    {
        var user = Guid.NewGuid();
        await SeedUser(user);
        await using var db = fx.CreateDbContext();

        var hits = await MakeSearch(db).SearchAsync(user, "anything", null, null, 20, roomId: Guid.NewGuid());
        Assert.Empty(hits);
    }
}
