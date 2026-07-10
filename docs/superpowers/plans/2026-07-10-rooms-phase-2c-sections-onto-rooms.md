# Rooms Phase 2c: Sections onto Rooms Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Folders belong to a **room**, not a user. `Section.UserId` becomes `Section.RoomId`; every query that scoped folders by user now scopes by the caller's personal room. The API behaves identically, because the personal room is the only room.

**Architecture:** Add `Section.RoomId` (FK → `Rooms`, cascade), backfill from each section owner's personal room, migrate every `s.UserId == UserId` call site to `s.RoomId == personalRoomId`, then drop `Section.UserId`. Fold in the 2b obligation: a placement's `SectionId` must name a folder in the placement's room (checkable now that sections carry `RoomId`).

**Preceded by:** 2a (`Room`/`RoomScope`), 2b (`RoomRecording`; `Recording.SectionId` dropped). On this accumulation branch, 2c → 2d → 3 → 4 → 5 land as one PR.

---

## Scope

`Section.UserId` (the entity property) is read at ~47 sites across 7 files: `SectionsController` (12), `SectionPageController` (15), `SectionSummaryProcessor` (7), `SectionMinutesProcessor` (5), `SectionAttachmentsController` (4), `RecordingsController` (2), `ChatController` (2). Every one is "the caller's own folders" and becomes "folders in the caller's personal room".

**Kept green throughout:** `Section.UserId` and `RoomId` coexist until the final task; call sites migrate while both exist.

## Constraints

1. TDD; failing test first.
2. No behaviour change. Existing tests change only for the `UserId`→`RoomId` fixture relocation, never for a moved assertion.
3. Build `Diariz.slnx`. Postgres-only config behind `Database.IsNpgsql()`.
4. Every commit builds and is green.

---

## Task 1: `Section.RoomId` (alongside `UserId`)

**Files:** `src/Diariz.Domain/Entities/Section.cs`, `DiarizDbContext.cs`; test `tests/Diariz.Api.Tests/SectionRoomModelTests.cs`

- [ ] **Step 1** — Failing test: a `Section` with a `RoomId` round-trips.

```csharp
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

public class SectionRoomModelTests
{
    [Fact]
    public async Task Section_CarriesARoomId()
    {
        using var db = TestDb.Create();
        var roomId = Guid.NewGuid();
        db.Sections.Add(new Section { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), RoomId = roomId, Name = "F" });
        await db.SaveChangesAsync();
        Assert.Equal(roomId, db.Sections.Single().RoomId);
    }
}
```

- [ ] **Step 2** — Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~SectionRoomModelTests"` → FAIL (no `RoomId`).

- [ ] **Step 3** — Add to `Section.cs`, after `public ApplicationUser? User`:

```csharp
    /// <summary>The room this folder belongs to. Every folder lives in exactly one room; a user's folders live
    /// in their personal room. Replaces the old per-user ownership.</summary>
    public Guid RoomId { get; set; }
    public Room? Room { get; set; }
```

In `DiarizDbContext.cs`, inside `builder.Entity<Section>(e => {...})`, add:

```csharp
            e.HasIndex(s => new { s.RoomId, s.Name });
            e.HasOne(s => s.Room).WithMany()
                .HasForeignKey(s => s.RoomId).OnDelete(DeleteBehavior.Cascade);
```

- [ ] **Step 4** — Run the filter → PASS; then `dotnet test tests/Diariz.Api.Tests`.
- [ ] **Step 5** — Commit `feat(domain): add Section.RoomId alongside UserId`.

---

## Task 2: Migration + backfill

**Files:** `src/Diariz.Domain/Migrations/SectionRoomBackfill.cs` + generated migration; test appended to `tests/Diariz.Api.IntegrationTests/RoomsIntegrationTests.cs`

The backfill copies each section into its owner's personal room, minting a missing personal room first (same trap as 2b). One-way → in the migration.

- [ ] **Step 1** — Failing integration test:

```csharp
    [Fact]
    public async Task SectionBackfill_PutsEachSectionInItsOwnersPersonalRoom()
    {
        await using var db = fx.CreateDbContext();
        var userId = await NewUserAsync(db, "Ada");
        var sectionId = Guid.NewGuid();
        db.Sections.Add(new Section { Id = sectionId, UserId = userId, RoomId = Guid.Empty, Name = "F" });
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync(SectionRoomBackfill.Sql);
        await db.Database.ExecuteSqlRawAsync(SectionRoomBackfill.Sql); // idempotent

        var room = await db.Rooms.Where(r => r.OwnerUserId == userId).Select(r => r.Id).SingleAsync();
        Assert.Equal(room, (await db.Sections.FindAsync(sectionId))!.RoomId);
    }
```

> `NewUserAsync` already exists in that file. `RoomId = Guid.Empty` seeds a section pre-backfill; the backfill overwrites it.

- [ ] **Step 2** — Run → FAIL (`SectionRoomBackfill` missing, then pending-model-changes).

- [ ] **Step 3** — `src/Diariz.Domain/Migrations/SectionRoomBackfill.cs`:

```csharp
namespace Diariz.Domain.Migrations;

/// <summary>Puts every section in its owner's personal room. Mints a missing personal room first (a user
/// created between phases has none). One-way, so it lives in the migration - not the seeder. Idempotent.</summary>
public static class SectionRoomBackfill
{
    public const string Sql = PersonalRoomBackfill.Sql + """

        UPDATE "Sections" s
        SET "RoomId" = r."Id"
        FROM "Rooms" r
        WHERE r."OwnerUserId" = s."UserId" AND r."Kind" = 0;
        """;
}
```

Then `dotnet ef migrations add AddSectionRoomId --project src/Diariz.Domain --startup-project src/Diariz.Api`. Confirm it adds `Sections.RoomId` (NOT NULL, default `'00000000-...'`), the FK (cascade), and the `(RoomId, Name)` index — and does NOT drop `UserId`. Append `migrationBuilder.Sql(SectionRoomBackfill.Sql);` to `Up()`.

- [ ] **Step 4** — Run the filter → PASS; then the full integration suite.
- [ ] **Step 5** — Commit `feat(domain): section-room migration + backfill`.

---

## Task 3: Migrate the read/write call sites

**Files:** `SectionsController.cs`, `SectionPageController.cs`, `SectionAttachmentsController.cs`, `RecordingsController.cs`, `ChatController.cs`, `SectionSummaryProcessor.cs`, `SectionMinutesProcessor.cs`

Mechanical, but do it controller-by-controller and keep green. The transform:

- Controllers with `UserId`: fetch `var roomId = await _rooms.PersonalRoomIdAsync(UserId);` once, replace `s.UserId == UserId` → `s.RoomId == roomId`, and set `RoomId = roomId` (not `UserId`) when creating a section. **Keep `UserId = UserId` set too** until Task 5, so existing FK/tests hold.
- `SectionSummaryProcessor.IncludedRecordingsAsync` (static, has `section`): the room is now `section.RoomId` directly — replace the `Rooms.OwnerUserId == section.UserId` join with `p.RoomId == section.RoomId`.
- `SectionMinutesProcessor`: same, if it has its own section-scoped query; else it inherits via the shared helper.

Inject `IRoomScope _rooms` into `SectionsController` and `SectionAttachmentsController` (the two that don't have it yet). `SectionPageController`, `RecordingsController`, `ChatController` already have `_rooms` from 2b.

- [ ] **Step 1** — Failing test: add to `SectionsControllerTests` a test that a section created via `Create` lands in the caller's personal room:

```csharp
    [Fact]
    public async Task Create_PutsTheSectionInThePersonalRoom()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "a@b.test", Email = "a@b.test" });
        await db.SaveChangesAsync();
        var scope = new RoomScope(db);
        var sut = new SectionsController(db, scope) { ControllerContext = Http.Context(userId) };

        var result = await sut.Create(new CreateSectionRequest("Work"));

        var id = ((SectionDto)((OkObjectResult)result.Result!).Value!).Id;
        Assert.Equal(await scope.PersonalRoomIdAsync(userId), (await db.Sections.FindAsync(id))!.RoomId);
    }
```

> Match the real `SectionsController` ctor after adding `IRoomScope` (see below).

- [ ] **Step 2** — Run → FAIL (ctor arity / `RoomId` not set).

- [ ] **Step 3** — Apply the transform across all seven files. For `SectionsController.Create`, set both `RoomId = roomId` and `UserId = UserId`. For every `s.UserId == UserId` predicate, use `s.RoomId == roomId`.

- [ ] **Step 4** — Fix construction sites (unit + integration `SectionsController`/`SectionAttachmentsController` builds gain `new RoomScope(db)`). Update any fixture that seeds a `Section` to also set `RoomId` via `RoomScope.PersonalRoomIdAsync` (or seed the room). `dotnet build Diariz.slnx`; both suites green.

- [ ] **Step 5** — Commit `refactor(api): folders scoped by room, not user`.

---

## Task 4: The 2b obligation — a placement's section must be in its room

**Files:** `RecordingsController.MoveToSection`/`Reorder`, `RoomScope.SetSectionAsync`; test in `RoomRecordingsIntegrationTests`

Now that sections carry `RoomId`, enforce that a recording can only be filed under a folder **in the same room** as the placement.

- [ ] **Step 1** — Failing integration test: `SetSectionAsync` (or `MoveToSection`) rejects a section from another room.

```csharp
    [Fact]
    public async Task SetSection_RejectsASectionFromAnotherRoom()
    {
        await using var db = fx.CreateDbContext();
        var userId = await NewUserAsync(db);
        var recId = await NewRecordingAsync(db, userId);
        var room = await PersonalRoomAsync(db, userId);
        db.RoomRecordings.Add(new RoomRecording { RoomId = room, RecordingId = recId, IsMainRoom = true });
        var otherRoom = new Room { Id = Guid.NewGuid(), Name = $"Other {Guid.NewGuid():N}", Kind = RoomKind.Shared };
        db.Rooms.Add(otherRoom);
        var foreignSection = new Section { Id = Guid.NewGuid(), UserId = userId, RoomId = otherRoom.Id, Name = "X" };
        db.Sections.Add(foreignSection);
        await db.SaveChangesAsync();

        Assert.False(await new RoomScope(db).SetSectionAsync(room, recId, foreignSection.Id));
    }
```

- [ ] **Step 2** — Run → FAIL (`SetSectionAsync` accepts any section id).

- [ ] **Step 3** — In `RoomScope.SetSectionAsync`, when `sectionId` is non-null, verify the section is in `roomId`:

```csharp
        if (sectionId is { } sid && !await db.Sections.AnyAsync(s => s.Id == sid && s.RoomId == roomId, ct))
            return false;
```

`RecordingsController.MoveToSection` already checks the caller owns the section; the room check makes it robust once shared rooms arrive.

- [ ] **Step 4** — Run → PASS; both suites green.
- [ ] **Step 5** — Commit `feat(api): a placement's section must belong to its room`.

---

## Task 5: Drop `Section.UserId`

**Files:** `Section.cs`, `DiarizDbContext.cs`, generated migration; fix remaining fixtures

- [ ] **Step 1** — Failing integration test (append to `RoomsIntegrationTests`):

```csharp
    [Fact]
    public async Task Sections_NoLongerHasAUserIdColumn()
    {
        await using var db = fx.CreateDbContext();
        var count = await db.Database
            .SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM information_schema.columns WHERE table_name = 'Sections' AND column_name = 'UserId'""")
            .SingleAsync();
        Assert.Equal(0, count);
    }
```

- [ ] **Step 2** — Run → FAIL (column present).

- [ ] **Step 3** — Remove `UserId`/`User` from `Section.cs`; remove the `HasOne(s => s.User)` config and the `(UserId, Name)` index from `DiarizDbContext.cs`. `dotnet ef migrations add DropSectionUserId ...`; confirm it drops the FK, index, and column only. Fix every fixture that set `Section.UserId` (seed `RoomId` instead — via `RoomScope.PlaceInMainRoomAsync`'s room, or set `RoomId` directly with a seeded room).

- [ ] **Step 4** — `dotnet build Diariz.slnx`; unit + integration + `cd apps/web && npm test && npx tsc --noEmit`. All green, zero warnings.
- [ ] **Step 5** — Commit `refactor(domain): drop Section.UserId; folders belong to a room`.

---

## Task 6: Docs + version

- [ ] `Data_Schema.md`: `AddSectionRoomId` + `DropSectionUserId` migration rows; `Sections` table `UserId`→`RoomId` (FK cascade, `(RoomId, Name)` index).
- [ ] `Overall_Synopsis_of_Platform.md`: note folders are room-scoped; the 2c line in the Rooms subsection.
- [ ] Version `0.118.3` → **`0.118.4`** (Build +1, invisible) across the four files; `releases.ts` entry. (Sweep PR holds `0.118.3`; reconcile at merge.)
- [ ] Full verify; commit `docs: sections onto rooms; bump version`.

## Deployment surface

Server redeploy only. Two migrations (`AddSectionRoomId` backfills; `DropSectionUserId` is destructive) apply back-to-back on startup — **backup before deploying; verify the backfill on a copy of dev** (`SELECT count(*) FROM "Sections" WHERE "RoomId" = '00000000-0000-0000-0000-000000000000'` must be `0`).
