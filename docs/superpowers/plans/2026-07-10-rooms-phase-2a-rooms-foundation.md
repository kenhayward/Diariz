# Rooms Phase 2a: Rooms Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every user gets a real `Room` row - their Personal Room - and a `RoomScope` service that resolves it and answers "what may this caller do in this room?". Nothing else consumes it yet, so the API behaves exactly as it does today.

**Architecture:** `Room` (Personal or Shared) + `RoomMember` (a user *or* a group, carrying a `[Flags] RoomPermission`). A migration backfills one Personal room per existing user. `RoomScope` is a scoped service that finds-or-creates the caller's Personal room and unions their room permissions from their own member row plus the rows of every group they belong to (Phase 1's `UserGroup`). Nothing calls it from a controller in this phase - it lands green and dormant.

**Tech Stack:** ASP.NET Core 10, EF Core + Postgres, xUnit + Testcontainers.

**Spec:** [`docs/superpowers/specs/2026-07-10-rooms-design.md`](../specs/2026-07-10-rooms-design.md) - "Data model", "Why the main room is always the Personal Room", "Server surface → RoomScope".

---

## Why Phase 2 is split, and what this plan is not

The spec's Phase 2 is one PR. Measured against the code, it is not:

| Surface | Count |
|---|---|
| Controllers filtering by `UserId` | 21 |
| Services / MCP tools reading `UserId` | ~16 |
| Files touching `Recording.SectionId` | 14 |
| `db.Sections` / `SpeakerProfiles` / `ChatSessions` / `MeetingTypes` call sites | ~90 |

That is roughly three times Phase 1 in one commit, with no safe stopping point. It is therefore split into four sub-phases, each of which **ships on its own and is invisible to users** (the Personal Room is the only room, so behaviour cannot change):

| Sub-phase | Content | This plan? |
|---|---|---|
| **2a** | `Room`, `RoomMember`, `RoomPermission`, the personal-room backfill, and `RoomScope`. Nothing consumes it. | **Yes** |
| 2b | `RoomRecording` (the placement join), backfilled from `Recording.SectionId`; `RoomScope.Recordings`; drop `Recording.SectionId`. | No |
| 2c | `Section.UserId` → `RoomId`, and the ~90 call sites that follow. | No |
| 2d | `SpeakerProfile` / `ChatSession` / `MeetingType` re-scoped; `TranscriptChunk.UserId` dropped for the `RoomRecording` semi-join. | No |

**Out of scope here:** any controller change, any route change, `RoomRecording`, `RoomsController`, and every re-scoping. If a task in this plan makes you edit a controller, you have gone too far.

---

## Non-negotiable constraints

1. **TDD.** Failing test first, watch it fail, then the minimal code.
2. **No behaviour change.** No controller is touched. Every existing test must pass untouched. If one needs editing, stop and ask - it means something is consuming rooms that should not be.
3. **Build `Diariz.slnx`, not just the unit project,** before pushing. Controller constructor changes have a second construction site in `tests/Diariz.Api.IntegrationTests/RbacIntegrationTests.cs`.
4. **Postgres-only model config goes behind `Database.IsNpgsql()`** (see `DiarizDbContext.OnModelCreating`), or the in-memory unit tests cannot build the model.
5. **Int enums are append-only.** Never renumber a flag; Postgres stores the number.

---

## Two spec corrections this plan makes

**`Room.Name` cannot be globally unique.** The spec says unique, and also says a personal room is named after its owner. Two users called "Ken Hayward" would collide on the backfill and the migration would fail on a real database. The unique index is therefore **filtered to shared rooms**: `WHERE "Kind" = 1`. Personal room names are display labels, not identifiers.

**Personal rooms are created by `RoomScope`, not at the four user-creation sites.** Users are created in `AdminUsersController`, `AuthController`, `GoogleSignInHandler`, and `Seeder` - four places today, and a fifth tomorrow that somebody forgets. Instead `RoomScope.PersonalRoomIdAsync()` **finds-or-creates**, so the room exists by the time anything needs it and no creation site can forget. The filtered unique index on `OwnerUserId` makes the race safe; a `DbUpdateException` means another request won, so we re-read.

---

## File structure

**Create:**

| File | Responsibility |
|---|---|
| `src/Diariz.Domain/Entities/RoomPermission.cs` | The `[Flags]` enum of per-room authority. |
| `src/Diariz.Domain/Entities/RoomKind.cs` | `Personal` / `Shared`. |
| `src/Diariz.Domain/Entities/Room.cs` | The room entity. |
| `src/Diariz.Domain/Entities/RoomMember.cs` | `(RoomId, PrincipalType, PrincipalId)` + permissions. |
| `src/Diariz.Domain/Migrations/PersonalRoomBackfill.cs` | The one-time backfill SQL, shared with its test. |
| `src/Diariz.Api/Services/RoomScope.cs` | `IRoomScope` + implementation. |
| `tests/Diariz.Api.Tests/RoomModelTests.cs` | Entity round-trip on the in-memory provider. |
| `tests/Diariz.Api.Tests/RoomScopeTests.cs` | Permission union, owner override, `Require`. |
| `tests/Diariz.Api.IntegrationTests/RoomsIntegrationTests.cs` | Indexes, cascades, the backfill, find-or-create. |

**Modify:**

| File | Change |
|---|---|
| `src/Diariz.Domain/DiarizDbContext.cs` | Two `DbSet`s + `OnModelCreating` config. |
| `src/Diariz.Domain/Migrations/*` | New migration `AddRooms`. |
| `src/Diariz.Api/Program.cs` | Register `IRoomScope`. |
| `docs/Data_Schema.md`, `docs/Overall_Synopsis_of_Platform.md` | New tables + the rooms model. |
| `version.json` (+3 mirrors), `apps/web/src/lib/releases.ts` | Release. |

> **Namespaces in the snippets:** `DiarizDbContext` is in `Diariz.Domain`; entities in `Diariz.Domain.Entities`; `TestDb` / `Http` / `Perms` in `Diariz.Api.Tests.Infrastructure`. Integration tests use `[Collection(IntegrationCollection.Name)]` and `class X(ContainersFixture fx)` with `fx.CreateDbContext()`.

---

## Task 1: `RoomPermission`, `RoomKind`, `Room`, `RoomMember`

**Files:**
- Create: `src/Diariz.Domain/Entities/RoomPermission.cs`, `RoomKind.cs`, `Room.cs`, `RoomMember.cs`
- Modify: `src/Diariz.Domain/DiarizDbContext.cs`
- Test: `tests/Diariz.Api.Tests/RoomModelTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Diariz.Api.Tests/RoomModelTests.cs`:

```csharp
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

public class RoomModelTests
{
    [Fact]
    public async Task PersonalRoom_WithAnOwnerMember_RoundTrips()
    {
        using var db = TestDb.Create();
        var ownerId = Guid.NewGuid();
        var room = new Room
        {
            Id = Guid.NewGuid(),
            Name = "Ken Hayward",
            Kind = RoomKind.Personal,
            OwnerUserId = ownerId,
        };
        db.Rooms.Add(room);
        db.RoomMembers.Add(new RoomMember
        {
            RoomId = room.Id,
            PrincipalType = RoomPrincipalType.User,
            PrincipalId = ownerId,
            Permissions = RoomPermission.ManageRoom | RoomPermission.CreateRecording,
        });
        await db.SaveChangesAsync();

        var loaded = db.Rooms.Single();
        Assert.Equal(RoomKind.Personal, loaded.Kind);
        Assert.Equal(ownerId, loaded.OwnerUserId);

        var member = db.RoomMembers.Single();
        Assert.Equal(RoomPrincipalType.User, member.PrincipalType);
        Assert.True(member.Permissions.HasFlag(RoomPermission.CreateRecording));
        Assert.False(member.Permissions.HasFlag(RoomPermission.ShareOut));
    }

    [Fact]
    public void SharedRoom_HasNoOwner()
    {
        var room = new Room { Id = Guid.NewGuid(), Name = "Engineering", Kind = RoomKind.Shared };
        Assert.Null(room.OwnerUserId);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~RoomModelTests"`
Expected: FAIL to **compile** - `Room`, `RoomKind`, `RoomMember`, `RoomPrincipalType`, `RoomPermission`, `db.Rooms` do not exist.

- [ ] **Step 3: Write minimal implementation**

`src/Diariz.Domain/Entities/RoomPermission.cs`:

```csharp
namespace Diariz.Domain.Entities;

/// <summary>What a member may do inside one room. Stored as an int and APPEND-ONLY: never renumber.
///
/// RemoveOthersRecordings is named for what it does. Because a recording's main room is always its
/// recorder's Personal Room, it can only ever unshare a recording from this room - never destroy it.</summary>
[Flags]
public enum RoomPermission
{
    None = 0,
    /// <summary>Change the room's settings and membership.</summary>
    ManageRoom = 1,
    /// <summary>Record or upload into this room, and receive recordings shared into it.</summary>
    CreateRecording = 2,
    /// <summary>Remove other people's recordings from this room (unshare).</summary>
    RemoveOthersRecordings = 4,
    /// <summary>Share a recording from this room into another room.</summary>
    ShareOut = 8,
    /// <summary>Create, rename and delete folders, and move recordings between them.</summary>
    ManageContents = 16,
    /// <summary>Edit or regenerate other people's recordings (summary, minutes, actions, attachments).</summary>
    EditOthersRecordings = 32,
}
```

`src/Diariz.Domain/Entities/RoomKind.cs`:

```csharp
namespace Diariz.Domain.Entities;

/// <summary>Append-only (int in Postgres).</summary>
public enum RoomKind
{
    /// <summary>Exactly one per user, auto-created. Immutable and private: it cannot be renamed, deleted,
    /// shared, or gain members. A recording's main room is always its recorder's Personal Room.</summary>
    Personal = 0,
    Shared = 1,
}

/// <summary>A room member is a user or a group. Append-only.</summary>
public enum RoomPrincipalType
{
    User = 0,
    Group = 1,
}
```

`src/Diariz.Domain/Entities/Room.cs`:

```csharp
namespace Diariz.Domain.Entities;

/// <summary>A workspace: folders, recordings, voiceprints, chats and meeting types live in one.
///
/// A Personal room belongs to exactly one user and renders their avatar rather than a stored icon. When that
/// user is deleted the room is ORPHANED (OwnerUserId → null), not cascaded: its recordings survive in the
/// shared rooms they were shared into. An orphaned room has no members and appears in no switcher.</summary>
public class Room
{
    public Guid Id { get; set; }

    /// <summary>Unique among SHARED rooms only. Personal room names are display labels (the owner's name), and
    /// two users may legitimately share a name.</summary>
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>Icon key from the shared set. Null for personal rooms (the owner's avatar is shown).</summary>
    public string? Icon { get; set; }

    /// <summary>Hex background colour. Null for personal rooms.</summary>
    public string? Color { get; set; }

    public RoomKind Kind { get; set; }

    /// <summary>Set only for a personal room. Null on a shared room, and on an ORPHANED personal room whose
    /// owner was deleted.</summary>
    public Guid? OwnerUserId { get; set; }
    public ApplicationUser? Owner { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<RoomMember> Members { get; set; } = [];
}
```

`src/Diariz.Domain/Entities/RoomMember.cs`:

```csharp
namespace Diariz.Domain.Entities;

/// <summary>A user's or a group's membership of a room, with the permissions it confers. One table with a
/// discriminator rather than two, because resolving a caller's effective permissions unions across both and a
/// single table keeps that a single query.
///
/// No FK on PrincipalId: it points at AspNetUsers or UserGroups depending on PrincipalType. Rows are cleaned
/// up when the principal is deleted (see RoomScope and the group/user delete paths), not by the database.</summary>
public class RoomMember
{
    public Guid RoomId { get; set; }
    public Room? Room { get; set; }

    public RoomPrincipalType PrincipalType { get; set; }

    /// <summary>An `AspNetUsers.Id` when PrincipalType is User, a `UserGroups.Id` when Group.</summary>
    public Guid PrincipalId { get; set; }

    public RoomPermission Permissions { get; set; }
}
```

In `src/Diariz.Domain/DiarizDbContext.cs`, add the `DbSet`s beside `UserGroups`:

```csharp
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<RoomMember> RoomMembers => Set<RoomMember>();
```

and in `OnModelCreating`, **before** the `var isNpgsql = Database.IsNpgsql();` line (provider-agnostic config):

```csharp
        builder.Entity<Room>(e =>
        {
            e.Property(r => r.Name).HasMaxLength(128).IsRequired();
            e.HasOne(r => r.Owner).WithMany()
                .HasForeignKey(r => r.OwnerUserId)
                // A deleted user ORPHANS their personal room; its recordings survive in shared rooms.
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<RoomMember>(e =>
        {
            e.HasKey(m => new { m.RoomId, m.PrincipalType, m.PrincipalId });
            e.HasOne(m => m.Room).WithMany(r => r.Members)
                .HasForeignKey(m => m.RoomId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(m => new { m.PrincipalType, m.PrincipalId });
        });
```

and **inside** the `if (isNpgsql)` block (filtered indexes are relational-only):

```csharp
            // One personal room per user, and any number of orphaned ones (OwnerUserId null).
            builder.Entity<Room>()
                .HasIndex(r => r.OwnerUserId)
                .IsUnique()
                .HasFilter("\"OwnerUserId\" IS NOT NULL");

            // Shared room names are identifiers; personal room names are display labels (the owner's name),
            // and two users may legitimately share a name.
            builder.Entity<Room>()
                .HasIndex(r => r.Name)
                .IsUnique()
                .HasFilter("\"Kind\" = 1");
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~RoomModelTests"`
Expected: PASS (2 tests).

Then run the whole unit suite - it must be untouched: `dotnet test tests/Diariz.Api.Tests`
Expected: PASS, same count as before plus 2.

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Domain/Entities/Room.cs src/Diariz.Domain/Entities/RoomMember.cs src/Diariz.Domain/Entities/RoomKind.cs src/Diariz.Domain/Entities/RoomPermission.cs src/Diariz.Domain/DiarizDbContext.cs tests/Diariz.Api.Tests/RoomModelTests.cs
git commit -m "feat(domain): add Room, RoomMember and RoomPermission"
```

---

## Task 2: The migration and the personal-room backfill

**Files:**
- Create: `src/Diariz.Domain/Migrations/PersonalRoomBackfill.cs`
- Create: `src/Diariz.Domain/Migrations/<timestamp>_AddRooms.cs` (generated, then edited)
- Test: `tests/Diariz.Api.IntegrationTests/RoomsIntegrationTests.cs`

The backfill is a **one-way data move** and therefore lives in the migration, which `__EFMigrationsHistory` runs exactly once per database. It must **not** go in the startup seeder: a seeder runs on every boot, and would recreate a personal room for a user whose room was deliberately changed. (This is the same mistake Phase 1 made and corrected - see `RoleToGroupBackfill`.)

- [ ] **Step 1: Write the failing test**

Create `tests/Diariz.Api.IntegrationTests/RoomsIntegrationTests.cs`:

```csharp
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Diariz.Domain.Migrations;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

/// <summary>Rooms against real Postgres: the two filtered unique indexes, the orphan-on-user-delete rule, and
/// the personal-room backfill, none of which the in-memory provider honours.</summary>
[Collection(IntegrationCollection.Name)]
public class RoomsIntegrationTests(ContainersFixture fx)
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

    private static Room Personal(Guid ownerId, string name) =>
        new() { Id = Guid.NewGuid(), Name = name, Kind = RoomKind.Personal, OwnerUserId = ownerId };

    [Fact]
    public async Task OneOwnedPersonalRoom_PerUser()
    {
        await using var db = fx.CreateDbContext();
        var userId = await NewUserAsync(db);
        db.Rooms.Add(Personal(userId, "Ada"));
        await db.SaveChangesAsync();

        db.Rooms.Add(Personal(userId, "Ada again"));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    /// <summary>Personal room names are display labels: two users may share one. Only shared rooms have unique names.</summary>
    [Fact]
    public async Task TwoUsersMayShareAPersonalRoomName()
    {
        await using var db = fx.CreateDbContext();
        var a = await NewUserAsync(db);
        var b = await NewUserAsync(db);
        var name = $"Ken Hayward {Guid.NewGuid():N}";

        db.Rooms.Add(Personal(a, name));
        db.Rooms.Add(Personal(b, name));
        await db.SaveChangesAsync(); // must not throw

        Assert.Equal(2, await db.Rooms.CountAsync(r => r.Name == name));
    }

    [Fact]
    public async Task SharedRoomNames_AreUnique()
    {
        await using var db = fx.CreateDbContext();
        var name = $"Engineering {Guid.NewGuid():N}";
        db.Rooms.Add(new Room { Id = Guid.NewGuid(), Name = name, Kind = RoomKind.Shared });
        await db.SaveChangesAsync();

        db.Rooms.Add(new Room { Id = Guid.NewGuid(), Name = name, Kind = RoomKind.Shared });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    /// <summary>Deleting a user ORPHANS their personal room rather than destroying it: the recordings inside it
    /// are shared into other people's rooms, and must survive their author's departure.</summary>
    [Fact]
    public async Task DeletingUser_OrphansTheirPersonalRoom()
    {
        await using var db = fx.CreateDbContext();
        var userId = await NewUserAsync(db);
        var room = Personal(userId, $"Ada {Guid.NewGuid():N}");
        db.Rooms.Add(room);
        db.RoomMembers.Add(new RoomMember
        {
            RoomId = room.Id, PrincipalType = RoomPrincipalType.User, PrincipalId = userId,
            Permissions = RoomPermission.ManageRoom,
        });
        await db.SaveChangesAsync();

        db.Users.Remove(await db.Users.FindAsync(userId) ?? throw new InvalidOperationException("user vanished"));
        await db.SaveChangesAsync();

        var orphan = await db.Rooms.FindAsync(room.Id);
        Assert.NotNull(orphan);
        Assert.Null(orphan!.OwnerUserId);
        Assert.Equal(RoomKind.Personal, orphan.Kind);
    }

    [Fact]
    public async Task DeletingRoom_CascadesItsMembers()
    {
        await using var db = fx.CreateDbContext();
        var userId = await NewUserAsync(db);
        var room = Personal(userId, $"Ada {Guid.NewGuid():N}");
        db.Rooms.Add(room);
        db.RoomMembers.Add(new RoomMember
        {
            RoomId = room.Id, PrincipalType = RoomPrincipalType.User, PrincipalId = userId,
            Permissions = RoomPermission.ManageRoom,
        });
        await db.SaveChangesAsync();

        db.Rooms.Remove(room);
        await db.SaveChangesAsync();

        Assert.Empty(await db.RoomMembers.Where(m => m.RoomId == room.Id).ToListAsync());
    }

    /// <summary>The migration's backfill: one personal room per pre-existing user, named after them, with the
    /// owner holding every permission. Idempotent (ON CONFLICT DO NOTHING) so re-running cannot duplicate.</summary>
    [Fact]
    public async Task Backfill_GivesEveryUserAPersonalRoom_WithFullPermissions()
    {
        await using var db = fx.CreateDbContext();
        var withName = await NewUserAsync(db, "Ada Lovelace");
        var withoutName = await NewUserAsync(db);

        await db.Database.ExecuteSqlRawAsync(PersonalRoomBackfill.Sql);
        await db.Database.ExecuteSqlRawAsync(PersonalRoomBackfill.Sql); // twice: must not duplicate

        var named = await db.Rooms.SingleAsync(r => r.OwnerUserId == withName);
        Assert.Equal("Ada Lovelace", named.Name);
        Assert.Equal(RoomKind.Personal, named.Kind);

        // No display name: fall back to the email, never to an empty string.
        var unnamed = await db.Rooms.SingleAsync(r => r.OwnerUserId == withoutName);
        Assert.False(string.IsNullOrWhiteSpace(unnamed.Name));

        var member = await db.RoomMembers.SingleAsync(m => m.RoomId == named.Id);
        Assert.Equal(RoomPrincipalType.User, member.PrincipalType);
        Assert.Equal(withName, member.PrincipalId);
        foreach (var p in new[]
                 {
                     RoomPermission.ManageRoom, RoomPermission.CreateRecording, RoomPermission.RemoveOthersRecordings,
                     RoomPermission.ShareOut, RoomPermission.ManageContents, RoomPermission.EditOthersRecordings,
                 })
            Assert.True(member.Permissions.HasFlag(p));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~RoomsIntegrationTests"`
Expected: FAIL to compile (`PersonalRoomBackfill` does not exist). Once that compiles, the remaining failure is EF's `PendingModelChangesWarning` - the model changed with no migration.

- [ ] **Step 3: Write the backfill, then generate and edit the migration**

`src/Diariz.Domain/Migrations/PersonalRoomBackfill.cs`:

```csharp
namespace Diariz.Domain.Migrations;

/// <summary>Gives every pre-existing user a Personal room, owned by them, with every room permission.
///
/// It lives in the AddRooms migration - not the startup seeder - because it must run EXACTLY ONCE per
/// database. __EFMigrationsHistory guarantees that, including for an upgrading deployment. A seeder runs on
/// every boot and would silently recreate a room the user had since changed. (Phase 1 made exactly this
/// mistake with the role backfill; see RoleToGroupBackfill.)
///
/// Idempotent anyway (ON CONFLICT DO NOTHING), so re-running it in a test cannot duplicate rows. That is a
/// safety net, not a licence to run it on every boot.
///
/// 63 = every RoomPermission flag (1|2|4|8|16|32). 0 = RoomKind.Personal. 0 = RoomPrincipalType.User.</summary>
public static class PersonalRoomBackfill
{
    public const string Sql = """
        INSERT INTO "Rooms" ("Id", "Name", "Description", "Icon", "Color", "Kind", "OwnerUserId", "CreatedAt")
        SELECT gen_random_uuid(),
               COALESCE(NULLIF(TRIM(u."FullName"), ''), u."Email", 'Personal'),
               NULL, NULL, NULL, 0, u."Id", now()
        FROM "AspNetUsers" u
        WHERE NOT EXISTS (SELECT 1 FROM "Rooms" r WHERE r."OwnerUserId" = u."Id")
        ON CONFLICT DO NOTHING;

        INSERT INTO "RoomMembers" ("RoomId", "PrincipalType", "PrincipalId", "Permissions")
        SELECT r."Id", 0, r."OwnerUserId", 63
        FROM "Rooms" r
        WHERE r."Kind" = 0 AND r."OwnerUserId" IS NOT NULL
        ON CONFLICT DO NOTHING;
        """;
}
```

Then generate the migration:

```bash
dotnet ef migrations add AddRooms --project src/Diariz.Domain --startup-project src/Diariz.Api
```

Open the generated file and confirm it creates `Rooms` and `RoomMembers`, the FK `Rooms.OwnerUserId → AspNetUsers` with `onDelete: ReferentialAction.SetNull`, the `RoomMembers.RoomId` FK with `Cascade`, and **both filtered unique indexes** (`filter: "\"OwnerUserId\" IS NOT NULL"` and `filter: "\"Kind\" = 1"`). Then append the backfill as the last statement of `Up()`:

```csharp
            // One personal room per existing user. Runs exactly once per database - that is the point;
            // see PersonalRoomBackfill.
            migrationBuilder.Sql(PersonalRoomBackfill.Sql);
```

Add `using Diariz.Domain.Migrations;` to the generated file if the editor asks.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~RoomsIntegrationTests"`
Expected: PASS (6 tests).

Then the whole integration suite, because a new migration applies to the shared container database every other integration test uses: `dotnet test tests/Diariz.Api.IntegrationTests`
Expected: PASS, previous count plus 6.

> If `Backfill_GivesEveryUserAPersonalRoom_WithFullPermissions` fails with a duplicate-key error rather than passing, the migration already gave those users rooms (the fixture applies migrations before the test inserts its users). That is fine - the test's users are created *after* migration, so only the test's own `ExecuteSqlRawAsync` gives them rooms. If you see a conflict, check `ON CONFLICT DO NOTHING` is on both statements.

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Domain/Migrations tests/Diariz.Api.IntegrationTests/RoomsIntegrationTests.cs
git commit -m "feat(domain): rooms migration + one-time personal-room backfill"
```

---

## Task 3: `RoomScope` - find-or-create the personal room, resolve permissions

**Files:**
- Create: `src/Diariz.Api/Services/RoomScope.cs`
- Modify: `src/Diariz.Api/Program.cs`
- Test: `tests/Diariz.Api.Tests/RoomScopeTests.cs`

Effective permissions in a room = the union of the caller's own `RoomMember` row and the rows of **every group they belong to** (Phase 1's `UserGroupMember`), plus one override: **the owner of a personal room implicitly holds every permission**.

- [ ] **Step 1: Write the failing test**

Create `tests/Diariz.Api.Tests/RoomScopeTests.cs`:

```csharp
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

public class RoomScopeTests
{
    private static readonly RoomPermission All =
        RoomPermission.ManageRoom | RoomPermission.CreateRecording | RoomPermission.RemoveOthersRecordings |
        RoomPermission.ShareOut | RoomPermission.ManageContents | RoomPermission.EditOthersRecordings;

    private static async Task<Guid> NewUserAsync(DiarizDbContext db, string? fullName = null)
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid():N}@x.test", Email = $"{Guid.NewGuid():N}@x.test",
            FullName = fullName,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<Room> SharedRoomAsync(DiarizDbContext db, string name = "Engineering")
    {
        var room = new Room { Id = Guid.NewGuid(), Name = name, Kind = RoomKind.Shared };
        db.Rooms.Add(room);
        await db.SaveChangesAsync();
        return room;
    }

    private static async Task MemberAsync(
        DiarizDbContext db, Guid roomId, RoomPrincipalType type, Guid principalId, RoomPermission perms)
    {
        db.RoomMembers.Add(new RoomMember
        {
            RoomId = roomId, PrincipalType = type, PrincipalId = principalId, Permissions = perms,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task PersonalRoom_IsCreatedOnFirstAsk_AndNamedAfterTheUser()
    {
        using var db = TestDb.Create();
        var userId = await NewUserAsync(db, "Ada Lovelace");
        var sut = new RoomScope(db);

        var roomId = await sut.PersonalRoomIdAsync(userId);

        var room = db.Rooms.Single();
        Assert.Equal(roomId, room.Id);
        Assert.Equal("Ada Lovelace", room.Name);
        Assert.Equal(RoomKind.Personal, room.Kind);
        Assert.Equal(userId, room.OwnerUserId);
    }

    [Fact]
    public async Task PersonalRoom_IsCreatedOnce()
    {
        using var db = TestDb.Create();
        var userId = await NewUserAsync(db, "Ada");
        var sut = new RoomScope(db);

        var first = await sut.PersonalRoomIdAsync(userId);
        var second = await sut.PersonalRoomIdAsync(userId);

        Assert.Equal(first, second);
        Assert.Single(db.Rooms);
        Assert.Single(db.RoomMembers);
    }

    [Fact]
    public async Task PersonalRoom_FallsBackToEmail_WhenTheUserHasNoDisplayName()
    {
        using var db = TestDb.Create();
        var userId = await NewUserAsync(db);
        var sut = new RoomScope(db);

        await sut.PersonalRoomIdAsync(userId);

        Assert.False(string.IsNullOrWhiteSpace(db.Rooms.Single().Name));
    }

    [Fact]
    public async Task PersonalRoomOwner_HoldsEveryPermission()
    {
        using var db = TestDb.Create();
        var userId = await NewUserAsync(db, "Ada");
        var sut = new RoomScope(db);
        var roomId = await sut.PersonalRoomIdAsync(userId);

        Assert.Equal(All, await sut.PermissionsAsync(userId, roomId));
    }

    [Fact]
    public async Task SharedRoom_UnionsTheUsersOwnRowWithTheirGroupsRows()
    {
        using var db = TestDb.Create();
        var userId = await NewUserAsync(db, "Ada");
        var room = await SharedRoomAsync(db);

        var group = new UserGroup { Id = Guid.NewGuid(), Name = "Engineering" };
        db.UserGroups.Add(group);
        db.UserGroupMembers.Add(new UserGroupMember { GroupId = group.Id, UserId = userId });
        await db.SaveChangesAsync();

        await MemberAsync(db, room.Id, RoomPrincipalType.User, userId, RoomPermission.CreateRecording);
        await MemberAsync(db, room.Id, RoomPrincipalType.Group, group.Id, RoomPermission.ShareOut);

        var perms = await new RoomScope(db).PermissionsAsync(userId, room.Id);

        Assert.True(perms.HasFlag(RoomPermission.CreateRecording));
        Assert.True(perms.HasFlag(RoomPermission.ShareOut));
        Assert.False(perms.HasFlag(RoomPermission.ManageRoom));
    }

    [Fact]
    public async Task NonMember_HoldsNothing_AndIsNotAMember()
    {
        using var db = TestDb.Create();
        var stranger = await NewUserAsync(db, "Eve");
        var room = await SharedRoomAsync(db);
        var sut = new RoomScope(db);

        Assert.Equal(RoomPermission.None, await sut.PermissionsAsync(stranger, room.Id));
        Assert.False(await sut.IsMemberAsync(stranger, room.Id));
    }

    /// <summary>Membership is row existence, not "holds some permission". A member granted nothing can still
    /// see the room; if IsMemberAsync inferred membership from the flags, they would be 404'd out of it.</summary>
    [Fact]
    public async Task MemberWithNoPermissions_IsStillAMember()
    {
        using var db = TestDb.Create();
        var userId = await NewUserAsync(db, "Ada");
        var room = await SharedRoomAsync(db);
        await MemberAsync(db, room.Id, RoomPrincipalType.User, userId, RoomPermission.None);
        var sut = new RoomScope(db);

        Assert.True(await sut.IsMemberAsync(userId, room.Id));
        Assert.Equal(RoomPermission.None, await sut.PermissionsAsync(userId, room.Id));
    }

    [Fact]
    public async Task StrangerIsNotAMemberOfAnotherUsersPersonalRoom()
    {
        using var db = TestDb.Create();
        var owner = await NewUserAsync(db, "Ada");
        var stranger = await NewUserAsync(db, "Eve");
        var sut = new RoomScope(db);
        var roomId = await sut.PersonalRoomIdAsync(owner);

        Assert.True(await sut.IsMemberAsync(owner, roomId));
        Assert.False(await sut.IsMemberAsync(stranger, roomId));
    }

    /// <summary>Another user's personal room grants a stranger nothing: the owner override is keyed on OwnerUserId.</summary>
    [Fact]
    public async Task AnotherUsersPersonalRoom_GrantsAStrangerNothing()
    {
        using var db = TestDb.Create();
        var owner = await NewUserAsync(db, "Ada");
        var stranger = await NewUserAsync(db, "Eve");
        var sut = new RoomScope(db);
        var roomId = await sut.PersonalRoomIdAsync(owner);

        Assert.Equal(RoomPermission.None, await sut.PermissionsAsync(stranger, roomId));
    }

    [Fact]
    public async Task Require_ThrowsWhenThePermissionIsMissing_AndReturnsWhenHeld()
    {
        using var db = TestDb.Create();
        var userId = await NewUserAsync(db, "Ada");
        var room = await SharedRoomAsync(db);
        await MemberAsync(db, room.Id, RoomPrincipalType.User, userId, RoomPermission.CreateRecording);
        var sut = new RoomScope(db);

        await sut.RequireAsync(userId, room.Id, RoomPermission.CreateRecording); // must not throw

        var ex = await Assert.ThrowsAsync<RoomForbiddenException>(
            () => sut.RequireAsync(userId, room.Id, RoomPermission.ManageRoom));
        Assert.Contains("ManageRoom", ex.Message);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~RoomScopeTests"`
Expected: FAIL to compile - `RoomScope` and `RoomForbiddenException` do not exist.

- [ ] **Step 3: Write minimal implementation**

`src/Diariz.Api/Services/RoomScope.cs`:

```csharp
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>Thrown when a caller lacks a room permission. Translated to a 403 by the controllers that will
/// consume RoomScope in later phases.</summary>
public class RoomForbiddenException(RoomPermission required)
    : Exception($"This action requires the {required} permission in this room.")
{
    public RoomPermission Required { get; } = required;
}

/// <summary>Resolves rooms and the caller's authority inside them.
///
/// Effective permissions are the union of the caller's own RoomMember row and the rows of every group they
/// belong to, plus one override: the owner of a Personal room implicitly holds everything.
///
/// Membership is the read gate. A non-member holds RoomPermission.None and IsMemberAsync is false; callers
/// return 404 rather than 403, so a stranger cannot learn that a room exists.</summary>
public interface IRoomScope
{
    /// <summary>The caller's Personal room, created on first ask. Every user has exactly one.</summary>
    Task<Guid> PersonalRoomIdAsync(Guid userId, CancellationToken ct = default);

    Task<RoomPermission> PermissionsAsync(Guid userId, Guid roomId, CancellationToken ct = default);

    Task<bool> IsMemberAsync(Guid userId, Guid roomId, CancellationToken ct = default);

    /// <summary>Throws <see cref="RoomForbiddenException"/> unless the caller holds <paramref name="required"/>.</summary>
    Task RequireAsync(Guid userId, Guid roomId, RoomPermission required, CancellationToken ct = default);
}

public class RoomScope(DiarizDbContext db) : IRoomScope
{
    private const RoomPermission AllPermissions =
        RoomPermission.ManageRoom | RoomPermission.CreateRecording | RoomPermission.RemoveOthersRecordings |
        RoomPermission.ShareOut | RoomPermission.ManageContents | RoomPermission.EditOthersRecordings;

    public async Task<Guid> PersonalRoomIdAsync(Guid userId, CancellationToken ct = default)
    {
        var existing = await db.Rooms
            .Where(r => r.OwnerUserId == userId && r.Kind == RoomKind.Personal)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is { } id) return id;

        // Created here rather than at the four user-creation sites (AdminUsersController, AuthController,
        // GoogleSignInHandler, Seeder), so a fifth site cannot forget. The filtered unique index on
        // OwnerUserId makes the race safe: a concurrent request that wins leaves us to re-read.
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
                   ?? throw new InvalidOperationException($"No such user: {userId}");

        var room = new Room
        {
            Id = Guid.NewGuid(),
            Name = Display(user),
            Kind = RoomKind.Personal,
            OwnerUserId = userId,
        };
        db.Rooms.Add(room);
        db.RoomMembers.Add(new RoomMember
        {
            RoomId = room.Id,
            PrincipalType = RoomPrincipalType.User,
            PrincipalId = userId,
            Permissions = AllPermissions,
        });

        try
        {
            await db.SaveChangesAsync(ct);
            return room.Id;
        }
        catch (DbUpdateException)
        {
            // Another request created it between our read and our write. Theirs is as good as ours.
            db.ChangeTracker.Clear();
            return await db.Rooms
                .Where(r => r.OwnerUserId == userId && r.Kind == RoomKind.Personal)
                .Select(r => r.Id)
                .FirstAsync(ct);
        }
    }

    public async Task<RoomPermission> PermissionsAsync(Guid userId, Guid roomId, CancellationToken ct = default)
    {
        var room = await db.Rooms
            .Where(r => r.Id == roomId)
            .Select(r => new { r.Kind, r.OwnerUserId })
            .FirstOrDefaultAsync(ct);
        if (room is null) return RoomPermission.None;

        // The owner of a personal room holds everything, with no member row needed.
        if (room.Kind == RoomKind.Personal && room.OwnerUserId == userId) return AllPermissions;

        var groupIds = await db.UserGroupMembers
            .Where(m => m.UserId == userId)
            .Select(m => m.GroupId)
            .ToListAsync(ct);

        var rows = await db.RoomMembers
            .Where(m => m.RoomId == roomId
                        && ((m.PrincipalType == RoomPrincipalType.User && m.PrincipalId == userId)
                            || (m.PrincipalType == RoomPrincipalType.Group && groupIds.Contains(m.PrincipalId))))
            .Select(m => m.Permissions)
            .ToListAsync(ct);

        var result = RoomPermission.None;
        foreach (var r in rows) result |= r;
        return result;
    }

    /// <summary>Membership is row existence, NOT "holds some permission". A member granted RoomPermission.None
    /// can still see the room; inferring membership from the flags would 404 them out of a room they belong to.</summary>
    public async Task<bool> IsMemberAsync(Guid userId, Guid roomId, CancellationToken ct = default)
    {
        var room = await db.Rooms
            .Where(r => r.Id == roomId)
            .Select(r => new { r.Kind, r.OwnerUserId })
            .FirstOrDefaultAsync(ct);
        if (room is null) return false;
        if (room.Kind == RoomKind.Personal) return room.OwnerUserId == userId;

        var groupIds = await db.UserGroupMembers
            .Where(m => m.UserId == userId)
            .Select(m => m.GroupId)
            .ToListAsync(ct);

        return await db.RoomMembers.AnyAsync(
            m => m.RoomId == roomId
                 && ((m.PrincipalType == RoomPrincipalType.User && m.PrincipalId == userId)
                     || (m.PrincipalType == RoomPrincipalType.Group && groupIds.Contains(m.PrincipalId))),
            ct);
    }

    public async Task RequireAsync(Guid userId, Guid roomId, RoomPermission required, CancellationToken ct = default)
    {
        if (!(await PermissionsAsync(userId, roomId, ct)).HasFlag(required))
            throw new RoomForbiddenException(required);
    }

    /// <summary>A personal room's display name. Never empty: the Name column is required.</summary>
    private static string Display(ApplicationUser user)
    {
        if (!string.IsNullOrWhiteSpace(user.FullName)) return user.FullName!.Trim();
        if (!string.IsNullOrWhiteSpace(user.Email)) return user.Email!;
        return "Personal";
    }
}
```

Register it in `src/Diariz.Api/Program.cs`, next to `IUserPermissions`:

```csharp
builder.Services.AddScoped<IRoomScope, RoomScope>();
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~RoomScopeTests"`
Expected: PASS (10 tests).

Then: `dotnet build Diariz.slnx && dotnet test tests/Diariz.Api.Tests`
Expected: clean build, whole unit suite green with **no existing test edited**.

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Services/RoomScope.cs src/Diariz.Api/Program.cs tests/Diariz.Api.Tests/RoomScopeTests.cs
git commit -m "feat(api): RoomScope resolves the personal room and room permissions"
```

---

## Task 4: Prove find-or-create is safe on real Postgres

**Files:**
- Test: `tests/Diariz.Api.IntegrationTests/RoomsIntegrationTests.cs` (append)

The in-memory provider does not enforce the filtered unique index, so `PersonalRoomIdAsync`'s race handling is untested by Task 3. This is the whole reason that `catch (DbUpdateException)` exists.

- [ ] **Step 1: Write the failing test**

Append to `tests/Diariz.Api.IntegrationTests/RoomsIntegrationTests.cs`:

```csharp
    /// <summary>Two concurrent first-requests for the same user must yield ONE room. The filtered unique index
    /// makes the loser's insert fail, and RoomScope re-reads the winner's row.</summary>
    [Fact]
    public async Task PersonalRoomIdAsync_IsSafeUnderConcurrency()
    {
        await using var setup = fx.CreateDbContext();
        var userId = await NewUserAsync(setup, "Ada Lovelace");

        // Separate DbContexts: two in-flight requests, as ASP.NET would scope them.
        await using var dbA = fx.CreateDbContext();
        await using var dbB = fx.CreateDbContext();

        var results = await Task.WhenAll(
            new RoomScope(dbA).PersonalRoomIdAsync(userId),
            new RoomScope(dbB).PersonalRoomIdAsync(userId));

        Assert.Equal(results[0], results[1]);

        await using var check = fx.CreateDbContext();
        Assert.Single(await check.Rooms.Where(r => r.OwnerUserId == userId).ToListAsync());
        Assert.Single(await check.RoomMembers.Where(m => m.PrincipalId == userId).ToListAsync());
    }

    [Fact]
    public async Task PersonalRoomIdAsync_ReturnsTheRoomTheMigrationBackfilled()
    {
        await using var db = fx.CreateDbContext();
        var userId = await NewUserAsync(db, "Grace Hopper");
        await db.Database.ExecuteSqlRawAsync(PersonalRoomBackfill.Sql);
        var backfilled = await db.Rooms.SingleAsync(r => r.OwnerUserId == userId);

        var resolved = await new RoomScope(db).PersonalRoomIdAsync(userId);

        Assert.Equal(backfilled.Id, resolved);
        Assert.Single(await db.Rooms.Where(r => r.OwnerUserId == userId).ToListAsync());
    }
```

Add `using Diariz.Api.Services;` to the file's usings.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~PersonalRoomIdAsync_IsSafeUnderConcurrency"`
Expected: it may PASS immediately if the race does not materialise. To prove the test has teeth, temporarily comment out the `catch (DbUpdateException)` block in `RoomScope.PersonalRoomIdAsync` (rethrow instead) and re-run: it must FAIL with a unique-violation `DbUpdateException`. **Restore the catch, and confirm green.** A race test that has never been seen to fail is not a race test.

- [ ] **Step 3: No implementation needed**

Task 3 already wrote the `catch`. If the test fails for real, the bug is there, not in the test.

- [ ] **Step 4: Run the whole integration suite**

Run: `dotnet test tests/Diariz.Api.IntegrationTests`
Expected: PASS (previous count plus 8 from this plan: 6 in Task 2, 2 in Task 4).

- [ ] **Step 5: Commit**

```bash
git add tests/Diariz.Api.IntegrationTests/RoomsIntegrationTests.cs
git commit -m "test(api): personal-room find-or-create is safe under concurrency"
```

---

## Task 5: Docs and release

**Files:**
- Modify: `docs/Data_Schema.md`, `docs/Overall_Synopsis_of_Platform.md`
- Modify: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`
- Modify: `apps/web/src/lib/releases.ts`

This phase is **invisible to users**: no route, no UI, no behaviour change. Per `CLAUDE.md` that makes it a chore/refactor, so the version bumps **Build +1** (`0.118.0` → `0.118.1`), *not* Minor. `README.md`, `docs/features.md`, and the About-box `CAPABILITIES` are **not** touched - the app's user-facing scope has not changed.

- [ ] **Step 1: Update `Data_Schema.md`**

Add a migration-history row for `AddRooms`:

```
| `AddRooms` | `Rooms` (workspace; `Kind` int 0=Personal/1=Shared; `OwnerUserId` FK **`ON DELETE SET NULL`** - a deleted user's personal room is orphaned, not destroyed; filtered unique index on `OwnerUserId WHERE NOT NULL`, filtered unique index on `Name WHERE "Kind" = 1`) + `RoomMembers` (composite PK `(RoomId, PrincipalType, PrincipalId)`; principal is a user or a group; `Permissions` int **[Flags]**; cascade from `Rooms`) — the room model. The migration also **backfills** one Personal room per existing user, once (`PersonalRoomBackfill`) |
```

Add `#### Rooms` and `#### RoomMembers` sections in "Tables in detail" (mirroring the `UserGroups` / `UserGroupMembers` sections added in Phase 1): every column, both filtered indexes, the `SetNull` FK and why, the `[Flags] RoomPermission` values (`ManageRoom=1`, `CreateRecording=2`, `RemoveOthersRecordings=4`, `ShareOut=8`, `ManageContents=16`, `EditOthersRecordings=32`, append-only), and the note that `RoomMember.PrincipalId` carries **no FK** because it points at one of two tables.

- [ ] **Step 2: Update `Overall_Synopsis_of_Platform.md`**

Under the RBAC section added in Phase 1, add a **Rooms** subsection stating: a room is a workspace; every user has exactly one immutable Personal room; a recording's main room is always its recorder's Personal room; `RoomScope` resolves the personal room (find-or-create) and unions a caller's room permissions from their own member row and their groups'; the personal-room owner implicitly holds everything; membership is the read gate (non-members get 404, not 403); and that **nothing consumes `RoomScope` yet** - phases 2b-2d wire it into the controllers.

- [ ] **Step 3: Bump the version**

Set `0.118.1` in `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, and `<Version>` in `src/Diariz.Api/Diariz.Api.csproj`.

- [ ] **Step 4: Add the release entry and verify everything**

Add a new `RELEASES[0]` to `apps/web/src/lib/releases.ts` with `version: "0.118.1"`, the date, the PR number, `headline`, a prose `summary`, and an `added` list. The summary must be honest that this is groundwork with no user-visible change: rooms exist in the database, every account now has a Personal room, and nothing uses it yet.

`RELEASES[0].version` must equal `version.json` - `releases.test.ts` asserts it.

Then:

```bash
dotnet build Diariz.slnx
dotnet test tests/Diariz.Api.Tests
dotnet test tests/Diariz.Api.IntegrationTests
cd apps/web && npm test && npx tsc --noEmit
```
Expected: all green, **zero warnings**.

- [ ] **Step 5: Commit**

```bash
git add docs version.json apps/web apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj
git commit -m "docs: rooms foundation; bump version and release notes"
```

---

## Deployment surface

**Server redeploy only.** No `apps/desktop/**` source changes; the desktop `package.json` bump is the lockstep mirror. The `AddRooms` migration applies on API startup and backfills a personal room per user.

## Manual verification before opening the PR

Run against a **copy of dev, not a fresh database** - the backfill only matters on data that predates it. (Phase 1 did exactly this and it caught a bug that 1,124 unit tests missed.)

1. Boot against a pre-rooms database. Confirm every existing user now has exactly one `Rooms` row with `Kind = 0`, named after them, and one `RoomMembers` row with `Permissions = 63`.
2. Confirm the API behaves **identically**: sign in, list recordings, open one, record. Nothing in this phase should be observable.
3. Restart the API. Confirm no new rooms appear - the backfill is in the migration, not the seeder, so it must not run twice.
4. Create a new user through Manage Users. They have **no** room yet (nothing has called `RoomScope`). This is expected; 2b is the first phase where a request creates one.
5. `SELECT count(*) FROM "Rooms" WHERE "OwnerUserId" IS NULL;` - must be `0` on a healthy database (orphans only appear when a user is deleted).
