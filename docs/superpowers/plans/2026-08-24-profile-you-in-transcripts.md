# Profile "You in transcripts" + personal room name Implementation Plan

> **Shipped in v0.246.0 (PR #597).** Merged retrospectively on 2026-08-27 as a design record; the checkboxes below are preserved as written and are not outstanding work. Names and email addresses in the example fixtures are invented placeholders.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show the signed-in user which `Person` record their account is, and whether it carries a voiceprint; and make the personal room's name follow the display name instead of freezing at whatever it was when the room was created.

**Architecture:** The link already exists (`Person.LinkedUserId`) and is already kept in step by `PeopleDirectory.SyncFromUserAsync`; the only gap is that nothing surfaces it, because `PeopleController.List` is gated behind `ManagePeople`. So this adds a read-only projection to `GET /api/user/profile` and renders it. Separately, `RoomScope` stamps a personal room's name once at creation and never again, so a new `SyncPersonalRoomNameAsync` is called wherever `ApplicationUser.FullName` is written, plus a one-off SQL backfill migration for rooms that have already drifted.

**Tech Stack:** ASP.NET Core (.NET 10) + EF Core + Postgres; React 19 + TypeScript + Vite + Tailwind v4; xUnit (unit, in-memory provider) and Testcontainers (integration); vitest + @testing-library/react.

Source spec: [docs/superpowers/specs/2026-08-24-chat-panel-three-changes-design.md](../specs/2026-08-24-chat-panel-three-changes-design.md)

## Global Constraints

- **TDD is required.** Write the failing test, run it and see it fail, then write the minimal code. No production code without a preceding failing test.
- **A passing run has no errors or warnings.** Keep test output pristine.
- **No em dashes or en dashes in user-facing text.** Use a plain hyphen `-`. Applies to UI strings, i18n catalogs, and release notes. Code comments and internal docs are exempt.
- **Every new i18n key must be added to all four locales:** `apps/web/src/locales/{en,de,es,fr}/account.json`. `apps/web/src/locales.test.ts` fails the build if the key sets differ or any value is empty.
- **This is a fix, so it starts as a GitHub issue** (`gh issue create`) and the PR body carries `Fixes #<n>` on its own line.
- **`main` is branch-protected.** Work on a branch, push, and open a PR. Never commit to `main`, never merge locally.
- **Version bump:** this is a functional enhancement, so Minor +1 and Build reset (`0.245.0` -> `0.246.0`, unless another PR has landed first - read `version.json` and apply the rule to what is actually there).
- **Do not use `git add -A`.** This repo accumulates untracked agent scratch files; stage explicit paths only.
- **Deployment surface:** server redeploy only. Nothing here touches `apps/desktop/**`.

---

## File Structure

**Server**

| File | Responsibility |
|---|---|
| `src/Diariz.Api/Contracts/ApiDtos.cs` | Add `SelfPersonDto`; add a trailing `Person` parameter to `UserProfileDto`. |
| `src/Diariz.Api/Controllers/UserProfileController.cs` | Project the linked person on `Get`; call the room sync on `Update`; take `IRoomScope`. |
| `src/Diariz.Api/Services/RoomScope.cs` | `IRoomScope.SyncPersonalRoomNameAsync` + implementation. |
| `src/Diariz.Api/Controllers/AuthController.cs` | Call the room sync in `Setup`, beside the existing person sync. |
| `src/Diariz.Api/Services/Seeder.cs` | Call both syncs after the conditional `FullName` write. |
| `src/Diariz.Domain/Migrations/PersonalRoomNameBackfill.cs` | The backfill SQL, as a `const string` (mirrors `PersonalRoomBackfill`). |
| `src/Diariz.Domain/Migrations/<stamp>_SyncPersonalRoomNames.cs` | Empty schema migration that runs the backfill SQL. |

**Web**

| File | Responsibility |
|---|---|
| `apps/web/src/lib/types.ts` | `SelfPerson` interface; optional `person` on `UserProfile`. |
| `apps/web/src/components/ProfileSection.tsx` | Render the read-only block. |
| `apps/web/src/locales/{en,de,es,fr}/account.json` | Five new keys. |

**Tests**

| File | Responsibility |
|---|---|
| `tests/Diariz.Api.Tests/UserProfileControllerTests.cs` | Person projection; self-heal; room name after rename. |
| `tests/Diariz.Api.Tests/UserProfilePermissionsTests.cs` | Constructor call site only. |
| `tests/Diariz.Api.IntegrationTests/UserProfileIntegrationTests.cs` | Constructor call site only. |
| `tests/Diariz.Api.Tests/RoomScopeTests.cs` | `SyncPersonalRoomNameAsync` unit behaviour. |
| `tests/Diariz.Api.IntegrationTests/PersonalRoomNameBackfillTests.cs` | The backfill SQL against real Postgres. |
| `apps/web/src/components/ProfileSection.test.tsx` | The rendered block. |

---

## Task 1: `SyncPersonalRoomNameAsync` on `IRoomScope`

**Files:**
- Modify: `src/Diariz.Api/Services/RoomScope.cs`
- Test: `tests/Diariz.Api.Tests/RoomScopeTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `Task IRoomScope.SyncPersonalRoomNameAsync(Guid userId, CancellationToken ct = default)`. Sets the user's personal room `Name` to the same value `PersonalRoomIdAsync` would create it with. No-op when the user has no personal room, when the user does not exist, or when the name already matches.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Diariz.Api.Tests/RoomScopeTests.cs`. Check the file's existing `using` block first and add only what is missing.

```csharp
    /// <summary>A personal room is named once at creation and was then never touched again, so renaming
    /// yourself left the room showing the old name forever. Personal rooms are immutable
    /// (UpdateRoomAsync refuses them), so there is no hand-typed name this can clobber.</summary>
    [Fact]
    public async Task SyncPersonalRoomName_FollowsTheDisplayName()
    {
        await using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "a@b.test", Email = "a@b.test", FullName = "Old Name" });
        await db.SaveChangesAsync();
        var sut = new RoomScope(db);
        var roomId = await sut.PersonalRoomIdAsync(userId);

        db.Users.Single(u => u.Id == userId).FullName = "New Name";
        await db.SaveChangesAsync();
        await sut.SyncPersonalRoomNameAsync(userId);

        Assert.Equal("New Name", db.Rooms.Single(r => r.Id == roomId).Name);
    }

    /// <summary>Blank name falls back to the email, exactly as room creation does - otherwise clearing your
    /// display name would blank the Name column, which is required.</summary>
    [Fact]
    public async Task SyncPersonalRoomName_FallsBackToEmail_WhenTheNameIsCleared()
    {
        await using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "a@b.test", Email = "a@b.test", FullName = "Old Name" });
        await db.SaveChangesAsync();
        var sut = new RoomScope(db);
        var roomId = await sut.PersonalRoomIdAsync(userId);

        db.Users.Single(u => u.Id == userId).FullName = "   ";
        await db.SaveChangesAsync();
        await sut.SyncPersonalRoomNameAsync(userId);

        Assert.Equal("a@b.test", db.Rooms.Single(r => r.Id == roomId).Name);
    }

    /// <summary>Called on the invite-setup path before the room exists. Minting one here would create it
    /// out of order; doing nothing is correct, because creation names it correctly anyway.</summary>
    [Fact]
    public async Task SyncPersonalRoomName_DoesNothing_WhenThereIsNoPersonalRoomYet()
    {
        await using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "a@b.test", Email = "a@b.test", FullName = "A B" });
        await db.SaveChangesAsync();
        var sut = new RoomScope(db);

        await sut.SyncPersonalRoomNameAsync(userId);

        Assert.Empty(db.Rooms);
    }

    /// <summary>A shared room the user happens to own must not be renamed to their display name.</summary>
    [Fact]
    public async Task SyncPersonalRoomName_LeavesSharedRoomsAlone()
    {
        await using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "a@b.test", Email = "a@b.test", FullName = "A B" });
        var shared = new Room { Id = Guid.NewGuid(), Name = "Engineering", Kind = RoomKind.Shared, OwnerUserId = userId };
        db.Rooms.Add(shared);
        await db.SaveChangesAsync();
        var sut = new RoomScope(db);

        await sut.SyncPersonalRoomNameAsync(userId);

        Assert.Equal("Engineering", db.Rooms.Single(r => r.Id == shared.Id).Name);
    }
```

- [ ] **Step 2: Run the tests and verify they fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~SyncPersonalRoomName"
```

Expected: compile error - `IRoomScope` does not contain a definition for `SyncPersonalRoomNameAsync`. A compile failure is a legitimate red here; do not weaken the test to make it "run".

Note: `--filter "Name=..."` does not work in this repo despite what `CLAUDE.md` says. Always use `FullyQualifiedName~`.

- [ ] **Step 3: Declare the method on the interface**

In `src/Diariz.Api/Services/RoomScope.cs`, inside `interface IRoomScope`, directly after the `UpdateRoomAsync` declaration:

```csharp
    /// <summary>Point the user's Personal room name back at their display name. Personal rooms are named
    /// once at creation and are immutable to the user (<see cref="UpdateRoomAsync"/> refuses them), so the
    /// name is purely derived and this can never clobber something hand-typed. A no-op when the user has no
    /// Personal room yet - creation names it correctly, and minting one here would do it out of order.</summary>
    Task SyncPersonalRoomNameAsync(Guid userId, CancellationToken ct = default);
```

- [ ] **Step 4: Implement it**

In `class RoomScope`, directly after `UpdateRoomAsync`:

```csharp
    public async Task SyncPersonalRoomNameAsync(Guid userId, CancellationToken ct = default)
    {
        var room = await db.Rooms
            .FirstOrDefaultAsync(r => r.OwnerUserId == userId && r.Kind == RoomKind.Personal, ct);
        if (room is null) return;

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return;

        var name = Display(user);
        if (room.Name == name) return;

        room.Name = name;
        await db.SaveChangesAsync(ct);
    }
```

- [ ] **Step 5: Run the tests and verify they pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~SyncPersonalRoomName"
```

Expected: 4 passed.

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Services/RoomScope.cs tests/Diariz.Api.Tests/RoomScopeTests.cs
git commit -m "feat(api): sync a personal room's name to the owner's display name"
```

---

## Task 2: Call the sync wherever `FullName` is written

**Files:**
- Modify: `src/Diariz.Api/Controllers/UserProfileController.cs`
- Modify: `src/Diariz.Api/Controllers/AuthController.cs`
- Modify: `src/Diariz.Api/Services/Seeder.cs`
- Modify: `tests/Diariz.Api.Tests/UserProfileControllerTests.cs`
- Modify: `tests/Diariz.Api.Tests/UserProfilePermissionsTests.cs`
- Modify: `tests/Diariz.Api.IntegrationTests/UserProfileIntegrationTests.cs`

**Interfaces:**
- Consumes: `IRoomScope.SyncPersonalRoomNameAsync(Guid, CancellationToken)` from Task 1.
- Produces: `UserProfileController`'s constructor gains a **seventh** parameter, `IRoomScope rooms`, appended last:
  `UserProfileController(UserManager<ApplicationUser> users, DiarizDbContext db, ITokenService tokens, IPlatformSettingsService platform, IUserPermissions permissions, IPeopleDirectory people, IRoomScope rooms)`.

There are **three** construction sites outside the DI container. All three must be updated or the solution will not build - and a unit-test-only run will not tell you, because one of them is in the integration project.

- [ ] **Step 1: Write the failing test**

Append to `tests/Diariz.Api.Tests/UserProfileControllerTests.cs`:

```csharp
    /// <summary>The invariant: after any rename, the personal room reads the same as the display name.
    /// It used to drift silently - the person was re-synced on save and the room was not, so a production
    /// account sat under the seeded name "Platform Administrator" long after being renamed. This test is the
    /// guard against a fourth FullName write site forgetting to call the sync.</summary>
    [Fact]
    public async Task Renaming_AlsoRenamesThePersonalRoom()
    {
        using var host = new IdentityTestHost();
        var user = new ApplicationUser { UserName = "a@b.test", Email = "a@b.test", IsEnabled = true, FullName = "Old Name" };
        await host.Users.CreateAsync(user);
        var rooms = new RoomScope(host.Db);
        var roomId = await rooms.PersonalRoomIdAsync(user.Id);
        var sut = new UserProfileController(
            host.Users, host.Db, Tokens(), new PlatformSettingsService(host.Db),
            new UserPermissions(host.Db), new PeopleDirectory(host.Db), rooms)
        {
            ControllerContext = Http.Context(user.Id),
        };

        await sut.Update(new UpdateUserProfileRequest(FullName: "New Name"));

        Assert.Equal("New Name", host.Db.Rooms.Single(r => r.Id == roomId).Name);
    }
```

Before writing this, open `src/Diariz.Api/Contracts/ApiDtos.cs` and read the real `UpdateUserProfileRequest` declaration. It is a positional record with many optional parameters; construct it with **named arguments** as above so this test does not break when a field is added. If `FullName` is not the parameter name, use the real one.

- [ ] **Step 2: Run the test and verify it fails**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~Renaming_AlsoRenamesThePersonalRoom"
```

Expected: compile error on the 7-argument constructor.

- [ ] **Step 3: Add the constructor parameter and the call**

In `src/Diariz.Api/Controllers/UserProfileController.cs`, add the field, constructor parameter and assignment following the existing style (explicit fields, not a primary constructor):

```csharp
    private readonly IRoomScope _rooms;
```

```csharp
    public UserProfileController(
        UserManager<ApplicationUser> users, DiarizDbContext db, ITokenService tokens,
        IPlatformSettingsService platform, IUserPermissions permissions, IPeopleDirectory people,
        IRoomScope rooms)
    {
        _users = users;
        _db = db;
        _tokens = tokens;
        _platform = platform;
        _permissions = permissions;
        _people = people;
        _rooms = rooms;
    }
```

Then in `Update`, immediately after the existing `await _people.SyncFromUserAsync(UserId);`:

```csharp
        // A personal room is named from the display name at creation and is immutable to the user, so it has
        // to follow a rename too. It did not, and a renamed account kept showing the name it was seeded with.
        await _rooms.SyncPersonalRoomNameAsync(UserId);
```

- [ ] **Step 4: Fix the other two construction sites**

Add `, new RoomScope(host.Db)` (unit) / `, new RoomScope(db)` (integration) as the final argument:

- `tests/Diariz.Api.Tests/UserProfileControllerTests.cs` - the `BuildAsync` helper.
- `tests/Diariz.Api.Tests/UserProfilePermissionsTests.cs:26`.
- `tests/Diariz.Api.IntegrationTests/UserProfileIntegrationTests.cs:43`.

- [ ] **Step 5: Add the call to the invite-setup path**

In `src/Diariz.Api/Controllers/AuthController.cs`, in `Setup`, directly after `await _people.SyncFromUserAsync(user.Id);`:

```csharp
        await _rooms.SyncPersonalRoomNameAsync(user.Id);
```

`AuthController` does **not** currently take `IRoomScope`, so add it the same way as above. There are two construction sites outside DI, and one of them is in the **integration** project, which a unit-only test run will not compile:

- `tests/Diariz.Api.Tests/AuthControllerTests.cs:30`
- `tests/Diariz.Api.IntegrationTests/RbacIntegrationTests.cs:84`

Re-grep rather than trusting those line numbers:

```bash
grep -rn "new AuthController" --include=*.cs tests src | grep -v "/obj/"
```

At setup time the personal room usually does not exist yet, so this call is normally a no-op - it is there so the invariant holds at every write site rather than at most of them. Prove it does not crash in that case:

Append to `tests/Diariz.Api.Tests/AuthControllerTests.cs`, in its "Setup" region, reusing that file's `SeedInvited`, `BuildController` and `GoodPassword`:

```csharp
    /// <summary>Setup gives an invited account its real name for the first time, so the room has to follow.
    /// At this point the room usually does not exist yet, and the sync must cope with that rather than throw
    /// or mint one out of order - the room is created, correctly named, on first use.</summary>
    [Fact]
    public async Task Setup_LeavesThePersonalRoomNameCorrect()
    {
        using var host = new IdentityTestHost();
        var (user, token) = await SeedInvited(host, "set@x.test");

        var result = await BuildController(host)
            .Setup(new SetupRequest("set@x.test", token, "Ada Lovelace", GoodPassword));

        Assert.IsType<OkObjectResult>(result);
        var roomId = await new RoomScope(host.Db).PersonalRoomIdAsync(user.Id);
        Assert.Equal("Ada Lovelace", host.Db.Rooms.Single(r => r.Id == roomId).Name);
    }
```

Note what this proves either way: if the room already existed it must have been renamed, and if it did not it must be minted with the right name. Both are the invariant.

`BuildController` currently ends with `new PeopleDirectory(host.Db)`; append `, new RoomScope(host.Db)` to match the new constructor.

- [ ] **Step 6: Add both syncs to the Seeder**

In `src/Diariz.Api/Services/Seeder.cs`, in `SeedDefaultUserAsync`, after `await users.UpdateAsync(user);`:

```csharp
        // The seeder only fills a BLANK name, so this rarely fires - but when it does, the person and the
        // personal room must follow it like they do on every other FullName write.
        await sp.GetRequiredService<IPeopleDirectory>().SyncFromUserAsync(user.Id);
        await sp.GetRequiredService<IRoomScope>().SyncPersonalRoomNameAsync(user.Id);
```

- [ ] **Step 7: Build the whole solution**

```bash
dotnet build Diariz.slnx
```

Expected: build succeeded, 0 warnings. Building `Diariz.slnx` - not just the unit test project - is what catches the integration-project construction site.

- [ ] **Step 8: Run the API test suites**

```bash
dotnet test tests/Diariz.Api.Tests
```

Expected: all pass, including the new `Renaming_AlsoRenamesThePersonalRoom`.

- [ ] **Step 9: Commit**

```bash
git add src/Diariz.Api/Controllers/UserProfileController.cs src/Diariz.Api/Controllers/AuthController.cs src/Diariz.Api/Services/Seeder.cs tests/Diariz.Api.Tests/UserProfileControllerTests.cs tests/Diariz.Api.Tests/UserProfilePermissionsTests.cs tests/Diariz.Api.IntegrationTests/UserProfileIntegrationTests.cs
git commit -m "fix(api): keep the personal room name in step with the display name"
```

---

## Task 3: Backfill the rooms that have already drifted

**Files:**
- Create: `src/Diariz.Domain/Migrations/PersonalRoomNameBackfill.cs`
- Create: `src/Diariz.Domain/Migrations/<timestamp>_SyncPersonalRoomNames.cs` (generated)
- Test: `tests/Diariz.Api.IntegrationTests/PersonalRoomNameBackfillTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Diariz.Domain.Migrations.PersonalRoomNameBackfill.Sql` - a `public const string`.

- [ ] **Step 1: Write the failing test**

Create `tests/Diariz.Api.IntegrationTests/PersonalRoomNameBackfillTests.cs`:

```csharp
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Domain.Entities;
using Diariz.Domain.Migrations;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

/// <summary>The one-off correction for rooms that drifted before the sync existed. Asserted against the SQL
/// the migration runs, not against the shared container's accumulated state.</summary>
[Collection(IntegrationCollection.Name)]
public class PersonalRoomNameBackfillTests(ContainersFixture fx)
{
    [Fact]
    public async Task Backfill_RenamesADriftedPersonalRoom_AndIsIdempotent()
    {
        await using var db = fx.CreateDbContext();
        var user = await SeedUserAsync(db, "Ada Lovelace");
        var room = new Room
        {
            Id = Guid.NewGuid(), Name = "Platform Administrator", Kind = RoomKind.Personal, OwnerUserId = user.Id,
        };
        db.Rooms.Add(room);
        await db.SaveChangesAsync();

        // Run it twice: re-running must be safe.
        await db.Database.ExecuteSqlRawAsync(PersonalRoomNameBackfill.Sql);
        await db.Database.ExecuteSqlRawAsync(PersonalRoomNameBackfill.Sql);

        db.ChangeTracker.Clear(); // the tracked entity still holds the pre-UPDATE name
        Assert.Equal("Ada Lovelace", (await db.Rooms.SingleAsync(r => r.Id == room.Id)).Name);
    }

    [Fact]
    public async Task Backfill_LeavesSharedRoomsAlone()
    {
        await using var db = fx.CreateDbContext();
        var user = await SeedUserAsync(db, "Ada Lovelace");
        var room = new Room
        {
            Id = Guid.NewGuid(), Name = "Engineering", Kind = RoomKind.Shared, OwnerUserId = user.Id,
        };
        db.Rooms.Add(room);
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync(PersonalRoomNameBackfill.Sql);

        db.ChangeTracker.Clear();
        Assert.Equal("Engineering", (await db.Rooms.SingleAsync(r => r.Id == room.Id)).Name);
    }

    [Fact]
    public async Task Backfill_FallsBackToEmail_ForAUserWithNoDisplayName()
    {
        await using var db = fx.CreateDbContext();
        var user = await SeedUserAsync(db, null);
        var room = new Room
        {
            Id = Guid.NewGuid(), Name = "Stale", Kind = RoomKind.Personal, OwnerUserId = user.Id,
        };
        db.Rooms.Add(room);
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync(PersonalRoomNameBackfill.Sql);

        db.ChangeTracker.Clear();
        Assert.Equal(user.Email, (await db.Rooms.SingleAsync(r => r.Id == room.Id)).Name);
    }

    private static async Task<ApplicationUser> SeedUserAsync(Diariz.Domain.DiarizDbContext db, string? fullName)
    {
        var email = $"{Guid.NewGuid():N}@x.test";
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(), UserName = email, Email = email, FullName = fullName, Status = UserStatus.Active,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }
}
```

- [ ] **Step 2: Run the test and verify it fails**

Docker must be running.

```bash
dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~PersonalRoomNameBackfill"
```

Expected: compile error - `PersonalRoomNameBackfill` does not exist.

- [ ] **Step 3: Write the backfill SQL**

Create `src/Diariz.Domain/Migrations/PersonalRoomNameBackfill.cs`:

```csharp
namespace Diariz.Domain.Migrations;

/// <summary>Points every Personal room's name back at its owner's display name.
///
/// A Personal room was named once, at creation, and nothing ever re-synced it - so renaming yourself left the
/// room reading whatever your name was the day the room was minted. A production account sat under the seeded
/// "Platform Administrator" long after being renamed.
///
/// It lives in a migration, not the startup seeder, because a seeder runs on every boot; see
/// <see cref="PersonalRoomBackfill"/> for the same argument. It happens to be idempotent (the UPDATE is a
/// no-op once the names agree), which is a safety net, not a licence to run it on every boot.
///
/// The COALESCE mirrors <c>RoomScope.Display</c> exactly - keep the two in step. Magic number: Kind 0 =
/// RoomKind.Personal.</summary>
public static class PersonalRoomNameBackfill
{
    public const string Sql = """
        UPDATE "Rooms" r
        SET "Name" = COALESCE(NULLIF(TRIM(u."FullName"), ''), u."Email", 'Personal')
        FROM "AspNetUsers" u
        WHERE r."OwnerUserId" = u."Id"
          AND r."Kind" = 0
          AND r."Name" IS DISTINCT FROM COALESCE(NULLIF(TRIM(u."FullName"), ''), u."Email", 'Personal');
        """;
}
```

- [ ] **Step 4: Generate the migration**

```bash
dotnet ef migrations add SyncPersonalRoomNames --project src/Diariz.Domain --startup-project src/Diariz.Api
```

This produces an **empty** `Up`/`Down` because nothing in the model changed. That is expected. Edit the generated `Up` to read:

```csharp
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Data-only: correct the Personal room names that drifted before the rename sync existed.
            migrationBuilder.Sql(PersonalRoomNameBackfill.Sql);
        }
```

Add `using Diariz.Domain.Migrations;` if the generated file's namespace does not already make the class visible. Leave `Down` empty and say why:

```csharp
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: the old names were wrong, and the correct ones are re-derivable.
        }
```

This is **not** a destructive change, so `MaintenanceController.CurrentFormat` must **not** be bumped - an older backup restores into this schema unharmed.

- [ ] **Step 5: Run the integration tests and verify they pass**

```bash
dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~PersonalRoomNameBackfill"
```

Expected: 3 passed.

- [ ] **Step 6: Verify the migration itself applies**

```bash
dotnet build Diariz.slnx
```

Expected: build succeeded, 0 warnings. In particular, **no "pending model changes" warning** - if one appears, the generated migration was not empty and something else changed in the model; investigate before continuing.

- [ ] **Step 7: Commit**

```bash
git add src/Diariz.Domain/Migrations/PersonalRoomNameBackfill.cs src/Diariz.Domain/Migrations/*SyncPersonalRoomNames* tests/Diariz.Api.IntegrationTests/PersonalRoomNameBackfillTests.cs
git commit -m "fix(domain): backfill personal room names that drifted from their owner"
```

---

## Task 4: Project the linked person onto the profile

**Files:**
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs`
- Modify: `src/Diariz.Api/Controllers/UserProfileController.cs`
- Test: `tests/Diariz.Api.Tests/UserProfileControllerTests.cs`

**Interfaces:**
- Consumes: `UserProfileController`'s 7-argument constructor from Task 2.
- Produces: `public record SelfPersonDto(Guid Id, string Name, bool HasVoiceprint, int SampleCount, bool VoiceprintOptOut);` and a trailing `SelfPersonDto? Person = null` parameter on `UserProfileDto`. The web reads it as `profile.person`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Diariz.Api.Tests/UserProfileControllerTests.cs`:

```csharp
    /// <summary>Every account is also a Person (Person.LinkedUserId), and that person is what carries the
    /// voiceprint - but the directory that would show it is gated behind ManagePeople, so an ordinary user
    /// had no way to see their own row at all. The profile reports it read-only.</summary>
    [Fact]
    public async Task Profile_reports_the_linked_person_and_its_voiceprint()
    {
        using var host = new IdentityTestHost();
        var sut = await BuildAsync(host);
        var userId = Guid.Parse(sut.ControllerContext.HttpContext.User.FindFirst(
            System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
        await new PeopleDirectory(host.Db).EnsureForUserAsync(userId);
        var person = host.Db.People.Single(p => p.LinkedUserId == userId);
        person.SampleCount = 8;
        await host.Db.SaveChangesAsync();

        var res = await sut.Get();

        Assert.Equal(person.Id, res.Value!.Person!.Id);
        Assert.Equal(person.Name, res.Value.Person.Name);
        Assert.True(res.Value.Person.HasVoiceprint);
        Assert.Equal(8, res.Value.Person.SampleCount);
    }

    /// <summary>No samples means no voiceprint, which is the ordinary case and the one the UI has to tell
    /// the user about.</summary>
    [Fact]
    public async Task Profile_reports_no_voiceprint_when_there_are_no_samples()
    {
        using var host = new IdentityTestHost();
        var sut = await BuildAsync(host);

        var res = await sut.Get();

        Assert.False(res.Value!.Person!.HasVoiceprint);
        Assert.Equal(0, res.Value.Person.SampleCount);
    }

    /// <summary>Self-heal, mirroring PeopleController.List: an account created by a path that forgot to
    /// provision still gets a block rather than a blank one.</summary>
    [Fact]
    public async Task Profile_provisions_the_person_when_the_account_has_none()
    {
        using var host = new IdentityTestHost();
        var sut = await BuildAsync(host);
        var userId = Guid.Parse(sut.ControllerContext.HttpContext.User.FindFirst(
            System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
        host.Db.People.RemoveRange(host.Db.People.Where(p => p.LinkedUserId == userId));
        await host.Db.SaveChangesAsync();

        var res = await sut.Get();

        Assert.NotNull(res.Value!.Person);
        Assert.Equal(1, await host.Db.People.CountAsync(p => p.LinkedUserId == userId));
    }
```

`BuildAsync` currently returns only the controller. If reaching the user id through `ControllerContext` is awkward, change `BuildAsync` to return `(UserProfileController Sut, ApplicationUser User)` and update the existing tests in the file to destructure it - that is cleaner than digging claims out. Either way is fine; pick one and be consistent.

- [ ] **Step 2: Run the tests and verify they fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~UserProfileControllerTests"
```

Expected: compile error - `UserProfileDto` has no `Person`.

- [ ] **Step 3: Add the DTO**

In `src/Diariz.Api/Contracts/ApiDtos.cs`, directly above `public record UserProfileDto(`:

```csharp
/// <summary>The <c>Person</c> the signed-in account <em>is</em> (<c>Person.LinkedUserId</c>) - the name they
/// appear under in transcripts, and the row that carries their voiceprint. Read-only on the profile: the name
/// follows the account's display name, and the biometric controls live in the people UI.</summary>
public record SelfPersonDto(Guid Id, string Name, bool HasVoiceprint, int SampleCount, bool VoiceprintOptOut);
```

Then append a trailing parameter to `UserProfileDto`, after `TranscriptionLanguage`:

```csharp
    /// <summary>Who the caller is in the people directory, and whether that person has a voiceprint. Always
    /// sent; nullable only so the positional record stays source-compatible.</summary>
    SelfPersonDto? Person = null);
```

Remember to move the closing `)` and `;` off `TranscriptionLanguage` onto the new last parameter.

- [ ] **Step 4: Project it in the controller**

In `UserProfileController.Get`, before building the response:

```csharp
        // Self-heal, exactly as PeopleController.List does: an account provisioned by a path that predates
        // the directory still gets a person here rather than a blank block.
        var person = await _people.EnsureForUserAsync(UserId);
```

and add the new argument to the `new UserProfileDto(...)` call:

```csharp
            Permissions: ToDto(await _permissions.ForAsync(UserId)),
            Person: new SelfPersonDto(
                person.Id, person.Name,
                // Same rule as PersonDto: an embedding or any sample counts as having a voiceprint.
                HasVoiceprint: person.Embedding is not null || person.SampleCount > 0,
                person.SampleCount, person.VoiceprintOptOut));
```

- [ ] **Step 5: Run the tests and verify they pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~UserProfileControllerTests"
```

Expected: all pass.

- [ ] **Step 6: Regenerate the OpenAPI snapshot**

`api/user/profile` is in the published document (only `api/admin`, `api/platform`, `api/oauth` and `api/maintenance` are excluded), so the snapshot changes.

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~OpenApi"
```

Run it **twice**. The test rewrites its own snapshot, so run 1 fails and run 2 passes with no code change in between. Commit the regenerated snapshot file.

- [ ] **Step 7: Regenerate the n8n node**

```bash
cd integrations/n8n-nodes-diariz && npm run generate
```

`generated/index.ts` does **not** self-heal the way the OpenAPI snapshot does; a stale copy reds the "n8n community node" check. Commit the regenerated file.

- [ ] **Step 8: Commit**

```bash
git add src/Diariz.Api/Contracts/ApiDtos.cs src/Diariz.Api/Controllers/UserProfileController.cs tests/Diariz.Api.Tests/UserProfileControllerTests.cs
git add tests/Diariz.Api.Tests/Snapshots integrations/n8n-nodes-diariz/generated
git commit -m "feat(api): report the caller's linked person on their profile"
```

Check the real snapshot path with `git status` before staging - adjust the path above to what actually changed.

---

## Task 5: Render the block on the profile

**Files:**
- Modify: `apps/web/src/lib/types.ts`
- Modify: `apps/web/src/components/ProfileSection.tsx`
- Modify: `apps/web/src/locales/en/account.json`, and the `de`, `es`, `fr` copies
- Test: `apps/web/src/components/ProfileSection.test.tsx`

**Interfaces:**
- Consumes: the `person` field on `GET /api/user/profile` from Task 4.
- Produces: nothing later tasks depend on.

- [ ] **Step 1: Add the i18n keys**

In `apps/web/src/locales/en/account.json`, after `"linkedinPlaceholder"`:

```json
  "youInTranscripts": "You in transcripts",
  "youInTranscriptsHint": "The name you appear under on your transcripts. It follows your display name above.",
  "voiceprintSamples": "Voiceprint: {{count}} samples",
  "voiceprintNone": "No voiceprint yet. Open one of your transcripts, assign yourself as the speaker, then enrol your voice from there.",
  "voiceprintOptedOut": "You have opted out of voice-printing.",
```

Add the same five keys, translated, to `de`, `es` and `fr`. `apps/web/src/locales.test.ts` fails the build if any locale is missing a key or has an empty value. **Plain hyphens only** - no em or en dashes.

- [ ] **Step 2: Write the failing tests**

In `apps/web/src/components/ProfileSection.test.tsx`, extend the shared `PROFILE` fixture with `person: null` so existing tests keep compiling, then append:

```tsx
  it("shows the linked person and its sample count", async () => {
    vi.mocked(api.getProfile).mockResolvedValue({
      ...PROFILE,
      person: { id: "p1", name: "Ada Lovelace", hasVoiceprint: true, sampleCount: 8, voiceprintOptOut: false },
    } as never);
    renderSection();

    expect(await screen.findByText("Ada Lovelace")).toBeTruthy();
    expect(screen.getByText(/8 samples/i)).toBeTruthy();
  });

  it("tells a user with no voiceprint how to get one", async () => {
    vi.mocked(api.getProfile).mockResolvedValue({
      ...PROFILE,
      person: { id: "p1", name: "Ada Lovelace", hasVoiceprint: false, sampleCount: 0, voiceprintOptOut: false },
    } as never);
    renderSection();

    expect(await screen.findByText(/no voiceprint yet/i)).toBeTruthy();
    expect(screen.queryByText(/samples/i)).toBeNull();
  });

  it("says so when the user has opted out of voice-printing", async () => {
    vi.mocked(api.getProfile).mockResolvedValue({
      ...PROFILE,
      person: { id: "p1", name: "Ada Lovelace", hasVoiceprint: false, sampleCount: 0, voiceprintOptOut: true },
    } as never);
    renderSection();

    expect(await screen.findByText(/opted out of voice-printing/i)).toBeTruthy();
  });
```

Note: `apps/web` does **not** have `jest-dom`. Use plain truthiness assertions as above, not `toBeInTheDocument()`.

- [ ] **Step 3: Run the tests and verify they fail**

```bash
cd apps/web && npx vitest run src/components/ProfileSection.test.tsx
```

Expected: FAIL - "Ada Lovelace" is not found, because nothing renders it.

- [ ] **Step 4: Add the type**

In `apps/web/src/lib/types.ts`, above `export interface UserProfile {`:

```ts
/// The person the signed-in account IS in the people directory - the name they appear under on transcripts,
/// and the row that carries their voiceprint. Read-only: the name follows the account's display name.
export interface SelfPerson {
  id: string;
  name: string;
  hasVoiceprint: boolean;
  sampleCount: number;
  voiceprintOptOut: boolean;
}
```

and add to `UserProfile`:

```ts
  /// Optional because a server older than this field omits it; the block simply does not render then.
  person?: SelfPerson | null;
```

- [ ] **Step 5: Render the block**

In `apps/web/src/components/ProfileSection.tsx`, insert directly after the LinkedIn `<label>` block and before the job-description block:

```tsx
      {profile?.person && (
        <div className="rounded border p-2 text-sm dark:border-gray-700">
          <span className={labelSpan}>{t("youInTranscripts")}</span>
          <p className="font-medium text-gray-800 dark:text-gray-100">{profile.person.name}</p>
          <p className="mt-1 text-xs text-gray-400 dark:text-gray-500">
            {profile.person.voiceprintOptOut
              ? t("voiceprintOptedOut")
              : profile.person.hasVoiceprint
                ? t("voiceprintSamples", { count: profile.person.sampleCount })
                : t("voiceprintNone")}
          </p>
          <p className="mt-1 text-xs text-gray-400 dark:text-gray-500">{t("youInTranscriptsHint")}</p>
        </div>
      )}
```

It is read-only on purpose: the name follows the display name field above, and erasing a voiceprint or opting out already exist in the people UI for `isSelf`. Do not add controls here.

- [ ] **Step 6: Run the tests and verify they pass**

```bash
cd apps/web && npx vitest run src/components/ProfileSection.test.tsx src/locales.test.ts
```

Expected: all pass, including the locale parity gate.

- [ ] **Step 7: Typecheck and run the full web suite**

```bash
cd apps/web && npm run build && npm test
```

Expected: build succeeds, all tests pass, no warnings.

- [ ] **Step 8: Commit**

```bash
git add apps/web/src/lib/types.ts apps/web/src/components/ProfileSection.tsx apps/web/src/components/ProfileSection.test.tsx apps/web/src/locales
git commit -m "feat(web): show the signed-in user's transcript identity on their profile"
```

---

## Task 6: Verify it live

**Files:** none.

This is the step that catches what jsdom cannot: whether the block actually renders in the running app, and whether the room name really changed.

- [ ] **Step 1: Confirm which stack you are pointing at**

The local `diariz` compose stack **is production** on this machine. Before touching it:

```bash
docker exec diariz-api-1 printenv App__PublicUrl
```

If that is the production URL, do **not** run write operations against it beyond your own account's profile save. Prefer the dev stack.

- [ ] **Step 2: Rebuild and restart the affected containers**

```bash
cd deploy && docker compose up -d --build api web
```

A stale build is the single most common cause of "the fix does not work" here - MSBuild and Vite will both happily serve old code. If live and test disagree, believe the test and force a fresh build.

- [ ] **Step 3: Check the profile block**

Open the app, sign in, open Preferences -> Profile. Confirm the "You in transcripts" block shows your name and the correct voiceprint line.

- [ ] **Step 4: Check the room name**

Change your display name, save, and confirm the room switcher now shows the new name. Then confirm the backfill corrected the pre-existing drift:

```bash
docker exec diariz-postgres-1 psql -U diariz -d diariz -c "select r.\"Name\" as room, u.\"FullName\" from \"Rooms\" r join \"AspNetUsers\" u on u.\"Id\"=r.\"OwnerUserId\" where r.\"Kind\"=0;"
```

Expected: every row's `room` equals its `FullName`.

- [ ] **Step 5: Commit nothing**

Nothing to commit. Record what you saw in the PR body.

---

## Task 7: Release paperwork

**Files:**
- Modify: `version.json`, `apps/web/package.json`, `apps/web/package-lock.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`
- Modify: `apps/web/src/lib/releases.ts`
- Modify: `README.md`, `docs/features.md`
- Modify: `docs/Overall_Synopsis_of_Platform.md`, `docs/Data_Schema.md`

- [ ] **Step 1: Open the GitHub issue**

Do this **before** the PR, and write it from the user-visible symptom, not from the fix:

```bash
gh issue create --title "Personal room name does not follow the display name" --body "Renaming yourself in Preferences -> Profile updates your display name everywhere except your personal room, which keeps the name it was created with. On this deployment the room still reads \"Platform Administrator\" long after the account was renamed to \"Ada Lovelace\". Expected: the personal room's name tracks the display name (personal rooms cannot be renamed by hand, so there is nothing else it could be)."
```

Note the issue number. The PR number is usually issue + 1, but **confirm it** rather than assuming - Dependabot PRs and other issues share the sequence, and the number goes into `releases.ts` before `gh pr create` exists to report it.

- [ ] **Step 2: Bump the version everywhere**

Read `version.json`, apply Minor +1 / Build 0, and write the same value into all five mirrors. `apps/web/package-lock.json` holds it in **two** places (the root `version` and the `packages[""]` entry). `apps/web/src/lib/versionMirrors.test.ts` fails the build on any drift.

- [ ] **Step 3: Add the release entry**

Prepend to `RELEASES` in `apps/web/src/lib/releases.ts` with `version` (equal to `version.json`), `date`, `pr`, `headline`, a prose `summary`, and `added` / `fixed` bullets. `releases.test.ts` asserts `RELEASES[0].version === version.json`. Plain hyphens only.

- [ ] **Step 4: Update the inventories**

Add a row to the README **Features** table and the matching prose bullet in `docs/features.md` - always both, never one. Add the matching `CAPABILITIES` table row in `releases.ts`.

- [ ] **Step 5: Update the reference docs**

- `docs/Overall_Synopsis_of_Platform.md`: state the invariant - a personal room's name follows its owner's display name, synced at every `FullName` write.
- `docs/Data_Schema.md`: add the `SyncPersonalRoomNames` migration to the migration-history table. No column changed, so nothing else needs editing.

- [ ] **Step 6: Run everything**

```bash
dotnet build Diariz.slnx && dotnet test tests/Diariz.Api.Tests
cd apps/web && npm run build && npm test
```

Expected: green throughout, no warnings.

- [ ] **Step 7: Commit, push and open the PR**

```bash
git add version.json apps/web/package.json apps/web/package-lock.json apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj integrations/n8n-nodes-diariz/package.json apps/web/src/lib/releases.ts README.md docs/features.md docs/Overall_Synopsis_of_Platform.md docs/Data_Schema.md
git commit -m "chore: release <version>"
git push -u origin <branch>
gh pr create --title "..." --body "..."
```

The PR body must contain `Fixes #<issue>` on its own line, and must state the deployment surface: **server redeploy only, no desktop release** (nothing under `apps/desktop/src/**`, `apps/desktop/build/**`, `electron-builder.config.js`, or desktop dependencies changed; the version bump to `apps/desktop/package.json` alone does not need one).

After the merge, confirm the issue actually closed. Close it by hand if it did not.
