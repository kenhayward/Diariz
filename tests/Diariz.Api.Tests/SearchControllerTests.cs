using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;

namespace Diariz.Api.Tests;

/// Everything the *controller* decides: validation, the limit clamp, resolving a folder scope to recording
/// ids, folder hits, breadcrumbs, and collapsing many segment hits to one per recording. The search engine
/// itself is raw Postgres SQL and is faked here - its behaviour is verified in the integration project.
public class SearchControllerTests
{
    private static readonly DateTimeOffset When = new(2026, 6, 26, 12, 0, 0, TimeSpan.Zero);

    private static SearchController Build(DiarizDbContext db, Guid userId, ITranscriptSearch search)
    {
        if (db.Users.Find(userId) is null)
        {
            db.Users.Add(new ApplicationUser { Id = userId, UserName = $"{userId}@x.test", Email = $"{userId}@x.test" });
            db.SaveChanges();
        }
        return new(db, new RoomScope(db), search) { ControllerContext = Http.Context(userId) };
    }

    private static TranscriptHit Hit(Guid recordingId, string name, long startMs, double sim, string text = "hello") =>
        new(recordingId, name, When, startMs, "Alice", text, sim);

    /// A recording placed in the caller's personal room, optionally filed in a section. Filing lives on the
    /// *placement* (RoomRecording), not the recording: the same recording can sit in a folder in one room and
    /// be ungrouped in another.
    private static async Task<Recording> SeedRecording(
        DiarizDbContext db, Guid userId, Guid? sectionId = null, string name = "Rec")
    {
        var roomId = await new RoomScope(db).PersonalRoomIdAsync(userId);
        var r = new Recording
        {
            Id = Guid.NewGuid(), UserId = userId, Title = name, Name = name,
            Source = RecordingSource.Upload, CreatedAt = When, DurationMs = 1000,
        };
        db.Recordings.Add(r);
        db.RoomRecordings.Add(new RoomRecording { RoomId = roomId, RecordingId = r.Id, SectionId = sectionId });
        await db.SaveChangesAsync();
        return r;
    }

    private static async Task<Section> SeedSection(
        DiarizDbContext db, Guid userId, string name, Guid? parentId = null, Guid? roomId = null)
    {
        var room = roomId ?? await new RoomScope(db).PersonalRoomIdAsync(userId);
        var s = new Section { Id = Guid.NewGuid(), UserId = userId, RoomId = room, Name = name, ParentId = parentId };
        db.Sections.Add(s);
        await db.SaveChangesAsync();
        return s;
    }

    [Fact]
    public async Task Search_WithEmptyQuery_ReturnsBadRequest()
    {
        using var db = TestDb.Create();
        var user = Guid.NewGuid();
        var c = Build(db, user, new FakeTranscriptSearch());

        Assert.IsType<BadRequestObjectResult>((await c.Search("  ")).Result);
    }

    [Fact]
    public async Task Search_ClampsLimitToMax_AndDefaultsWhenAbsent()
    {
        using var db = TestDb.Create();
        var user = Guid.NewGuid();
        var fake = new FakeTranscriptSearch();
        var c = Build(db, user, fake);

        await c.Search("hi", limit: 999);
        Assert.Equal(TranscriptSearch.MaxLimit, fake.LastSearch!.Value.Limit);

        await c.Search("hi");
        Assert.Equal(20, fake.LastSearch!.Value.Limit);
    }

    [Fact]
    public async Task Search_WithSectionId_ScopesToThatSectionAndItsSubsections()
    {
        using var db = TestDb.Create();
        var user = Guid.NewGuid();
        Build(db, user, new FakeTranscriptSearch()); // ensure the user exists first
        var parent = await SeedSection(db, user, "Customers");
        var child = await SeedSection(db, user, "Ambu", parent.Id);
        var inParent = await SeedRecording(db, user, parent.Id);
        var inChild = await SeedRecording(db, user, child.Id);
        var elsewhere = await SeedRecording(db, user); // ungrouped - must not be in scope

        var fake = new FakeTranscriptSearch();
        var c = Build(db, user, fake);
        await c.Search("hi", sectionId: parent.Id);

        Assert.NotNull(fake.LastSearch!.Value.Scope);
        Assert.Contains(inParent.Id, fake.LastSearch!.Value.Scope!);
        Assert.Contains(inChild.Id, fake.LastSearch!.Value.Scope!); // the subtree, not just the folder itself
        Assert.DoesNotContain(elsewhere.Id, fake.LastSearch!.Value.Scope!);
    }

    /// The fail-open trap: TranscriptSearch treats an *empty* recordingScope as "unscoped", so a folder with
    /// no recordings must short-circuit here rather than quietly searching the whole library.
    [Fact]
    public async Task Search_InEmptySection_ReturnsNothing_WithoutSearchingEverything()
    {
        using var db = TestDb.Create();
        var user = Guid.NewGuid();
        Build(db, user, new FakeTranscriptSearch());
        var empty = await SeedSection(db, user, "Empty");
        await SeedRecording(db, user); // a recording exists, but not in that folder

        var fake = new FakeTranscriptSearch { Hits = { Hit(Guid.NewGuid(), "Nope", 0, 0.9) } };
        var c = Build(db, user, fake);
        var res = await c.Search("hi", sectionId: empty.Id);

        Assert.Empty(res.Value!.Recordings);
        Assert.Equal(0, fake.SearchCalls); // never reached the engine at all
    }

    [Fact]
    public async Task Search_WithUnknownSection_ReturnsNothing()
    {
        using var db = TestDb.Create();
        var user = Guid.NewGuid();
        var fake = new FakeTranscriptSearch();
        var c = Build(db, user, fake);

        var res = await c.Search("hi", sectionId: Guid.NewGuid());
        Assert.Empty(res.Value!.Recordings);
        Assert.Equal(0, fake.SearchCalls);
    }

    [Fact]
    public async Task Search_MatchesFolderNames_CaseInsensitively()
    {
        using var db = TestDb.Create();
        var user = Guid.NewGuid();
        Build(db, user, new FakeTranscriptSearch());
        var parent = await SeedSection(db, user, "Customers");
        await SeedSection(db, user, "Ambu", parent.Id);

        var c = Build(db, user, new FakeTranscriptSearch());
        var res = await c.Search("custom");

        var folder = Assert.Single(res.Value!.Folders);
        Assert.Equal("Customers", folder.Name);
    }

    [Fact]
    public async Task Search_FolderHit_CarriesItsAncestorBreadcrumb()
    {
        using var db = TestDb.Create();
        var user = Guid.NewGuid();
        Build(db, user, new FakeTranscriptSearch());
        var parent = await SeedSection(db, user, "Customers");
        await SeedSection(db, user, "Ambu", parent.Id);

        var c = Build(db, user, new FakeTranscriptSearch());
        var res = await c.Search("ambu");

        var folder = Assert.Single(res.Value!.Folders);
        Assert.Equal(["Customers"], folder.Breadcrumb);
    }

    [Fact]
    public async Task Search_DoesNotLeakFoldersFromRoomsTheCallerCannotSee()
    {
        using var db = TestDb.Create();
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        Build(db, mine, new FakeTranscriptSearch());
        Build(db, theirs, new FakeTranscriptSearch());
        await SeedSection(db, theirs, "Secret Customers"); // in *their* personal room

        var c = Build(db, mine, new FakeTranscriptSearch());
        var res = await c.Search("customers");

        Assert.Empty(res.Value!.Folders);
    }

    /// Folders are per-room, so a placement's folder should always belong to that placement's room. If it ever
    /// doesn't, the name belongs to a room the caller may not be in and must not be rendered.
    [Fact]
    public async Task Search_SuppressesSectionNameWhenTheFolderIsNotFromThatPlacementsRoom()
    {
        using var db = TestDb.Create();
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        Build(db, mine, new FakeTranscriptSearch());
        Build(db, theirs, new FakeTranscriptSearch());

        var hidden = await SeedSection(db, theirs, "Secret Folder"); // lives in *their* personal room
        var rec = await SeedRecording(db, mine, hidden.Id, "Shared in"); // ...but filed on my placement

        var fake = new FakeTranscriptSearch { Hits = { Hit(rec.Id, "Shared in", 0, 0.9) } };
        var c = Build(db, mine, fake);
        var res = await c.Search("hello");

        var hit = Assert.Single(res.Value!.Recordings);
        Assert.Null(hit.SectionName);
        Assert.Empty(hit.Breadcrumb);
    }

    /// The everyday case the one above guards: a recording filed in my own folder does report it.
    [Fact]
    public async Task Search_ReportsTheFolderARecordingIsFiledIn()
    {
        using var db = TestDb.Create();
        var user = Guid.NewGuid();
        Build(db, user, new FakeTranscriptSearch());
        var parent = await SeedSection(db, user, "Customers");
        var child = await SeedSection(db, user, "Ambu", parent.Id);
        var rec = await SeedRecording(db, user, child.Id, "Account review");

        var fake = new FakeTranscriptSearch { Hits = { Hit(rec.Id, "Account review", 0, 0.9) } };
        var c = Build(db, user, fake);
        var res = await c.Search("hello");

        var hit = Assert.Single(res.Value!.Recordings);
        Assert.Equal("Ambu", hit.SectionName);
        Assert.Equal(["Customers", "Ambu"], hit.Breadcrumb);
    }

    [Fact]
    public async Task Search_CollapsesManySegmentHitsToOnePerRecording_KeepingTheBest()
    {
        using var db = TestDb.Create();
        var user = Guid.NewGuid();
        Build(db, user, new FakeTranscriptSearch());
        var rec = await SeedRecording(db, user, name: "Only one");

        var fake = new FakeTranscriptSearch
        {
            Hits = { Hit(rec.Id, "Only one", 1000, 0.4, "weaker match"), Hit(rec.Id, "Only one", 5000, 0.9, "stronger match") },
        };
        var c = Build(db, user, fake);
        var res = await c.Search("match");

        var hit = Assert.Single(res.Value!.Recordings);
        Assert.Equal("stronger match", hit.Snippet);
        Assert.Equal(5000, hit.SnippetStartMs);
    }

    [Fact]
    public async Task Search_ReportsItsScope()
    {
        using var db = TestDb.Create();
        var user = Guid.NewGuid();
        Build(db, user, new FakeTranscriptSearch());
        var section = await SeedSection(db, user, "F");
        await SeedRecording(db, user, section.Id);
        var roomId = await new RoomScope(db).PersonalRoomIdAsync(user);

        var c = Build(db, user, new FakeTranscriptSearch());
        Assert.Equal("folder", (await c.Search("x", sectionId: section.Id)).Value!.Scope);
        Assert.Equal("room", (await c.Search("x", roomId: roomId)).Value!.Scope);
        Assert.Equal("everywhere", (await c.Search("x", everywhere: true)).Value!.Scope);
        Assert.Equal("everywhere", (await c.Search("x")).Value!.Scope);
    }

    /// `everywhere` wins outright - the server rule stays dead simple rather than intersecting scopes.
    [Fact]
    public async Task Search_Everywhere_IgnoresRoomAndSectionScope()
    {
        using var db = TestDb.Create();
        var user = Guid.NewGuid();
        Build(db, user, new FakeTranscriptSearch());
        var section = await SeedSection(db, user, "F");
        await SeedRecording(db, user, section.Id);

        var fake = new FakeTranscriptSearch();
        var c = Build(db, user, fake);
        await c.Search("x", roomId: Guid.NewGuid(), sectionId: section.Id, everywhere: true);

        Assert.Null(fake.LastSearch!.Value.Scope);
        Assert.Null(fake.LastSearch!.Value.RoomId);
    }

    [Fact]
    public async Task Search_PassesRoomAndSpeakerThrough()
    {
        using var db = TestDb.Create();
        var user = Guid.NewGuid();
        var fake = new FakeTranscriptSearch();
        var c = Build(db, user, fake);
        var roomId = await new RoomScope(db).PersonalRoomIdAsync(user);

        await c.Search("x", roomId: roomId, speaker: "Alice");

        Assert.Equal(roomId, fake.LastSearch!.Value.RoomId);
        Assert.Equal("Alice", fake.LastSearch!.Value.Speaker);
    }

    [Fact]
    public async Task Search_FiltersByDateRange()
    {
        using var db = TestDb.Create();
        var user = Guid.NewGuid();
        Build(db, user, new FakeTranscriptSearch());
        var rec = await SeedRecording(db, user, name: "Old one");

        var fake = new FakeTranscriptSearch { Hits = { Hit(rec.Id, "Old one", 0, 0.9) } };
        var c = Build(db, user, fake);

        // The hit is at 2026-06-26; a window starting after it must drop it.
        var res = await c.Search("hello", from: When.AddDays(1));
        Assert.Empty(res.Value!.Recordings);

        var res2 = await c.Search("hello", from: When.AddDays(-1), to: When.AddDays(1));
        Assert.Single(res2.Value!.Recordings);
    }
}
