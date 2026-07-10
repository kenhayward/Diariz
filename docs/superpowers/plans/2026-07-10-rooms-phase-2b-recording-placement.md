# Rooms Phase 2b: Recording Placement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A recording's folder becomes a property of its **placement in a room**, not of the recording. `RoomRecording` replaces `Recording.SectionId`, and every reader switches to it. The API behaves identically, because the personal room is still the only room.

**Architecture:** `RoomRecording` is the `(RoomId, RecordingId)` join carrying `IsMainRoom` and the `SectionId` **within that room**. Every recording gets exactly one row, `IsMainRoom = true`, in its recorder's Personal Room - the invariant that makes a shared room unable to hold a recording hostage. `RoomScope` gains `RecordingsIn(roomId)` and the placement accessors; the six files that read or write `Recording.SectionId` switch to them; then the column is dropped.

**Tech Stack:** ASP.NET Core 10, EF Core + Postgres, xUnit + Testcontainers.

**Spec:** [`docs/superpowers/specs/2026-07-10-rooms-design.md`](../specs/2026-07-10-rooms-design.md) - "Data model → `RoomRecording`", "Why the main room is always the Personal Room".

**Preceded by:** Phase 2a (`Room`, `RoomMember`, `RoomScope`) - merged as PR #261.

---

## Scope

Measured, not assumed. Only these read or write `Recording.SectionId`:

| File | Sites | What |
|---|---|---|
| `Controllers/RecordingsController.cs` | 85, 98, 221, 883, 888, 892 | `Reorder`, the list DTO, `MoveToSection` |
| `Controllers/SectionPageController.cs` | 64, 93, 108, 122 | four "recordings in this folder" queries |
| `Controllers/ChatController.cs` | 365, 371 | folder-scoped chat context |
| `Services/SectionSummaryProcessor.cs` | 86 | the folder roll-up's recording set |
| `Domain/Entities/Recording.cs` | 58 | the column itself |
| `Domain/Entities/Section.cs` | 26 | the `Recordings` navigation |

`SectionAttachmentsController`, `SectionMinutesProcessor`, and the rest of `SectionSummaryProcessor` reference `SectionAttachment.SectionId` / `Section.Id` and are **untouched**. `MoveRecordingRequest.SectionId`, `ReorderRecordingsRequest.SectionId`, `RecordingDto.SectionId` and `ChatRequest.SectionId` keep their shape - the wire format does not change, only where the value is stored.

**This is the first phase that edits controllers.** Phase 2a's "no controller change" rule ends here.

**Out of scope:** `Section.UserId` → `RoomId` (2c). Shared rooms, sharing, unsharing, `RoomsController`, routes carrying a `roomId` (Phase 3+). Everything resolves to the caller's personal room via `RoomScope.PersonalRoomIdAsync`.

---

## Non-negotiable constraints

1. **TDD.** Failing test first, watch it fail, then the minimal code.
2. **No behaviour change.** Same routes, same request/response shapes, same 404s. Existing tests should pass; where one must change it is because it constructed a controller (new ctor arg) or set `rec.SectionId` directly - **not** because an assertion moved. If an assertion has to change, stop and ask.
3. **Every commit builds and is green.** The column is dropped in the *last* code task, after every reader has moved off it. Do not drop it early.
4. **Build `Diariz.slnx`**, not just the unit project. Controller ctor changes have a second construction site in `tests/Diariz.Api.IntegrationTests/`.
5. **Postgres-only model config** (filtered indexes, check constraints) goes behind `Database.IsNpgsql()`.
6. **Int enums / flags are append-only.**

---

## The invariant, and the trap in the backfill

`RoomRecording.IsMainRoom` is true on exactly one row per recording, and **that row's room is the personal room of `Recording.UserId`**. It is derivable, and stored anyway: it keeps `RoomScope`'s join simple and leaves room for a future "move between rooms".

**The trap:** Phase 2a backfilled a personal room for every user *that existed then*, and `RoomScope` creates one lazily thereafter. But **nothing calls `RoomScope` yet** (2a shipped it dormant), so any user created between the 2a and 2b deploys has **no personal room** - and may already own recordings. The 2b backfill must therefore create the missing rooms *first*, by re-running `PersonalRoomBackfill.Sql` (which is idempotent, by design), before it can place a single recording.

Get this wrong and the backfill silently skips those users' recordings, which then vanish from their list. Task 2 tests exactly this.

**A second invariant, not enforced by the database:** `RoomRecording.SectionId` must name a folder in
`RoomRecording.RoomId`. It is not expressible as a simple FK (the section's room lives one hop away), and in
this phase it holds trivially - `Section` is still user-scoped, and the only room is the owner's personal one, so
the existing "only allow moving into a section the caller owns" check covers it. **Phase 2c, which moves
`Section` onto `RoomId`, must add the explicit check and an integration test for it.**

---

## File structure

**Create:**

| File | Responsibility |
|---|---|
| `src/Diariz.Domain/Entities/RoomRecording.cs` | The placement entity. |
| `src/Diariz.Domain/Migrations/RecordingPlacementBackfill.cs` | The one-time backfill SQL, shared with its test. |
| `tests/Diariz.Api.Tests/RoomRecordingModelTests.cs` | Entity round-trip. |
| `tests/Diariz.Api.Tests/RoomScopePlacementTests.cs` | `RecordingsIn`, `SectionIdAsync`, `SetSectionAsync`. |
| `tests/Diariz.Api.IntegrationTests/RoomRecordingsIntegrationTests.cs` | Indexes, cascades, the backfill. |

**Modify:**

| File | Change |
|---|---|
| `src/Diariz.Domain/DiarizDbContext.cs` | `DbSet` + config; later, drop the `Recording.SectionId` relationship. |
| `src/Diariz.Domain/Entities/Recording.cs` | Loses `SectionId` + `Section` (Task 6). |
| `src/Diariz.Domain/Entities/Section.cs` | Loses the `Recordings` navigation (Task 6). |
| `src/Diariz.Api/Services/RoomScope.cs` | `RecordingsIn`, `SectionIdAsync`, `SetSectionAsync`, `PlaceInMainRoomAsync`. |
| `src/Diariz.Api/Controllers/RecordingsController.cs` | List DTO, `Reorder`, `MoveToSection`, and placement on upload. |
| `src/Diariz.Api/Controllers/SectionPageController.cs` | Four folder queries. |
| `src/Diariz.Api/Controllers/ChatController.cs` | Two folder queries. |
| `src/Diariz.Api/Services/SectionSummaryProcessor.cs` | One folder query. |
| `docs/Data_Schema.md`, `docs/Overall_Synopsis_of_Platform.md` | The placement model. |
| `version.json` (+3 mirrors), `apps/web/src/lib/releases.ts` | Release. |

> **Namespaces:** `DiarizDbContext` in `Diariz.Domain`; entities in `Diariz.Domain.Entities`; `TestDb` / `Http` / `Perms` in `Diariz.Api.Tests.Infrastructure`. Integration tests use `[Collection(IntegrationCollection.Name)]`, `class X(ContainersFixture fx)`, `fx.CreateDbContext()`.

---

## Task 1: The `RoomRecording` entity

**Files:**
- Create: `src/Diariz.Domain/Entities/RoomRecording.cs`
- Modify: `src/Diariz.Domain/DiarizDbContext.cs`
- Test: `tests/Diariz.Api.Tests/RoomRecordingModelTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Diariz.Api.Tests/RoomRecordingModelTests.cs`:

```csharp
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

public class RoomRecordingModelTests
{
    [Fact]
    public async Task MainPlacement_RoundTrips_WithAFolderAndNoSharer()
    {
        using var db = TestDb.Create();
        var roomId = Guid.NewGuid();
        var recordingId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();

        db.RoomRecordings.Add(new RoomRecording
        {
            RoomId = roomId,
            RecordingId = recordingId,
            IsMainRoom = true,
            SectionId = sectionId,
        });
        await db.SaveChangesAsync();

        var placement = db.RoomRecordings.Single();
        Assert.True(placement.IsMainRoom);
        Assert.Equal(sectionId, placement.SectionId);
        Assert.Null(placement.SharedByUserId);
        Assert.Null(placement.SharedAt);
    }

    [Fact]
    public async Task SharedPlacement_CarriesTheSharerAndNoMainFlag()
    {
        using var db = TestDb.Create();
        var sharer = Guid.NewGuid();
        var sharedAt = DateTimeOffset.UtcNow;

        db.RoomRecordings.Add(new RoomRecording
        {
            RoomId = Guid.NewGuid(),
            RecordingId = Guid.NewGuid(),
            IsMainRoom = false,
            SectionId = null, // ungrouped in the room it was shared into
            SharedByUserId = sharer,
            SharedAt = sharedAt,
        });
        await db.SaveChangesAsync();

        var placement = db.RoomRecordings.Single();
        Assert.False(placement.IsMainRoom);
        Assert.Null(placement.SectionId);
        Assert.Equal(sharer, placement.SharedByUserId);
        Assert.Equal(sharedAt, placement.SharedAt);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~RoomRecordingModelTests"`
Expected: FAIL to **compile** - `RoomRecording` and `db.RoomRecordings` do not exist.

- [ ] **Step 3: Write minimal implementation**

`src/Diariz.Domain/Entities/RoomRecording.cs`:

```csharp
namespace Diariz.Domain.Entities;

/// <summary>The placement of a recording in a room. A recording has exactly one MAIN placement - always in its
/// recorder's Personal Room - plus one row per room it has been shared into.
///
/// The folder is a property of the PLACEMENT, not of the recording: the same recording can sit in "Q3 Reviews"
/// in one room and be ungrouped in another. That is why Recording.SectionId no longer exists.
///
/// Because the main room is always personal, deleting a shared room can only ever unshare - never destroy.</summary>
public class RoomRecording
{
    public Guid RoomId { get; set; }
    public Room? Room { get; set; }

    public Guid RecordingId { get; set; }
    public Recording? Recording { get; set; }

    /// <summary>True on exactly one row per recording, and that row's room is the personal room of
    /// <c>Recording.UserId</c>. Derivable, but stored: it keeps the room-scoped query a plain join, and leaves
    /// room for a future "move between rooms".</summary>
    public bool IsMainRoom { get; set; }

    /// <summary>The folder WITHIN THIS ROOM. Null = ungrouped. `ON DELETE SET NULL`, so deleting a folder
    /// ungroups its recordings rather than removing them from the room.</summary>
    public Guid? SectionId { get; set; }
    public Section? Section { get; set; }

    /// <summary>Null on the main-room row: nobody shared a recording into its own home.</summary>
    public Guid? SharedByUserId { get; set; }
    public DateTimeOffset? SharedAt { get; set; }
}
```

In `src/Diariz.Domain/DiarizDbContext.cs`, add the `DbSet` beside `RoomMembers`:

```csharp
    public DbSet<RoomRecording> RoomRecordings => Set<RoomRecording>();
```

and in `OnModelCreating`, immediately after the `RoomMember` block (provider-agnostic part):

```csharp
        builder.Entity<RoomRecording>(e =>
        {
            e.HasKey(p => new { p.RoomId, p.RecordingId });
            e.HasOne(p => p.Room).WithMany()
                .HasForeignKey(p => p.RoomId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(p => p.Recording).WithMany()
                .HasForeignKey(p => p.RecordingId).OnDelete(DeleteBehavior.Cascade);
            // Deleting a folder ungroups its recordings; it never removes them from the room.
            e.HasOne(p => p.Section).WithMany()
                .HasForeignKey(p => p.SectionId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(p => new { p.RoomId, p.SectionId });
        });
```

and **inside** the `if (isNpgsql)` block that already holds the `Room` indexes:

```csharp
            // Exactly one main room per recording. Two would be unrepresentable, not merely wrong.
            builder.Entity<RoomRecording>()
                .HasIndex(p => p.RecordingId)
                .IsUnique()
                .HasFilter("\"IsMainRoom\"");

            // A main placement is nobody's share.
            builder.Entity<RoomRecording>()
                .ToTable(t => t.HasCheckConstraint(
                    "CK_RoomRecordings_MainRoomHasNoSharer",
                    "NOT \"IsMainRoom\" OR (\"SharedByUserId\" IS NULL AND \"SharedAt\" IS NULL)"));
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~RoomRecordingModelTests"`
Expected: PASS (2 tests).

Then `dotnet test tests/Diariz.Api.Tests` - the whole unit suite, unchanged plus 2.

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Domain/Entities/RoomRecording.cs src/Diariz.Domain/DiarizDbContext.cs tests/Diariz.Api.Tests/RoomRecordingModelTests.cs
git commit -m "feat(domain): add RoomRecording, the per-room placement of a recording"
```

---

## Task 2: The migration and the placement backfill

**Files:**
- Create: `src/Diariz.Domain/Migrations/RecordingPlacementBackfill.cs`
- Create: `src/Diariz.Domain/Migrations/<timestamp>_AddRoomRecordings.cs` (generated, then edited)
- Test: `tests/Diariz.Api.IntegrationTests/RoomRecordingsIntegrationTests.cs`

`Recording.SectionId` is **kept** in this task. It is dropped in Task 6, once every reader has moved off it.

- [ ] **Step 1: Write the failing test**

Create `tests/Diariz.Api.IntegrationTests/RoomRecordingsIntegrationTests.cs`:

```csharp
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Diariz.Domain.Migrations;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

/// <summary>Placement against real Postgres: the filtered unique index on IsMainRoom, the check constraint, the
/// SET NULL on folder delete, and the backfill - none of which the in-memory provider honours.</summary>
[Collection(IntegrationCollection.Name)]
public class RoomRecordingsIntegrationTests(ContainersFixture fx)
{
    private static async Task<Guid> NewUserAsync(DiarizDbContext db, string? fullName = null)
    {
        var name = $"u{Guid.NewGuid():N}@x.test";
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = name, NormalizedUserName = name.ToUpperInvariant(),
            Email = name, NormalizedEmail = name.ToUpperInvariant(),
            FullName = fullName,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<Guid> NewRecordingAsync(DiarizDbContext db, Guid userId, Guid? sectionId = null)
    {
        var rec = new Recording
        {
            Id = Guid.NewGuid(), UserId = userId, Title = "Rec", BlobKey = $"{userId}/{Guid.NewGuid():N}.webm",
            SectionId = sectionId,
        };
        db.Recordings.Add(rec);
        await db.SaveChangesAsync();
        return rec.Id;
    }

    private static async Task<Guid> NewSectionAsync(DiarizDbContext db, Guid userId)
    {
        var s = new Section { Id = Guid.NewGuid(), UserId = userId, Name = $"Folder {Guid.NewGuid():N}" };
        db.Sections.Add(s);
        await db.SaveChangesAsync();
        return s.Id;
    }

    private static async Task<Guid> PersonalRoomAsync(DiarizDbContext db, Guid userId)
    {
        await db.Database.ExecuteSqlRawAsync(PersonalRoomBackfill.Sql);
        return await db.Rooms.Where(r => r.OwnerUserId == userId).Select(r => r.Id).SingleAsync();
    }

    [Fact]
    public async Task ARecordingHasAtMostOneMainRoom()
    {
        await using var db = fx.CreateDbContext();
        var userId = await NewUserAsync(db);
        var recId = await NewRecordingAsync(db, userId);
        var roomA = await PersonalRoomAsync(db, userId);
        var roomB = new Room { Id = Guid.NewGuid(), Name = $"Shared {Guid.NewGuid():N}", Kind = RoomKind.Shared };
        db.Rooms.Add(roomB);
        await db.SaveChangesAsync();

        db.RoomRecordings.Add(new RoomRecording { RoomId = roomA, RecordingId = recId, IsMainRoom = true });
        await db.SaveChangesAsync();

        db.RoomRecordings.Add(new RoomRecording { RoomId = roomB.Id, RecordingId = recId, IsMainRoom = true });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task ARecordingMayBeSharedIntoManyRooms()
    {
        await using var db = fx.CreateDbContext();
        var userId = await NewUserAsync(db);
        var recId = await NewRecordingAsync(db, userId);
        var main = await PersonalRoomAsync(db, userId);
        var shared = new Room { Id = Guid.NewGuid(), Name = $"Shared {Guid.NewGuid():N}", Kind = RoomKind.Shared };
        db.Rooms.Add(shared);
        await db.SaveChangesAsync();

        db.RoomRecordings.Add(new RoomRecording { RoomId = main, RecordingId = recId, IsMainRoom = true });
        db.RoomRecordings.Add(new RoomRecording
        {
            RoomId = shared.Id, RecordingId = recId, IsMainRoom = false,
            SharedByUserId = userId, SharedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.RoomRecordings.CountAsync(p => p.RecordingId == recId));
    }

    /// <summary>Nobody shared a recording into its own home: the check constraint says so.</summary>
    [Fact]
    public async Task AMainPlacementCannotCarryASharer()
    {
        await using var db = fx.CreateDbContext();
        var userId = await NewUserAsync(db);
        var recId = await NewRecordingAsync(db, userId);
        var main = await PersonalRoomAsync(db, userId);

        db.RoomRecordings.Add(new RoomRecording
        {
            RoomId = main, RecordingId = recId, IsMainRoom = true,
            SharedByUserId = userId, SharedAt = DateTimeOffset.UtcNow,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    /// <summary>Deleting a folder ungroups its recordings; it must never remove them from the room.</summary>
    [Fact]
    public async Task DeletingASection_UngroupsThePlacement_ButKeepsIt()
    {
        await using var db = fx.CreateDbContext();
        var userId = await NewUserAsync(db);
        var sectionId = await NewSectionAsync(db, userId);
        var recId = await NewRecordingAsync(db, userId);
        var main = await PersonalRoomAsync(db, userId);
        db.RoomRecordings.Add(new RoomRecording
        {
            RoomId = main, RecordingId = recId, IsMainRoom = true, SectionId = sectionId,
        });
        await db.SaveChangesAsync();

        db.Sections.Remove(await db.Sections.FindAsync(sectionId) ?? throw new InvalidOperationException("gone"));
        await db.SaveChangesAsync();

        var placement = await db.RoomRecordings.SingleAsync(p => p.RecordingId == recId);
        Assert.Null(placement.SectionId);
        Assert.Equal(main, placement.RoomId);
    }

    [Fact]
    public async Task DeletingARecording_CascadesItsPlacements()
    {
        await using var db = fx.CreateDbContext();
        var userId = await NewUserAsync(db);
        var recId = await NewRecordingAsync(db, userId);
        var main = await PersonalRoomAsync(db, userId);
        db.RoomRecordings.Add(new RoomRecording { RoomId = main, RecordingId = recId, IsMainRoom = true });
        await db.SaveChangesAsync();

        db.Recordings.Remove(await db.Recordings.FindAsync(recId) ?? throw new InvalidOperationException("gone"));
        await db.SaveChangesAsync();

        Assert.Empty(await db.RoomRecordings.Where(p => p.RecordingId == recId).ToListAsync());
    }

    /// <summary>The backfill: one main placement per recording, in its recorder's personal room, carrying the
    /// folder the recording was in. Idempotent.</summary>
    [Fact]
    public async Task Backfill_PlacesEveryRecordingInItsRecordersPersonalRoom_KeepingItsFolder()
    {
        await using var db = fx.CreateDbContext();
        var userId = await NewUserAsync(db, "Ada Lovelace");
        var sectionId = await NewSectionAsync(db, userId);
        var filed = await NewRecordingAsync(db, userId, sectionId);
        var ungrouped = await NewRecordingAsync(db, userId);

        await db.Database.ExecuteSqlRawAsync(RecordingPlacementBackfill.Sql);
        await db.Database.ExecuteSqlRawAsync(RecordingPlacementBackfill.Sql); // twice: must not duplicate

        var room = await db.Rooms.Where(r => r.OwnerUserId == userId).Select(r => r.Id).SingleAsync();

        var a = await db.RoomRecordings.SingleAsync(p => p.RecordingId == filed);
        Assert.Equal(room, a.RoomId);
        Assert.True(a.IsMainRoom);
        Assert.Equal(sectionId, a.SectionId);
        Assert.Null(a.SharedByUserId);

        var b = await db.RoomRecordings.SingleAsync(p => p.RecordingId == ungrouped);
        Assert.True(b.IsMainRoom);
        Assert.Null(b.SectionId);
    }

    /// <summary>THE TRAP. Phase 2a gave rooms only to users who existed then, and nothing calls RoomScope yet,
    /// so a user created since has NO personal room - and may already own recordings. The backfill must mint
    /// the missing room first, or their recordings are silently left unplaced and vanish from their list.</summary>
    [Fact]
    public async Task Backfill_MintsAMissingPersonalRoom_ForAUserCreatedAfterPhase2a()
    {
        await using var db = fx.CreateDbContext();
        var userId = await NewUserAsync(db, "Grace Hopper");
        var recId = await NewRecordingAsync(db, userId);
        Assert.Empty(await db.Rooms.Where(r => r.OwnerUserId == userId).ToListAsync()); // no room yet

        await db.Database.ExecuteSqlRawAsync(RecordingPlacementBackfill.Sql);

        var room = await db.Rooms.SingleAsync(r => r.OwnerUserId == userId);
        var placement = await db.RoomRecordings.SingleAsync(p => p.RecordingId == recId);
        Assert.Equal(room.Id, placement.RoomId);
        Assert.True(placement.IsMainRoom);
    }
}
```

> Check `Recording`'s required properties before running (`Title`, `BlobKey`, `Status`, `CreatedAt` etc.). Set whatever the entity marks non-nullable; the helper above sets the ones that exist today.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~RoomRecordingsIntegrationTests"`
Expected: FAIL to compile (`RecordingPlacementBackfill` does not exist), then EF's `PendingModelChangesWarning`.

- [ ] **Step 3: Write the backfill, then generate and edit the migration**

`src/Diariz.Domain/Migrations/RecordingPlacementBackfill.cs`:

```csharp
namespace Diariz.Domain.Migrations;

/// <summary>Gives every existing recording its main placement: one RoomRecordings row in its recorder's Personal
/// room, carrying the folder the recording was filed in.
///
/// It re-runs <see cref="PersonalRoomBackfill"/> first, and that is not belt-and-braces. Phase 2a gave rooms
/// only to the users who existed then, and RoomScope creates them lazily - but nothing calls RoomScope yet, so
/// any user created between the two deploys has NO personal room and may already own recordings. Without the
/// mint, their recordings are silently left unplaced and disappear from their list.
///
/// One-way, and therefore in the migration rather than the seeder: __EFMigrationsHistory runs it exactly once
/// per database, including for an upgrading deployment. Idempotent anyway (NOT EXISTS + ON CONFLICT), so a test
/// can run it twice.
///
/// `true` = IsMainRoom. SharedByUserId / SharedAt stay NULL: nobody shared a recording into its own home.</summary>
public static class RecordingPlacementBackfill
{
    public const string Sql = PersonalRoomBackfill.Sql + """

        INSERT INTO "RoomRecordings" ("RoomId", "RecordingId", "IsMainRoom", "SectionId", "SharedByUserId", "SharedAt")
        SELECT room."Id", rec."Id", true, rec."SectionId", NULL, NULL
        FROM "Recordings" rec
        JOIN "Rooms" room ON room."OwnerUserId" = rec."UserId" AND room."Kind" = 0
        WHERE NOT EXISTS (
            SELECT 1 FROM "RoomRecordings" p WHERE p."RecordingId" = rec."Id" AND p."IsMainRoom"
        )
        ON CONFLICT DO NOTHING;
        """;
}
```

Generate the migration:

```bash
dotnet ef migrations add AddRoomRecordings --project src/Diariz.Domain --startup-project src/Diariz.Api
```

Confirm it creates `RoomRecordings` with: composite PK, `RoomId`/`RecordingId` FKs `Cascade`, `SectionId` FK **`SetNull`**, index on `(RoomId, SectionId)`, the **filtered** unique index on `RecordingId` (`filter: "\"IsMainRoom\""`), and the check constraint. It must **not** drop `Recordings.SectionId` - that is Task 6. Then append the backfill as the last statement of `Up()`:

```csharp
            // One main placement per existing recording, in its recorder's personal room, keeping its folder.
            // Runs exactly once per database - see RecordingPlacementBackfill.
            migrationBuilder.Sql(RecordingPlacementBackfill.Sql);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~RoomRecordingsIntegrationTests"`
Expected: PASS (7 tests).

Then the whole integration suite: `dotnet test tests/Diariz.Api.IntegrationTests`

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Domain/Migrations tests/Diariz.Api.IntegrationTests/RoomRecordingsIntegrationTests.cs
git commit -m "feat(domain): RoomRecordings migration + one-time placement backfill"
```

---

## Task 3: `RoomScope` placement accessors

**Files:**
- Modify: `src/Diariz.Api/Services/RoomScope.cs`
- Test: `tests/Diariz.Api.Tests/RoomScopePlacementTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Diariz.Api.Tests/RoomScopePlacementTests.cs`:

```csharp
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

public class RoomScopePlacementTests
{
    private static async Task<Guid> NewUserAsync(DiarizDbContext db)
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid():N}@x.test", Email = $"{Guid.NewGuid():N}@x.test",
            FullName = "Ada",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<Guid> NewRecordingAsync(DiarizDbContext db, Guid userId, string title = "Rec")
    {
        var rec = new Recording
        {
            Id = Guid.NewGuid(), UserId = userId, Title = title, BlobKey = $"{userId}/{Guid.NewGuid():N}.webm",
        };
        db.Recordings.Add(rec);
        await db.SaveChangesAsync();
        return rec.Id;
    }

    [Fact]
    public async Task RecordingsIn_ReturnsOnlyThatRoomsRecordings()
    {
        using var db = TestDb.Create();
        var alice = await NewUserAsync(db);
        var bob = await NewUserAsync(db);
        var sut = new RoomScope(db);
        var aliceRoom = await sut.PersonalRoomIdAsync(alice);
        var bobRoom = await sut.PersonalRoomIdAsync(bob);

        var aliceRec = await NewRecordingAsync(db, alice, "Alice's");
        var bobRec = await NewRecordingAsync(db, bob, "Bob's");
        await sut.PlaceInMainRoomAsync(aliceRec, alice, sectionId: null);
        await sut.PlaceInMainRoomAsync(bobRec, bob, sectionId: null);

        var titles = await sut.RecordingsIn(aliceRoom).Select(r => r.Title).ToListAsync();

        Assert.Equal(["Alice's"], titles);
        Assert.Equal(["Bob's"], await sut.RecordingsIn(bobRoom).Select(r => r.Title).ToListAsync());
    }

    [Fact]
    public async Task PlaceInMainRoom_PutsItInTheRecordersPersonalRoom_WithTheFolder()
    {
        using var db = TestDb.Create();
        var userId = await NewUserAsync(db);
        var sut = new RoomScope(db);
        var roomId = await sut.PersonalRoomIdAsync(userId);
        var section = new Section { Id = Guid.NewGuid(), UserId = userId, Name = "Q3" };
        db.Sections.Add(section);
        await db.SaveChangesAsync();
        var recId = await NewRecordingAsync(db, userId);

        await sut.PlaceInMainRoomAsync(recId, userId, section.Id);

        var placement = db.RoomRecordings.Single();
        Assert.Equal(roomId, placement.RoomId);
        Assert.True(placement.IsMainRoom);
        Assert.Equal(section.Id, placement.SectionId);
        Assert.Null(placement.SharedByUserId);
    }

    [Fact]
    public async Task SectionIdAsync_ReadsTheFolderForThatRoom()
    {
        using var db = TestDb.Create();
        var userId = await NewUserAsync(db);
        var sut = new RoomScope(db);
        var roomId = await sut.PersonalRoomIdAsync(userId);
        var section = new Section { Id = Guid.NewGuid(), UserId = userId, Name = "Q3" };
        db.Sections.Add(section);
        await db.SaveChangesAsync();
        var recId = await NewRecordingAsync(db, userId);
        await sut.PlaceInMainRoomAsync(recId, userId, section.Id);

        Assert.Equal(section.Id, await sut.SectionIdAsync(roomId, recId));
        Assert.Null(await sut.SectionIdAsync(Guid.NewGuid(), recId)); // a room it is not placed in
    }

    [Fact]
    public async Task SetSectionAsync_MovesAndUngroups()
    {
        using var db = TestDb.Create();
        var userId = await NewUserAsync(db);
        var sut = new RoomScope(db);
        var roomId = await sut.PersonalRoomIdAsync(userId);
        var section = new Section { Id = Guid.NewGuid(), UserId = userId, Name = "Q3" };
        db.Sections.Add(section);
        await db.SaveChangesAsync();
        var recId = await NewRecordingAsync(db, userId);
        await sut.PlaceInMainRoomAsync(recId, userId, sectionId: null);

        Assert.True(await sut.SetSectionAsync(roomId, recId, section.Id));
        Assert.Equal(section.Id, await sut.SectionIdAsync(roomId, recId));

        Assert.True(await sut.SetSectionAsync(roomId, recId, null)); // ungroup
        Assert.Null(await sut.SectionIdAsync(roomId, recId));
    }

    [Fact]
    public async Task SetSectionAsync_ReturnsFalse_WhenTheRecordingIsNotInThatRoom()
    {
        using var db = TestDb.Create();
        var userId = await NewUserAsync(db);
        var sut = new RoomScope(db);
        await sut.PersonalRoomIdAsync(userId);
        var recId = await NewRecordingAsync(db, userId);

        Assert.False(await sut.SetSectionAsync(Guid.NewGuid(), recId, null));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~RoomScopePlacementTests"`
Expected: FAIL to compile - `RecordingsIn`, `PlaceInMainRoomAsync`, `SectionIdAsync`, `SetSectionAsync` do not exist.

- [ ] **Step 3: Write minimal implementation**

Add to the `IRoomScope` interface in `src/Diariz.Api/Services/RoomScope.cs`:

```csharp
    /// <summary>Every recording placed in this room: its main-room recordings plus everything shared into it.
    /// The base queryable for every room-scoped recording query - the equivalent of today's
    /// `.Where(r => r.UserId == UserId)`, one level up.</summary>
    IQueryable<Recording> RecordingsIn(Guid roomId);

    /// <summary>Create the main placement for a new recording, in its recorder's personal room.</summary>
    Task PlaceInMainRoomAsync(Guid recordingId, Guid recordedByUserId, Guid? sectionId, CancellationToken ct = default);

    /// <summary>The folder this recording sits in, within this room. Null when ungrouped, or when it is not
    /// placed in that room at all.</summary>
    Task<Guid?> SectionIdAsync(Guid roomId, Guid recordingId, CancellationToken ct = default);

    /// <summary>Move the recording to a folder within this room (null = ungroup). False when it is not placed
    /// in that room.</summary>
    Task<bool> SetSectionAsync(Guid roomId, Guid recordingId, Guid? sectionId, CancellationToken ct = default);
```

and to `RoomScope`:

```csharp
    // An explicit join, not `.Select(p => p.Recording!)`: the in-memory test provider does not fix up the
    // navigation for an untracked query and would yield nulls. Same trap as UserPermissions in Phase 1.
    public IQueryable<Recording> RecordingsIn(Guid roomId) =>
        from p in db.RoomRecordings
        where p.RoomId == roomId
        join r in db.Recordings on p.RecordingId equals r.Id
        select r;

    public async Task PlaceInMainRoomAsync(
        Guid recordingId, Guid recordedByUserId, Guid? sectionId, CancellationToken ct = default)
    {
        var roomId = await PersonalRoomIdAsync(recordedByUserId, ct);
        db.RoomRecordings.Add(new RoomRecording
        {
            RoomId = roomId,
            RecordingId = recordingId,
            IsMainRoom = true,
            SectionId = sectionId,
        });
        await db.SaveChangesAsync(ct);
    }

    public Task<Guid?> SectionIdAsync(Guid roomId, Guid recordingId, CancellationToken ct = default) =>
        db.RoomRecordings
            .Where(p => p.RoomId == roomId && p.RecordingId == recordingId)
            .Select(p => p.SectionId)
            .FirstOrDefaultAsync(ct);

    public async Task<bool> SetSectionAsync(
        Guid roomId, Guid recordingId, Guid? sectionId, CancellationToken ct = default)
    {
        var placement = await db.RoomRecordings
            .FirstOrDefaultAsync(p => p.RoomId == roomId && p.RecordingId == recordingId, ct);
        if (placement is null) return false;

        placement.SectionId = sectionId;
        await db.SaveChangesAsync(ct);
        return true;
    }
```

> `SectionIdAsync` returns `null` both for "ungrouped" and for "not in this room". That is deliberate: every caller in this phase asks about the caller's own personal room, where the recording is by definition placed. `SetSectionAsync` distinguishes the two, because it must 404.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~RoomScopePlacementTests"`
Expected: PASS (5 tests). Then `dotnet build Diariz.slnx && dotnet test tests/Diariz.Api.Tests`.

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Services/RoomScope.cs tests/Diariz.Api.Tests/RoomScopePlacementTests.cs
git commit -m "feat(api): RoomScope placement accessors (RecordingsIn, SectionId, SetSection)"
```

---

## Task 4: `RecordingsController` reads and writes placements

**Files:**
- Modify: `src/Diariz.Api/Controllers/RecordingsController.cs` (lines 85, 98, 221, 883-892, and the upload path)
- Modify: whatever constructs it in `tests/`
- Test: existing `RecordingsController` tests, plus one new

`Recording.SectionId` still exists after this task. It is simply no longer read or written by this controller.

- [ ] **Step 1: Write the failing test**

Add to the existing `RecordingsController` unit test file (find it: `ls tests/Diariz.Api.Tests | grep -i recordings`):

```csharp
    /// <summary>Uploading creates the recording's main placement in the uploader's personal room, so it appears
    /// in their list. Before placements existed this was Recording.SectionId, defaulting to null.</summary>
    [Fact]
    public async Task Upload_CreatesTheMainPlacement_InThePersonalRoom()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "a@b.test", Email = "a@b.test", FullName = "Ada" });
        await db.SaveChangesAsync();

        var scope = new RoomScope(db);
        var sut = BuildController(db, scope, userId); // extend the file's existing Build helper with the scope

        var created = await sut.Create(FakeUpload("clip.webm"), "Title", 1000, "Microphone", null);

        var placement = db.RoomRecordings.Single();
        Assert.Equal(await scope.PersonalRoomIdAsync(userId), placement.RoomId);
        Assert.True(placement.IsMainRoom);
        Assert.Null(placement.SectionId);
    }
```

> Match the real `Create` signature and the file's existing upload helper - do not invent one. If the tests use `FakeAudioStorage` and `FakeJobQueue` from `TestSupport`, keep using them.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~Upload_CreatesTheMainPlacement"`
Expected: FAIL - `RecordingsController` has no `IRoomScope`, and no placement row is written.

- [ ] **Step 3: Rewrite the four sites**

Inject `IRoomScope _rooms` into the constructor. Then, in order:

**a. Upload (`Create`)** - after `await _db.SaveChangesAsync()` puts the `Recording` row in, add the placement:

```csharp
        await _rooms.PlaceInMainRoomAsync(rec.Id, UserId, sectionId: null);
```

**b. The list DTO (line 221)** - `rec.SectionId` becomes the placement's. Fetch the personal room once, then project the folder from `RoomRecordings`:

```csharp
        var roomId = await _rooms.PersonalRoomIdAsync(UserId);
        var folderOf = await _db.RoomRecordings
            .Where(p => p.RoomId == roomId)
            .ToDictionaryAsync(p => p.RecordingId, p => p.SectionId);
```
and replace `rec.SectionId` in the DTO construction with
```csharp
        folderOf.TryGetValue(rec.Id, out var sectionId) ? sectionId : null,
```

**c. `Reorder` (lines 85, 98)** - the section-ownership check is unchanged. Replace `rec.SectionId = req.SectionId;` with a placement update inside the loop:

```csharp
        var roomId = await _rooms.PersonalRoomIdAsync(UserId);
        var placements = await _db.RoomRecordings
            .Where(p => p.RoomId == roomId && ids.Contains(p.RecordingId))
            .ToDictionaryAsync(p => p.RecordingId);

        for (var i = 0; i < ids.Count; i++)
        {
            var rec = byId[ids[i]];
            rec.Position = i;
            if (placements.TryGetValue(ids[i], out var placement)) placement.SectionId = req.SectionId;
        }
```

**d. `MoveToSection` (lines 883-892)** - the whole body after the ownership check becomes:

```csharp
        var roomId = await _rooms.PersonalRoomIdAsync(UserId);
        if (!await _rooms.SetSectionAsync(roomId, id, req.SectionId)) return NotFound();
        return NoContent();
```
(keeping the existing "only allow moving into a section the caller owns" check above it, and deleting the `rec.SectionId = …` / `SaveChangesAsync` lines it replaces).

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet build Diariz.slnx
dotnet test tests/Diariz.Api.Tests
dotnet test tests/Diariz.Api.IntegrationTests
```
Expected: all green. Existing tests that set `rec.SectionId` directly as *setup* must now create a placement instead - that is a fixture change, not an assertion change. **If an assertion has to change, stop and ask.**

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Controllers/RecordingsController.cs tests/
git commit -m "refactor(api): RecordingsController reads and writes placements"
```

---

## Task 5: The three remaining readers

**Files:**
- Modify: `src/Diariz.Api/Controllers/SectionPageController.cs` (lines 64, 93, 108, 122)
- Modify: `src/Diariz.Api/Controllers/ChatController.cs` (lines 365, 371)
- Modify: `src/Diariz.Api/Services/SectionSummaryProcessor.cs` (line 86)

All six sites are the same query, spelled four ways: *the recordings filed under this folder or its children*. Today:

```csharp
r.UserId == UserId && r.SectionId.HasValue && allIds.Contains(r.SectionId.Value)
```

- [ ] **Step 1: Write the failing test**

Add to the existing `SectionPageController` test file:

```csharp
    /// <summary>A folder's recordings now come from the placement, not from Recording.SectionId.</summary>
    [Fact]
    public async Task FolderPage_ListsRecordingsPlacedInThatFolder()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "a@b.test", Email = "a@b.test", FullName = "Ada" });
        var section = new Section { Id = Guid.NewGuid(), UserId = userId, Name = "Q3" };
        db.Sections.Add(section);
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, Title = "In Q3", BlobKey = "k" };
        db.Recordings.Add(rec);
        await db.SaveChangesAsync();

        var scope = new RoomScope(db);
        await scope.PlaceInMainRoomAsync(rec.Id, userId, section.Id);

        var sut = BuildController(db, scope, userId); // extend the file's Build helper

        var page = (await sut.Get(section.Id)).Value!;

        Assert.Contains(page.Recordings, r => r.Id == rec.Id);
    }
```

> Read the real `SectionPageController.Get` return type and DTO before writing this - match them.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~FolderPage_ListsRecordingsPlacedInThatFolder"`
Expected: FAIL - the controller still reads `Recording.SectionId`, which nothing sets any more.

- [ ] **Step 3: Rewrite all six sites**

Inject `IRoomScope _rooms` into `SectionPageController` and `ChatController`; `SectionSummaryProcessor` already takes a `DiarizDbContext` - give it an `IRoomScope` too (check how it is constructed; it is resolved from a DI scope per job).

Each site becomes a placement query. Where the old shape was a `.Where(...)` on `_db.Recordings`:

```csharp
        var roomId = await _rooms.PersonalRoomIdAsync(UserId);
        // …in this room, filed under this folder or one of its children.
        var recs = from p in _db.RoomRecordings
                   where p.RoomId == roomId && p.SectionId.HasValue && allIds.Contains(p.SectionId.Value)
                   join r in _db.Recordings on p.RecordingId equals r.Id
                   select r;
```

and where it was a LINQ `from r in _db.Recordings where … join`, keep the query shape and swap the source for the same `RoomRecordings` projection.

In `SectionSummaryProcessor` the owner is `section.UserId`, not a JWT claim, so the room is `await rooms.PersonalRoomIdAsync(section.UserId, ct)`.

Delete the `SectionId.HasValue &&` guard comments that mention the in-memory provider - `p.SectionId.HasValue` on the placement is still needed for the same reason, so **keep the guard**, but update the comment to say it guards the placement's nullable folder.

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet build Diariz.slnx
dotnet test tests/Diariz.Api.Tests
dotnet test tests/Diariz.Api.IntegrationTests
```
Expected: all green.

Then confirm nothing outside the entity still reads the column:

```bash
grep -rn "\.SectionId" src/Diariz.Api --include=*.cs | grep -viE "SectionAttachment|SectionSummary|SectionMinutes|RoomRecording|req\.SectionId|p\.SectionId|placement\.SectionId"
```
Expected: no hits on `Recording`.

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api tests/
git commit -m "refactor(api): folder queries read the room placement"
```

---

## Task 6: Drop `Recording.SectionId`

**Files:**
- Modify: `src/Diariz.Domain/Entities/Recording.cs` (remove `SectionId`, `Section`)
- Modify: `src/Diariz.Domain/Entities/Section.cs` (remove the `Recordings` navigation)
- Modify: `src/Diariz.Domain/DiarizDbContext.cs` (remove the relationship config)
- Create: `src/Diariz.Domain/Migrations/<timestamp>_DropRecordingSectionId.cs`
- Test: `tests/Diariz.Api.IntegrationTests/RoomRecordingsIntegrationTests.cs` (append)

Nothing reads the column now. Removing it is what makes "the folder is a property of the placement" true rather than aspirational - leave it and someone will write to it in six months and wonder why the UI ignores them.

- [ ] **Step 1: Write the failing test**

Append to `tests/Diariz.Api.IntegrationTests/RoomRecordingsIntegrationTests.cs`:

```csharp
    /// <summary>The column is gone: the folder is a property of the placement, and there is no second, stale
    /// place to write it.</summary>
    [Fact]
    public async Task Recordings_NoLongerHasASectionIdColumn()
    {
        await using var db = fx.CreateDbContext();

        var count = await db.Database
            .SqlQuery<int>($"""
                SELECT count(*)::int AS "Value" FROM information_schema.columns
                WHERE table_name = 'Recordings' AND column_name = 'SectionId'
                """)
            .SingleAsync();

        Assert.Equal(0, count);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~Recordings_NoLongerHasASectionIdColumn"`
Expected: FAIL - `Assert.Equal() Failure: Expected 0, Actual 1`.

- [ ] **Step 3: Drop it**

In `Recording.cs`, delete:
```csharp
    public Guid? SectionId { get; set; }
    public Section? Section { get; set; }
```
In `Section.cs`, delete:
```csharp
    public ICollection<Recording> Recordings { get; set; } = new List<Recording>();
```
In `DiarizDbContext.cs`, delete the `Recording` → `Section` relationship config (the `HasOne(r => r.Section)` / `OnDelete(DeleteBehavior.SetNull)` block, and any `HasIndex` on `SectionId`).

Then:
```bash
dotnet ef migrations add DropRecordingSectionId --project src/Diariz.Domain --startup-project src/Diariz.Api
```
Confirm the generated `Up()` drops the FK, the index, and the column - and touches nothing else. The `Down()` will recreate an empty column; that is acceptable (the data lives in `RoomRecordings`, and a rollback of this phase would re-run the placement backfill's source query the other way, which is out of scope).

Fix the compile errors this produces in tests that set `SectionId` on a `Recording` fixture: they should create a placement via `RoomScope.PlaceInMainRoomAsync` instead.

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet build Diariz.slnx
dotnet test tests/Diariz.Api.Tests
dotnet test tests/Diariz.Api.IntegrationTests
cd apps/web && npm test && npx tsc --noEmit
```
Expected: all green, **zero warnings**. The web is untouched - `RecordingDto.sectionId` still exists on the wire.

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Domain src/Diariz.Api tests/
git commit -m "refactor(domain): drop Recording.SectionId; the folder belongs to the placement"
```

---

## Task 7: Docs and release

**Files:**
- Modify: `docs/Data_Schema.md`, `docs/Overall_Synopsis_of_Platform.md`
- Modify: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`
- Modify: `apps/web/src/lib/releases.ts`

Invisible to users: same routes, same payloads, same behaviour. **Build +1** (`0.118.1` → `0.118.2`), not Minor. `README.md`, `docs/features.md` and the About-box `CAPABILITIES` are **not** touched.

- [ ] **Step 1: `Data_Schema.md`**

Add the migration-history rows:

```
| `AddRoomRecordings` | `RoomRecordings` (the placement of a recording in a room; composite PK `(RoomId, RecordingId)`; `IsMainRoom` with a **filtered** unique index on `RecordingId WHERE "IsMainRoom"`; `SectionId` = the folder **within that room**, FK `ON DELETE SET NULL`; `SharedByUserId`/`SharedAt` null on the main row, enforced by `CK_RoomRecordings_MainRoomHasNoSharer`; cascade from `Rooms` and `Recordings`). The migration also **backfills**, once, one main placement per recording in its recorder's personal room, minting any missing personal room first (`RecordingPlacementBackfill`) |
| `DropRecordingSectionId` | Drops `Recordings.SectionId` — the folder is now a property of the **placement**, not of the recording, so the same recording can sit in different folders in different rooms |
```

Add a `#### RoomRecordings` section in "Tables in detail" with every column, both constraints, and the invariant: *exactly one main placement per recording, and its room is the personal room of `Recording.UserId`*. Remove `SectionId` from the `Recordings` table section and from the `Sections` relationship notes.

- [ ] **Step 2: `Overall_Synopsis_of_Platform.md`**

Under the Rooms subsection, replace "phases 2b-2d introduce `RoomRecording`…" with what now exists: the placement model, the main-room invariant and what it buys (a shared room can only unshare, never destroy), the per-room folder, and that `RoomScope.RecordingsIn(roomId)` is the base queryable every room-scoped recording query starts from. Note that 2c-2d still re-scope `Section`, `SpeakerProfile`, `ChatSession`, `MeetingType` and the RAG chunk filter.

- [ ] **Step 3: Bump the version**

`0.118.2` in `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `<Version>` in `src/Diariz.Api/Diariz.Api.csproj`.

- [ ] **Step 4: Release entry, then verify everything**

Add `RELEASES[0]` with `version: "0.118.2"`, the date, the PR number, a headline, and a prose `summary` that is honest: nothing changes for the user; a recording's folder is now recorded per-room, which is what will let the same meeting be filed differently in a shared room later. `RELEASES[0].version` must equal `version.json` (`releases.test.ts` asserts it).

```bash
dotnet build Diariz.slnx
dotnet test tests/Diariz.Api.Tests
dotnet test tests/Diariz.Api.IntegrationTests
cd apps/web && npm test && npx tsc --noEmit
```
Expected: all green, zero warnings.

- [ ] **Step 5: Commit**

```bash
git add docs version.json apps/web apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj
git commit -m "docs: recording placement; bump version and release notes"
```

---

## Deployment surface

**Server redeploy only.** No `apps/desktop/**` source changes. Two migrations apply on API startup: `AddRoomRecordings` (creates + backfills) and `DropRecordingSectionId`.

**This is the first phase with a destructive migration.** `DropRecordingSectionId` cannot be undone by re-running the app. Take a database backup before deploying, and verify the placement backfill on a copy of dev **before** the drop lands - the two migrations run back to back on startup, so there is no window to inspect in between.

## Manual verification before opening the PR

Against a **copy of dev, not a fresh database**. Restore the copy twice if needed - once to check the backfill, once end to end.

1. Before upgrading, record `SELECT count(*) FROM "Recordings"` and `SELECT count(*) FROM "Recordings" WHERE "SectionId" IS NOT NULL`.
2. Boot the new API. Confirm `SELECT count(*) FROM "RoomRecordings" WHERE "IsMainRoom"` equals the recording count, and that the number with a non-null `SectionId` matches step 1.
3. Confirm every main placement is in its recorder's personal room:
   `SELECT count(*) FROM "RoomRecordings" p JOIN "Recordings" r ON r."Id" = p."RecordingId" JOIN "Rooms" m ON m."Id" = p."RoomId" WHERE p."IsMainRoom" AND (m."Kind" <> 0 OR m."OwnerUserId" <> r."UserId");` → must be `0`.
4. Sign in. The recordings list, the folder tree, and each folder page must look **exactly** as before. Move a recording between folders, ungroup it, and reorder - all still work.
5. Create a **new user**, sign in as them, and upload a recording. They had no personal room; `RoomScope` must mint one and place the recording in it. Confirm it appears in their list.
6. `SELECT count(*) FROM "Recordings" r WHERE NOT EXISTS (SELECT 1 FROM "RoomRecordings" p WHERE p."RecordingId" = r."Id" AND p."IsMainRoom");` → must be `0`. Any non-zero means a recording is invisible to its owner.
