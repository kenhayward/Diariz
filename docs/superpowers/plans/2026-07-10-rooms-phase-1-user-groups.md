# Rooms Phase 1: User Groups Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the three Identity roles (`Standard` / `Administrator` / `PlatformAdministrator`) with User Groups carrying platform permissions, without changing what any existing user can do.

**Architecture:** A `UserGroup` holds a `[Flags] PlatformPermission`. Users belong to groups via `UserGroupMember`. A scoped `IUserPermissions` service unions a caller's group flags **from the database on every request** (a JWT claim would go stale when membership changes). An `IAuthorizationHandler` backs policies `ManageRooms` / `ManageUsers` / `ManagePlatform` / `ReadAdminSettings`, replacing `[Authorize(Policy = "Admin")]`, `[Authorize(Roles = ...)]`, and six `User.IsInRole(...)` calls. A seeder creates two groups mirroring today's privilege boundary exactly. The web reads permissions from `GET /api/user/profile` rather than decoding the JWT.

**Tech Stack:** ASP.NET Core 10, EF Core + Postgres, xUnit + Testcontainers, React 19 + TS + vitest.

**Spec:** [`docs/superpowers/specs/2026-07-10-rooms-design.md`](../specs/2026-07-10-rooms-design.md) - "Permissions", "Platform authorization", "Migration → Migration 1".

**Out of scope:** Rooms, `RoomPermission`, `RoomScope`, room-scoped anything. Phase 1 ships on its own: roles become groups, nothing else changes.

---

## Non-negotiable constraints

1. **TDD.** Every task writes a failing test first, watches it fail, then writes the minimal code. No production code without a preceding red test.
2. **No privilege change.** After this phase, every user can do exactly what they could before. The two seeded groups exist precisely to preserve today's boundary: `Administrator` cannot reach `MaintenanceController` or write platform settings, and must not gain that ability.
3. **The seed user stays undeletable.** The `Platform Administrators` group is `IsSystem`, cannot be deleted, and cannot have its last member removed.
4. **Build `Diariz.slnx`, not just the unit test project,** before pushing. Controller constructor changes have a second construction site in `tests/Diariz.Api.IntegrationTests/RbacIntegrationTests.cs`, and a unit-only run will not catch the break.
5. **No em or en dashes in user-facing text** (UI strings, locale catalogues, release notes). Plain hyphen only.

---

## File structure

**Create:**

| File | Responsibility |
|---|---|
| `src/Diariz.Domain/Entities/PlatformPermission.cs` | The `[Flags]` enum. |
| `src/Diariz.Domain/Entities/UserGroup.cs` | Group entity. |
| `src/Diariz.Domain/Entities/UserGroupMember.cs` | `(GroupId, UserId)` join. |
| `src/Diariz.Api/Auth/PermissionRequirement.cs` | Authorization requirement carrying the flags. |
| `src/Diariz.Api/Auth/PermissionAuthorizationHandler.cs` | Succeeds when the caller holds **any** required flag. |
| `src/Diariz.Api/Services/UserPermissions.cs` | `IUserPermissions` + implementation: union a user's group flags. |
| `src/Diariz.Api/Controllers/GroupsController.cs` | Group CRUD + membership. |
| `tests/Diariz.Api.Tests/UserPermissionsTests.cs` | Union, empty, system-group behaviour. |
| `tests/Diariz.Api.Tests/PermissionAuthorizationHandlerTests.cs` | Any-of semantics, unauthenticated caller. |
| `tests/Diariz.Api.Tests/GroupsControllerTests.cs` | CRUD, IsSystem guard, last-member guard. |
| `tests/Diariz.Api.IntegrationTests/UserGroupsIntegrationTests.cs` | Unique index, cascades, seeder idempotency + role migration. |
| `apps/web/src/components/GroupsTab.tsx` | The Groups tab body. |
| `apps/web/src/components/GroupsTab.test.tsx` | Its tests. |

**Modify:**

| File | Change |
|---|---|
| `src/Diariz.Domain/DiarizDbContext.cs` | `DbSet`s + `OnModelCreating` config. |
| `src/Diariz.Domain/Migrations/*` | New migration `AddUserGroups`. |
| `src/Diariz.Api/Program.cs:143` | Replace the `"Admin"` policy with four permission policies; register `IUserPermissions`. |
| `src/Diariz.Api/Services/Seeder.cs` | `SeedGroupsAsync` + role→group migration. |
| `src/Diariz.Api/Services/TokenService.cs:37` | Stop writing role claims. |
| `src/Diariz.Api/Controllers/AdminUsersController.cs` | Policy swap; `AccountTypeOf`/`IsPlatformAdmin` become group-based; role endpoints removed. |
| `src/Diariz.Api/Controllers/PlatformSettingsController.cs` | `ReadAdminSettings` on the class, `ManagePlatform` on writes. |
| `src/Diariz.Api/Controllers/MaintenanceController.cs:16` | `[Authorize(Policy = "ManagePlatform")]`. |
| `src/Diariz.Api/Controllers/MeetingTypesController.cs:25` | `IsPlatformAdmin` → `IUserPermissions`. |
| `src/Diariz.Api/Controllers/UserProfileController.cs` | Profile returns the caller's permissions. |
| `tests/Diariz.Api.TestSupport/Http.cs` | Keep role overload (unused) or delete; add nothing. |
| `apps/web/src/lib/jwt.ts` | Delete `rolesFromToken` / `isAdminFromToken` / `isPlatformAdminFromToken`. |
| `apps/web/src/lib/api.ts` | Group endpoints; drop `setUserRole`. |
| `apps/web/src/components/ManageUsersModal.tsx` | Tabs: Users + Groups; account-type column → group chips. |
| `docs/Data_Schema.md`, `docs/Overall_Synopsis_of_Platform.md` | New tables + auth model. |
| `README.md`, `docs/features.md`, `apps/web/src/lib/releases.ts`, `version.json` (+3 mirrors) | Release. |

---

> **Namespaces in the C# snippets below.** `DiarizDbContext` lives in `Diariz.Domain`, the entities in
> `Diariz.Domain.Entities`, and `TestDb` / `Http` in `Diariz.Api.Tests.Infrastructure`. Add the `using`
> lines your editor asks for; they are omitted from some snippets for brevity.

## Task 1: The `PlatformPermission` enum and group entities

**Files:**
- Create: `src/Diariz.Domain/Entities/PlatformPermission.cs`
- Create: `src/Diariz.Domain/Entities/UserGroup.cs`
- Create: `src/Diariz.Domain/Entities/UserGroupMember.cs`
- Modify: `src/Diariz.Domain/DiarizDbContext.cs`
- Test: `tests/Diariz.Api.Tests/UserGroupModelTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Diariz.Api.Tests/UserGroupModelTests.cs`:

```csharp
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

public class UserGroupModelTests
{
    [Fact]
    public async Task Group_WithMembers_RoundTrips()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var group = new UserGroup
        {
            Id = Guid.NewGuid(),
            Name = "Administrators",
            Permissions = PlatformPermission.ManageUsers | PlatformPermission.ManageRooms,
        };
        db.UserGroups.Add(group);
        db.UserGroupMembers.Add(new UserGroupMember { GroupId = group.Id, UserId = userId });
        await db.SaveChangesAsync();

        var loaded = db.UserGroups.Single();
        Assert.Equal("Administrators", loaded.Name);
        Assert.True(loaded.Permissions.HasFlag(PlatformPermission.ManageUsers));
        Assert.False(loaded.Permissions.HasFlag(PlatformPermission.ManagePlatform));
        Assert.False(loaded.IsSystem);
        Assert.Equal(userId, db.UserGroupMembers.Single().UserId);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~UserGroupModelTests"`
Expected: FAIL to **compile** - `UserGroup`, `UserGroupMember`, `PlatformPermission`, `db.UserGroups` do not exist.

- [ ] **Step 3: Write minimal implementation**

`src/Diariz.Domain/Entities/PlatformPermission.cs`:

```csharp
namespace Diariz.Domain.Entities;

/// <summary>Platform-level authority, granted through <see cref="UserGroup"/> membership. Stored as an
/// int and APPEND-ONLY: never renumber an existing flag (Postgres holds the numeric value).</summary>
[Flags]
public enum PlatformPermission
{
    None = 0,
    /// <summary>Create, edit and delete rooms and their membership. NOT a grant to read a room's contents.</summary>
    ManageRooms = 1,
    /// <summary>Create, edit, enable and delete users and groups.</summary>
    ManageUsers = 2,
    /// <summary>Read and write platform settings, and run maintenance (backup / restore).</summary>
    ManagePlatform = 4,
}
```

`src/Diariz.Domain/Entities/UserGroup.cs`:

```csharp
namespace Diariz.Domain.Entities;

/// <summary>A named collection of users carrying platform permissions. Replaces the old Identity roles.
/// A system group (IsSystem) cannot be deleted and cannot be left without members.</summary>
public class UserGroup
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    /// <summary>Icon key from the shared icon set (see MeetingTypeIcon).</summary>
    public string? Icon { get; set; }
    /// <summary>Background colour, hex (e.g. "#5C6BC0").</summary>
    public string? Color { get; set; }
    public PlatformPermission Permissions { get; set; }
    /// <summary>Seeded and protected: the Platform Administrators group.</summary>
    public bool IsSystem { get; set; }

    public ICollection<UserGroupMember> Members { get; set; } = [];
}
```

`src/Diariz.Domain/Entities/UserGroupMember.cs`:

```csharp
namespace Diariz.Domain.Entities;

/// <summary>Join row: a user's membership of a group. Composite key (GroupId, UserId).</summary>
public class UserGroupMember
{
    public Guid GroupId { get; set; }
    public UserGroup? Group { get; set; }

    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }
}
```

In `src/Diariz.Domain/DiarizDbContext.cs`, add the `DbSet`s next to the others (after line 35):

```csharp
    public DbSet<UserGroup> UserGroups => Set<UserGroup>();
    public DbSet<UserGroupMember> UserGroupMembers => Set<UserGroupMember>();
```

and in `OnModelCreating` (before the Npgsql-only `Database.IsNpgsql()` block, since this config is provider-agnostic):

```csharp
        b.Entity<UserGroup>(e =>
        {
            e.HasIndex(g => g.Name).IsUnique();
            e.Property(g => g.Name).HasMaxLength(128).IsRequired();
        });

        b.Entity<UserGroupMember>(e =>
        {
            e.HasKey(m => new { m.GroupId, m.UserId });
            e.HasOne(m => m.Group).WithMany(g => g.Members)
                .HasForeignKey(m => m.GroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(m => m.User).WithMany()
                .HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        });
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~UserGroupModelTests"`
Expected: PASS (1 test).

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Domain/Entities/PlatformPermission.cs src/Diariz.Domain/Entities/UserGroup.cs src/Diariz.Domain/Entities/UserGroupMember.cs src/Diariz.Domain/DiarizDbContext.cs tests/Diariz.Api.Tests/UserGroupModelTests.cs
git commit -m "feat(domain): add UserGroup, UserGroupMember and PlatformPermission"
```

---

## Task 2: The migration

**Files:**
- Create: `src/Diariz.Domain/Migrations/<timestamp>_AddUserGroups.cs` (generated)
- Test: `tests/Diariz.Api.IntegrationTests/UserGroupsIntegrationTests.cs`

The in-memory provider does not enforce unique indexes or FK cascades, so this is verified against real Postgres.

- [ ] **Step 1: Write the failing test**

Create `tests/Diariz.Api.IntegrationTests/UserGroupsIntegrationTests.cs`:

```csharp
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

[Collection("integration")]
public class UserGroupsIntegrationTests(ContainersFixture fx)
{
    private async Task<Guid> NewUserAsync(DiarizDbContext db)
    {
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"u{Guid.NewGuid():N}@x.test" };
        user.Email = user.UserName;
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    [Fact]
    public async Task GroupName_IsUnique()
    {
        await using var db = fx.CreateDbContext();
        var name = $"Group {Guid.NewGuid():N}";
        db.UserGroups.Add(new UserGroup { Id = Guid.NewGuid(), Name = name });
        await db.SaveChangesAsync();

        db.UserGroups.Add(new UserGroup { Id = Guid.NewGuid(), Name = name });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task DeletingGroup_CascadesMembers_ButKeepsUsers()
    {
        await using var db = fx.CreateDbContext();
        var userId = await NewUserAsync(db);
        var group = new UserGroup { Id = Guid.NewGuid(), Name = $"G {Guid.NewGuid():N}" };
        db.UserGroups.Add(group);
        db.UserGroupMembers.Add(new UserGroupMember { GroupId = group.Id, UserId = userId });
        await db.SaveChangesAsync();

        db.UserGroups.Remove(group);
        await db.SaveChangesAsync();

        Assert.Empty(await db.UserGroupMembers.Where(m => m.GroupId == group.Id).ToListAsync());
        Assert.NotNull(await db.Users.FindAsync(userId));
    }

    [Fact]
    public async Task DeletingUser_CascadesTheirMemberships()
    {
        await using var db = fx.CreateDbContext();
        var userId = await NewUserAsync(db);
        var group = new UserGroup { Id = Guid.NewGuid(), Name = $"G {Guid.NewGuid():N}" };
        db.UserGroups.Add(group);
        db.UserGroupMembers.Add(new UserGroupMember { GroupId = group.Id, UserId = userId });
        await db.SaveChangesAsync();

        db.Users.Remove(await db.Users.FindAsync(userId) ?? throw new InvalidOperationException());
        await db.SaveChangesAsync();

        Assert.Empty(await db.UserGroupMembers.Where(m => m.UserId == userId).ToListAsync());
        Assert.NotNull(await db.UserGroups.FindAsync(group.Id));
    }
}
```

> Check `ContainersFixture` for the exact fixture-injection style used by a neighbouring test class (constructor parameter vs `ICollectionFixture` property) and match it. Do not invent a new style.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~UserGroupsIntegrationTests"`
Expected: FAIL - the `UserGroups` table does not exist (`relation "UserGroups" does not exist`).

- [ ] **Step 3: Generate the migration**

```bash
dotnet ef migrations add AddUserGroups --project src/Diariz.Domain --startup-project src/Diariz.Api
```

Open the generated file and confirm: `UserGroups` (with the unique index on `Name`), `UserGroupMembers` (composite PK, both FKs `ON DELETE CASCADE`). No data changes here - the role migration lives in the seeder (Task 5) so it also backfills existing deployments on boot, which a migration cannot do idempotently across re-runs.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~UserGroupsIntegrationTests"`
Expected: PASS (3 tests). The API auto-applies migrations on startup, and `ContainersFixture` applies them for the fixture.

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Domain/Migrations tests/Diariz.Api.IntegrationTests/UserGroupsIntegrationTests.cs
git commit -m "feat(domain): migration for user groups, with cascade + uniqueness tests"
```

---

## Task 3: `IUserPermissions` - union a caller's group flags

**Files:**
- Create: `src/Diariz.Api/Services/UserPermissions.cs`
- Test: `tests/Diariz.Api.Tests/UserPermissionsTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Diariz.Api.Tests/UserPermissionsTests.cs`:

```csharp
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

public class UserPermissionsTests
{
    private static async Task<Guid> AddGroupWithMember(DiarizDbContext db, Guid userId, PlatformPermission perms, string name)
    {
        var group = new UserGroup { Id = Guid.NewGuid(), Name = name, Permissions = perms };
        db.UserGroups.Add(group);
        db.UserGroupMembers.Add(new UserGroupMember { GroupId = group.Id, UserId = userId });
        await db.SaveChangesAsync();
        return group.Id;
    }

    [Fact]
    public async Task NoGroups_GrantsNothing()
    {
        using var db = TestDb.Create();
        var sut = new UserPermissions(db);
        Assert.Equal(PlatformPermission.None, await sut.ForAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task MembershipOfTwoGroups_UnionsTheirFlags()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await AddGroupWithMember(db, userId, PlatformPermission.ManageUsers, "A");
        await AddGroupWithMember(db, userId, PlatformPermission.ManagePlatform, "B");

        var perms = await new UserPermissions(db).ForAsync(userId);

        Assert.True(perms.HasFlag(PlatformPermission.ManageUsers));
        Assert.True(perms.HasFlag(PlatformPermission.ManagePlatform));
        Assert.False(perms.HasFlag(PlatformPermission.ManageRooms));
    }

    [Fact]
    public async Task AnotherUsersGroup_GrantsNothing()
    {
        using var db = TestDb.Create();
        await AddGroupWithMember(db, Guid.NewGuid(), PlatformPermission.ManagePlatform, "A");
        Assert.Equal(PlatformPermission.None, await new UserPermissions(db).ForAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task HasAsync_IsAnyOf_NotAllOf()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await AddGroupWithMember(db, userId, PlatformPermission.ManageUsers, "A");
        var sut = new UserPermissions(db);

        Assert.True(await sut.HasAsync(userId, PlatformPermission.ManageUsers | PlatformPermission.ManagePlatform));
        Assert.False(await sut.HasAsync(userId, PlatformPermission.ManagePlatform));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~UserPermissionsTests"`
Expected: FAIL to compile - `UserPermissions` does not exist.

- [ ] **Step 3: Write minimal implementation**

`src/Diariz.Api/Services/UserPermissions.cs`:

```csharp
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>Resolves a user's platform permissions as the union of the flags on every group they belong to.
/// Read from the database per request rather than from a JWT claim: a claim would go stale the moment a
/// user is added to or removed from a group, and would keep working until their token expired.</summary>
public interface IUserPermissions
{
    Task<PlatformPermission> ForAsync(Guid userId, CancellationToken ct = default);

    /// <summary>True when the user holds ANY of the requested flags.</summary>
    Task<bool> HasAsync(Guid userId, PlatformPermission anyOf, CancellationToken ct = default);
}

public class UserPermissions(DiarizDbContext db) : IUserPermissions
{
    public async Task<PlatformPermission> ForAsync(Guid userId, CancellationToken ct = default)
    {
        var flags = await db.UserGroupMembers
            .Where(m => m.UserId == userId)
            .Select(m => m.Group!.Permissions)
            .ToListAsync(ct);

        var result = PlatformPermission.None;
        foreach (var f in flags) result |= f;
        return result;
    }

    public async Task<bool> HasAsync(Guid userId, PlatformPermission anyOf, CancellationToken ct = default) =>
        (await ForAsync(userId, ct) & anyOf) != PlatformPermission.None;
}
```

> `Select(m => m.Group!.Permissions)` requires the `Group` navigation to be loadable. The in-memory provider resolves it from the change tracker; Postgres joins. If the in-memory test returns `None`, use `db.UserGroups.Where(g => g.Members.Any(m => m.UserId == userId))` instead - it works on both.

Register it in `src/Diariz.Api/Program.cs`, next to the other scoped services:

```csharp
builder.Services.AddScoped<IUserPermissions, UserPermissions>();
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~UserPermissionsTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Services/UserPermissions.cs src/Diariz.Api/Program.cs tests/Diariz.Api.Tests/UserPermissionsTests.cs
git commit -m "feat(api): IUserPermissions resolves a caller's unioned group flags"
```

---

## Task 4: The authorization handler and policies

**Files:**
- Create: `src/Diariz.Api/Auth/PermissionRequirement.cs`
- Create: `src/Diariz.Api/Auth/PermissionAuthorizationHandler.cs`
- Modify: `src/Diariz.Api/Program.cs:143`
- Test: `tests/Diariz.Api.Tests/PermissionAuthorizationHandlerTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Diariz.Api.Tests/PermissionAuthorizationHandlerTests.cs`:

```csharp
using System.Security.Claims;
using Diariz.Api.Auth;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;

namespace Diariz.Api.Tests;

public class PermissionAuthorizationHandlerTests
{
    private static ClaimsPrincipal User(Guid id) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString())], "test"));

    private static async Task<AuthorizationHandlerContext> Evaluate(
        DiarizDbContext db, ClaimsPrincipal user, PlatformPermission required)
    {
        var requirement = new PermissionRequirement(required);
        var ctx = new AuthorizationHandlerContext([requirement], user, resource: null);
        await new PermissionAuthorizationHandler(new UserPermissions(db)).HandleAsync(ctx);
        return ctx;
    }

    [Fact]
    public async Task Succeeds_WhenUserHoldsTheFlag()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var group = new UserGroup { Id = Guid.NewGuid(), Name = "A", Permissions = PlatformPermission.ManageUsers };
        db.UserGroups.Add(group);
        db.UserGroupMembers.Add(new UserGroupMember { GroupId = group.Id, UserId = userId });
        await db.SaveChangesAsync();

        var ctx = await Evaluate(db, User(userId), PlatformPermission.ManageUsers);
        Assert.True(ctx.HasSucceeded);
    }

    [Fact]
    public async Task Succeeds_WhenUserHoldsAnyOfSeveralFlags()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var group = new UserGroup { Id = Guid.NewGuid(), Name = "A", Permissions = PlatformPermission.ManageUsers };
        db.UserGroups.Add(group);
        db.UserGroupMembers.Add(new UserGroupMember { GroupId = group.Id, UserId = userId });
        await db.SaveChangesAsync();

        var ctx = await Evaluate(db, User(userId), PlatformPermission.ManageUsers | PlatformPermission.ManagePlatform);
        Assert.True(ctx.HasSucceeded);
    }

    [Fact]
    public async Task Fails_WhenUserHoldsNone()
    {
        using var db = TestDb.Create();
        var ctx = await Evaluate(db, User(Guid.NewGuid()), PlatformPermission.ManagePlatform);
        Assert.False(ctx.HasSucceeded);
    }

    [Fact]
    public async Task Fails_ForAnonymousCaller()
    {
        using var db = TestDb.Create();
        var ctx = await Evaluate(db, new ClaimsPrincipal(new ClaimsIdentity()), PlatformPermission.ManageUsers);
        Assert.False(ctx.HasSucceeded);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~PermissionAuthorizationHandlerTests"`
Expected: FAIL to compile - `PermissionRequirement` / `PermissionAuthorizationHandler` do not exist.

- [ ] **Step 3: Write minimal implementation**

`src/Diariz.Api/Auth/PermissionRequirement.cs`:

```csharp
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;

namespace Diariz.Api.Auth;

/// <summary>Requires the caller to hold ANY of <paramref name="AnyOf"/>. "Any", not "all", so a single
/// policy can express "manage users OR manage platform".</summary>
public record PermissionRequirement(PlatformPermission AnyOf) : IAuthorizationRequirement;
```

`src/Diariz.Api/Auth/PermissionAuthorizationHandler.cs`:

```csharp
using System.Security.Claims;
using Diariz.Api.Services;
using Microsoft.AspNetCore.Authorization;

namespace Diariz.Api.Auth;

/// <summary>Backs the platform-permission policies. Reads the caller's group flags from the database on
/// every request, so a membership change takes effect immediately rather than at token expiry.</summary>
public class PermissionAuthorizationHandler(IUserPermissions permissions)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var raw = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(raw, out var userId)) return; // anonymous or malformed: fail closed

        if (await permissions.HasAsync(userId, requirement.AnyOf))
            context.Succeed(requirement);
    }
}
```

In `src/Diariz.Api/Program.cs`, register the handler alongside the other services:

```csharp
builder.Services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
```

and **replace** line 143 (`o.AddPolicy("Admin", ...)`) with:

```csharp
    o.AddPolicy("ManageRooms", p => p.AddRequirements(new PermissionRequirement(PlatformPermission.ManageRooms)));
    o.AddPolicy("ManageUsers", p => p.AddRequirements(new PermissionRequirement(PlatformPermission.ManageUsers)));
    o.AddPolicy("ManagePlatform", p => p.AddRequirements(new PermissionRequirement(PlatformPermission.ManagePlatform)));
    // Reading platform settings: the Manage Users modal shows the default quota, so an Administrator
    // (ManageUsers, no ManagePlatform) must still be able to GET them. Writes remain ManagePlatform.
    o.AddPolicy("ReadAdminSettings", p => p.AddRequirements(
        new PermissionRequirement(PlatformPermission.ManageUsers | PlatformPermission.ManagePlatform)));
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~PermissionAuthorizationHandlerTests"`
Expected: PASS (4 tests). The solution will not build yet if `"Admin"` is still referenced - that is Task 6.

Run: `dotnet build Diariz.slnx` and note which files still reference `"Admin"`. That is exactly the Task 6 worklist.

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Auth src/Diariz.Api/Program.cs tests/Diariz.Api.Tests/PermissionAuthorizationHandlerTests.cs
git commit -m "feat(api): permission-based authorization policies"
```

---

## Task 5: Seed the two groups and migrate role holders

**Files:**
- Modify: `src/Diariz.Api/Services/Seeder.cs`
- Test: `tests/Diariz.Api.IntegrationTests/UserGroupsIntegrationTests.cs` (append)

The seeder, not the migration, does the role→group move: it runs on every boot, so it backfills existing deployments and is idempotent by construction.

**Mapping (preserves today's privilege boundary exactly):**

| Old role | New group | Flags |
|---|---|---|
| `PlatformAdministrator` | `Platform Administrators` (`IsSystem`) | `ManageRooms \| ManageUsers \| ManagePlatform` |
| `Administrator` | `Administrators` | `ManageRooms \| ManageUsers` |
| `Standard` | none | `None` |

Do **not** put Administrators in one group with all three flags: `ManagePlatform` carries backup/restore
(`MaintenanceController`), which an Administrator cannot reach today.

- [ ] **Step 1: Write the failing test**

Append to `tests/Diariz.Api.IntegrationTests/UserGroupsIntegrationTests.cs`:

```csharp
    [Fact]
    public async Task SeedGroups_CreatesBothGroups_AndIsIdempotent()
    {
        await using var db = fx.CreateDbContext();
        await Seeder.SeedGroupsAsync(db);
        await Seeder.SeedGroupsAsync(db); // twice: must not duplicate

        var platform = await db.UserGroups.SingleAsync(g => g.Name == "Platform Administrators");
        var admins = await db.UserGroups.SingleAsync(g => g.Name == "Administrators");

        Assert.True(platform.IsSystem);
        Assert.Equal(
            PlatformPermission.ManageRooms | PlatformPermission.ManageUsers | PlatformPermission.ManagePlatform,
            platform.Permissions);

        Assert.False(admins.IsSystem);
        Assert.Equal(PlatformPermission.ManageRooms | PlatformPermission.ManageUsers, admins.Permissions);
        Assert.False(admins.Permissions.HasFlag(PlatformPermission.ManagePlatform));
    }
```

Add a second test proving an `Administrator` is **not** granted `ManagePlatform`. Use the fixture's
`UserManager` if one is exposed; if not, insert the `AspNetUserRoles` row directly:

```csharp
    [Fact]
    public async Task MigrateRoles_MovesAdministratorToAdministrators_WithoutManagePlatform()
    {
        await using var db = fx.CreateDbContext();
        await Seeder.SeedGroupsAsync(db);

        var adminRoleId = (await db.Roles.SingleAsync(r => r.Name == Roles.Administrator)).Id;
        var userId = await NewUserAsync(db);
        db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = userId, RoleId = adminRoleId });
        await db.SaveChangesAsync();

        await Seeder.MigrateRolesToGroupsAsync(db);

        var perms = await new UserPermissions(db).ForAsync(userId);
        Assert.True(perms.HasFlag(PlatformPermission.ManageUsers));
        Assert.True(perms.HasFlag(PlatformPermission.ManageRooms));
        Assert.False(perms.HasFlag(PlatformPermission.ManagePlatform));
    }
```

> `db.Roles` / `db.UserRoles` exist because `DiarizDbContext` derives from `IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>`. Confirm the exact base class before relying on those sets; if they are not exposed, seed the role rows through `RoleManager` from the fixture's service provider.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~UserGroupsIntegrationTests"`
Expected: FAIL to compile - `Seeder.SeedGroupsAsync` / `Seeder.MigrateRolesToGroupsAsync` do not exist.

- [ ] **Step 3: Write minimal implementation**

Add to `src/Diariz.Api/Services/Seeder.cs`:

```csharp
    /// <summary>The two seeded groups, mirroring the roles they replace. Platform Administrators is a
    /// system group: undeletable, and its last member cannot be removed.</summary>
    public const string PlatformAdminsGroup = "Platform Administrators";
    public const string AdminsGroup = "Administrators";

    /// <summary>Ensure both seeded groups exist with the right flags. Idempotent; runs on every boot.</summary>
    public static async Task SeedGroupsAsync(DiarizDbContext db)
    {
        await EnsureGroup(db, PlatformAdminsGroup, isSystem: true,
            PlatformPermission.ManageRooms | PlatformPermission.ManageUsers | PlatformPermission.ManagePlatform);
        await EnsureGroup(db, AdminsGroup, isSystem: false,
            PlatformPermission.ManageRooms | PlatformPermission.ManageUsers);
        await db.SaveChangesAsync();

        static async Task EnsureGroup(DiarizDbContext db, string name, bool isSystem, PlatformPermission perms)
        {
            var group = await db.UserGroups.FirstOrDefaultAsync(g => g.Name == name);
            if (group is null)
            {
                db.UserGroups.Add(new UserGroup { Id = Guid.NewGuid(), Name = name, IsSystem = isSystem, Permissions = perms });
                return;
            }
            // Backfill flags on an existing deployment, but never demote a group an operator has edited
            // beyond what we seed: we only add the flags we own.
            group.IsSystem = isSystem;
            group.Permissions |= perms;
        }
    }

    /// <summary>One-way move of Identity role holders into the seeded groups. Idempotent: a user already in
    /// the group is skipped. Roles remain in the database, unused, until a later chore removes them.</summary>
    public static async Task MigrateRolesToGroupsAsync(DiarizDbContext db)
    {
        await Move(db, Roles.PlatformAdministrator, PlatformAdminsGroup);
        await Move(db, Roles.Administrator, AdminsGroup);
        await db.SaveChangesAsync();

        static async Task Move(DiarizDbContext db, string roleName, string groupName)
        {
            var group = await db.UserGroups.FirstOrDefaultAsync(g => g.Name == groupName);
            var role = await db.Roles.FirstOrDefaultAsync(r => r.Name == roleName);
            if (group is null || role is null) return;

            var holders = await db.UserRoles.Where(ur => ur.RoleId == role.Id).Select(ur => ur.UserId).ToListAsync();
            var existing = await db.UserGroupMembers.Where(m => m.GroupId == group.Id)
                .Select(m => m.UserId).ToListAsync();

            foreach (var userId in holders.Except(existing))
                db.UserGroupMembers.Add(new UserGroupMember { GroupId = group.Id, UserId = userId });
        }
    }
```

Then, in `Program.cs`, call both on startup where `SeedRolesAsync` / `SeedDefaultUserAsync` are called, **after** the seed user exists (so the platform admin's role row is present to migrate):

```csharp
await Seeder.SeedRolesAsync(scope.ServiceProvider);          // unchanged: roles still exist, unused
await Seeder.SeedDefaultUserAsync(scope.ServiceProvider, builder.Configuration);
await Seeder.SeedGroupsAsync(db);
await Seeder.MigrateRolesToGroupsAsync(db);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~UserGroupsIntegrationTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Services/Seeder.cs src/Diariz.Api/Program.cs tests/Diariz.Api.IntegrationTests/UserGroupsIntegrationTests.cs
git commit -m "feat(api): seed Platform Administrators + Administrators, migrate role holders"
```

---

## Task 6: Swap every role check for a permission policy

**Files:**
- Modify: `src/Diariz.Api/Controllers/AdminUsersController.cs:22,193`
- Modify: `src/Diariz.Api/Controllers/PlatformSettingsController.cs:14,43,73,87`
- Modify: `src/Diariz.Api/Controllers/MaintenanceController.cs:16`
- Modify: `src/Diariz.Api/Controllers/MeetingTypesController.cs:25`
- Modify: `tests/Diariz.Api.IntegrationTests/RbacIntegrationTests.cs`
- Test: existing suites, updated

This is the task that must not change behaviour. Work one controller at a time; run the whole suite between each.

- [ ] **Step 1: Write the failing test**

Add to `tests/Diariz.Api.Tests/PermissionAuthorizationHandlerTests.cs` a test asserting the boundary that a
single seeded group would have broken:

```csharp
    [Fact]
    public async Task Administrator_CannotReachManagePlatform()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var group = new UserGroup
        {
            Id = Guid.NewGuid(),
            Name = "Administrators",
            Permissions = PlatformPermission.ManageRooms | PlatformPermission.ManageUsers,
        };
        db.UserGroups.Add(group);
        db.UserGroupMembers.Add(new UserGroupMember { GroupId = group.Id, UserId = userId });
        await db.SaveChangesAsync();

        Assert.True((await Evaluate(db, User(userId), PlatformPermission.ManageUsers)).HasSucceeded);
        Assert.False((await Evaluate(db, User(userId), PlatformPermission.ManagePlatform)).HasSucceeded);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~Administrator_CannotReachManagePlatform"`
Expected: PASS already (the handler is correct). Its value is as a **regression guard** on the mapping - keep it. The genuine red comes next: `dotnet build Diariz.slnx` fails, because `"Admin"` no longer exists as a policy.

- [ ] **Step 3: Apply the mapping**

`AdminUsersController.cs:22`:
```csharp
[Authorize(Policy = "ManageUsers")]
```

`AdminUsersController.cs:193` - `IsPlatformAdmin(ApplicationUser u)` currently asks the role. Inject
`IUserPermissions` and ask the group:
```csharp
    private Task<bool> IsPlatformAdmin(ApplicationUser u) =>
        _permissions.HasAsync(u.Id, PlatformPermission.ManagePlatform);
```
This keeps `Delete` refusing to remove a platform administrator. Add `IUserPermissions permissions` to the
constructor and store it as `_permissions`.

`PlatformSettingsController.cs:14` (class):
```csharp
[Authorize(Policy = "ReadAdminSettings")]
```
`PlatformSettingsController.cs:43,73,87` (each write):
```csharp
    [Authorize(Policy = "ManagePlatform")]
```

`MaintenanceController.cs:16`:
```csharp
[Authorize(Policy = "ManagePlatform")]
```

`MeetingTypesController.cs:25` - the property is synchronous and cannot await. Replace it and its call
sites with an awaited check:
```csharp
    private Task<bool> IsPlatformAdminAsync() =>
        _permissions.HasAsync(Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!), PlatformPermission.ManagePlatform);
```
Update each `if (IsPlatformAdmin)` to `if (await IsPlatformAdminAsync())`, making the enclosing action
`async` where it is not already. Inject `IUserPermissions`.

- [ ] **Step 4: Fix the second construction site, then run everything**

`tests/Diariz.Api.IntegrationTests/RbacIntegrationTests.cs` constructs these controllers directly and will
not compile after the constructor change. Pass `new UserPermissions(db)`.

Run, in order:
```bash
dotnet build Diariz.slnx
dotnet test tests/Diariz.Api.Tests
dotnet test tests/Diariz.Api.IntegrationTests
```
Expected: build clean, both suites green. Any test that granted authority with a **role claim** via
`Http.Context(userId, roles: ["Administrator"])` now proves nothing - the handler reads the database.
Rewrite it to insert a `UserGroup` + `UserGroupMember` instead. This will be several tests; do them all.

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Controllers tests/
git commit -m "refactor(api): replace role checks with permission policies"
```

---

## Task 7: `GroupsController` - CRUD, the IsSystem guard, the last-member guard

**Files:**
- Create: `src/Diariz.Api/Controllers/GroupsController.cs`
- Test: `tests/Diariz.Api.Tests/GroupsControllerTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Diariz.Api.Tests/GroupsControllerTests.cs`:

```csharp
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;

namespace Diariz.Api.Tests;

public class GroupsControllerTests
{
    private static GroupsController Sut(DiarizDbContext db, Guid callerId)
    {
        var c = new GroupsController(db);
        c.ControllerContext = Http.Context(callerId);
        return c;
    }

    private static async Task<UserGroup> SeedSystemGroup(DiarizDbContext db, params Guid[] members)
    {
        var group = new UserGroup
        {
            Id = Guid.NewGuid(),
            Name = Seeder.PlatformAdminsGroup,
            IsSystem = true,
            Permissions = PlatformPermission.ManagePlatform,
        };
        db.UserGroups.Add(group);
        foreach (var m in members) db.UserGroupMembers.Add(new UserGroupMember { GroupId = group.Id, UserId = m });
        await db.SaveChangesAsync();
        return group;
    }

    [Fact]
    public async Task Create_ThenList_ReturnsTheGroup()
    {
        using var db = TestDb.Create();
        var sut = Sut(db, Guid.NewGuid());

        await sut.Create(new GroupInput("Engineering", "The eng team", "users", "#5C6BC0", PlatformPermission.ManageRooms));

        var groups = await sut.List();
        Assert.Contains(groups, g => g.Name == "Engineering" && g.Permissions == PlatformPermission.ManageRooms);
    }

    [Fact]
    public async Task Delete_SystemGroup_IsForbidden()
    {
        using var db = TestDb.Create();
        var group = await SeedSystemGroup(db, Guid.NewGuid());

        var result = await Sut(db, Guid.NewGuid()).Delete(group.Id);

        Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, ((ObjectResult)result).StatusCode);
        Assert.NotNull(await db.UserGroups.FindAsync(group.Id));
    }

    [Fact]
    public async Task RemoveLastMember_OfSystemGroup_IsForbidden()
    {
        using var db = TestDb.Create();
        var onlyAdmin = Guid.NewGuid();
        var group = await SeedSystemGroup(db, onlyAdmin);

        var result = await Sut(db, onlyAdmin).RemoveMember(group.Id, onlyAdmin);

        Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, ((ObjectResult)result).StatusCode);
        Assert.Single(db.UserGroupMembers.Where(m => m.GroupId == group.Id));
    }

    [Fact]
    public async Task RemoveMember_OfSystemGroup_IsAllowed_WhenOthersRemain()
    {
        using var db = TestDb.Create();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var group = await SeedSystemGroup(db, a, b);

        var result = await Sut(db, a).RemoveMember(group.Id, b);

        Assert.IsType<NoContentResult>(result);
        Assert.Single(db.UserGroupMembers.Where(m => m.GroupId == group.Id));
    }

    [Fact]
    public async Task Delete_OrdinaryGroup_RemovesIt()
    {
        using var db = TestDb.Create();
        var group = new UserGroup { Id = Guid.NewGuid(), Name = "Engineering" };
        db.UserGroups.Add(group);
        await db.SaveChangesAsync();

        var result = await Sut(db, Guid.NewGuid()).Delete(group.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.Null(await db.UserGroups.FindAsync(group.Id));
    }

    [Fact]
    public async Task Create_DuplicateName_IsRejected()
    {
        using var db = TestDb.Create();
        var sut = Sut(db, Guid.NewGuid());
        await sut.Create(new GroupInput("Engineering", null, null, null, PlatformPermission.None));

        var result = await sut.Create(new GroupInput("Engineering", null, null, null, PlatformPermission.None));

        Assert.IsType<ConflictObjectResult>(result);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~GroupsControllerTests"`
Expected: FAIL to compile - `GroupsController` / `GroupInput` do not exist.

- [ ] **Step 3: Write minimal implementation**

`src/Diariz.Api/Controllers/GroupsController.cs`:

```csharp
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Controllers;

public record GroupInput(string Name, string? Description, string? Icon, string? Color, PlatformPermission Permissions);

public record GroupDto(Guid Id, string Name, string? Description, string? Icon, string? Color,
    PlatformPermission Permissions, bool IsSystem, Guid[] MemberIds);

/// <summary>Group administration. A system group (Platform Administrators) cannot be deleted, and its last
/// member cannot be removed - otherwise a deployment could be left with nobody able to administer it.</summary>
[ApiController]
[Route("api/groups")]
[Authorize(Policy = "ManageUsers")]
public class GroupsController(DiarizDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<List<GroupDto>> List() =>
        await db.UserGroups
            .OrderByDescending(g => g.IsSystem).ThenBy(g => g.Name)
            .Select(g => new GroupDto(g.Id, g.Name, g.Description, g.Icon, g.Color, g.Permissions, g.IsSystem,
                g.Members.Select(m => m.UserId).ToArray()))
            .ToListAsync();

    [HttpPost]
    public async Task<IActionResult> Create(GroupInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) return BadRequest("A group needs a name.");
        if (await db.UserGroups.AnyAsync(g => g.Name == input.Name))
            return Conflict($"A group named '{input.Name}' already exists.");

        var group = new UserGroup
        {
            Id = Guid.NewGuid(),
            Name = input.Name.Trim(),
            Description = input.Description,
            Icon = input.Icon,
            Color = input.Color,
            Permissions = input.Permissions,
        };
        db.UserGroups.Add(group);
        await db.SaveChangesAsync();
        return Ok(new GroupDto(group.Id, group.Name, group.Description, group.Icon, group.Color,
            group.Permissions, group.IsSystem, []));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, GroupInput input)
    {
        var group = await db.UserGroups.FirstOrDefaultAsync(g => g.Id == id);
        if (group is null) return NotFound();
        if (await db.UserGroups.AnyAsync(g => g.Name == input.Name && g.Id != id))
            return Conflict($"A group named '{input.Name}' already exists.");

        // A system group's name and flags are fixed: the seeder owns them, and demoting them could
        // leave the deployment unadministrable.
        if (!group.IsSystem)
        {
            group.Name = input.Name.Trim();
            group.Permissions = input.Permissions;
        }
        group.Description = input.Description;
        group.Icon = input.Icon;
        group.Color = input.Color;
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var group = await db.UserGroups.FirstOrDefaultAsync(g => g.Id == id);
        if (group is null) return NotFound();
        if (group.IsSystem) return Forbidden("The Platform Administrators group can't be deleted.");

        db.UserGroups.Remove(group); // members cascade
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPut("{id:guid}/members/{userId:guid}")]
    public async Task<IActionResult> AddMember(Guid id, Guid userId)
    {
        if (!await db.UserGroups.AnyAsync(g => g.Id == id)) return NotFound();
        if (await db.UserGroupMembers.AnyAsync(m => m.GroupId == id && m.UserId == userId)) return NoContent();

        db.UserGroupMembers.Add(new UserGroupMember { GroupId = id, UserId = userId });
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:guid}/members/{userId:guid}")]
    public async Task<IActionResult> RemoveMember(Guid id, Guid userId)
    {
        var group = await db.UserGroups.FirstOrDefaultAsync(g => g.Id == id);
        if (group is null) return NotFound();

        var member = await db.UserGroupMembers.FirstOrDefaultAsync(m => m.GroupId == id && m.UserId == userId);
        if (member is null) return NoContent();

        if (group.IsSystem && await db.UserGroupMembers.CountAsync(m => m.GroupId == id) <= 1)
            return Forbidden("The Platform Administrators group must always have at least one member.");

        db.UserGroupMembers.Remove(member);
        await db.SaveChangesAsync();
        return NoContent();
    }

    private IActionResult Forbidden(string message) => StatusCode(StatusCodes.Status403Forbidden, message);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~GroupsControllerTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Controllers/GroupsController.cs tests/Diariz.Api.Tests/GroupsControllerTests.cs
git commit -m "feat(api): groups CRUD with system-group and last-member guards"
```

---

## Task 8: Stop signing roles into the JWT; expose permissions on the profile

**Files:**
- Modify: `src/Diariz.Api/Services/TokenService.cs:37`
- Modify: `src/Diariz.Api/Controllers/AuthController.cs:158,373,449`
- Modify: `src/Diariz.Api/Controllers/UserProfileController.cs`
- Test: `tests/Diariz.Api.Tests/UserProfileControllerTests.cs`

- [ ] **Step 1: Write the failing test**

Create (or extend) `tests/Diariz.Api.Tests/UserProfileControllerTests.cs`:

```csharp
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

public class UserProfilePermissionsTests
{
    [Fact]
    public async Task Profile_ReportsTheCallersPermissions()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "a@b.test", Email = "a@b.test" });
        var group = new UserGroup { Id = Guid.NewGuid(), Name = "Administrators", Permissions = PlatformPermission.ManageUsers };
        db.UserGroups.Add(group);
        db.UserGroupMembers.Add(new UserGroupMember { GroupId = group.Id, UserId = userId });
        await db.SaveChangesAsync();

        // Construct UserProfileController exactly as its existing tests do, adding IUserPermissions.
        var sut = new UserProfileController(db, new UserPermissions(db));
        sut.ControllerContext = Http.Context(userId);

        var profile = await sut.Get();

        Assert.True(profile.Permissions.ManageUsers);
        Assert.False(profile.Permissions.ManagePlatform);
        Assert.False(profile.Permissions.ManageRooms);
    }
}
```

> Read `UserProfileController` first. Match its real constructor and its real `Get()` return type; add a
> `Permissions` property to whatever DTO it already returns rather than inventing a new one. The DTO shape
> above (`profile.Permissions.ManageUsers`) is the target - three booleans, not a raw int, so the web does
> not do bit arithmetic.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~UserProfilePermissionsTests"`
Expected: FAIL - `Permissions` is not on the profile DTO.

- [ ] **Step 3: Write minimal implementation**

Add to the profile DTO in `UserProfileController.cs`:

```csharp
public record PermissionsDto(bool ManageRooms, bool ManageUsers, bool ManagePlatform);
```

populate it from `IUserPermissions.ForAsync(userId)`, and inject `IUserPermissions`.

Then remove the role claim from `src/Diariz.Api/Services/TokenService.cs` - delete line 37
(`claims.Add(new Claim(ClaimTypes.Role, role));`) and the `roles` parameter of `CreateAccessToken`. Update
the three call sites in `AuthController.cs` (lines 158, 373, 449) to stop passing
`await _users.GetRolesAsync(user)`.

> Removing the parameter is the point: leaving it in place, unused, invites someone to re-add the claim and
> reintroduce the staleness bug this design exists to avoid.

- [ ] **Step 4: Run test to verify it passes**

Run:
```bash
dotnet build Diariz.slnx
dotnet test tests/Diariz.Api.Tests
```
Expected: PASS. `TokenService` tests asserting a role claim must be deleted, not weakened.

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api tests/Diariz.Api.Tests
git commit -m "feat(api): permissions on the user profile; stop signing roles into the JWT"
```

---

## Task 9: Web - permissions from the profile, not the token

**Files:**
- Modify: `apps/web/src/lib/jwt.ts` (delete three functions)
- Modify: `apps/web/src/lib/jwt.test.ts` (delete their tests)
- Modify: `apps/web/src/lib/api.ts`
- Modify: the `useAuth` hook and `apps/web/src/components/UserMenu.tsx:108`
- Test: `apps/web/src/components/UserMenu.test.tsx`

- [ ] **Step 1: Write the failing test**

In `apps/web/src/components/UserMenu.test.tsx`, replace whatever stubs `isAdminFromToken` with a stubbed
profile query, and assert:

```tsx
it("shows Manage Users only when the profile grants manageUsers", async () => {
  mockProfile({ permissions: { manageRooms: false, manageUsers: true, managePlatform: false } });
  render(<UserMenu />);
  fireEvent.click(await screen.findByRole("button", { name: /account/i }));
  expect(screen.getByRole("menuitem", { name: /manage users/i })).toBeTruthy();
});

it("hides Manage Users without the permission", async () => {
  mockProfile({ permissions: { manageRooms: false, manageUsers: false, managePlatform: false } });
  render(<UserMenu />);
  fireEvent.click(await screen.findByRole("button", { name: /account/i }));
  expect(screen.queryByRole("menuitem", { name: /manage users/i })).toBeNull();
});
```

> Read `UserMenu.test.tsx` first and match its existing mocking style (it already mocks `../lib/api`). The
> accessible names above must match the real menu items; correct them to whatever the component renders.

- [ ] **Step 2: Run test to verify it fails**

Run: `cd apps/web && npx vitest run src/components/UserMenu.test.tsx`
Expected: FAIL - the component still gates on `isAdminFromToken(token)`.

- [ ] **Step 3: Write minimal implementation**

- In `apps/web/src/lib/jwt.ts`, delete `rolesFromToken`, `isAdminFromToken`, `isPlatformAdminFromToken`
  and the `ROLE_KEYS` constant. Delete the corresponding cases in `jwt.test.ts` (lines 46-48 and their
  `describe` block if it empties).
- Add to `apps/web/src/lib/api.ts`:

```ts
export interface Permissions { manageRooms: boolean; manageUsers: boolean; managePlatform: boolean }

export interface Group {
  id: string; name: string; description?: string | null; icon?: string | null; color?: string | null;
  permissions: number; isSystem: boolean; memberIds: string[];
}
```

and the calls (mirroring the existing axios style in that file):

```ts
  async listGroups(): Promise<Group[]> { return (await http.get("/api/groups")).data },
  async createGroup(g: Omit<Group, "id" | "isSystem" | "memberIds">): Promise<Group> { return (await http.post("/api/groups", g)).data },
  async updateGroup(id: string, g: Omit<Group, "id" | "isSystem" | "memberIds">): Promise<void> { await http.put(`/api/groups/${id}`, g) },
  async deleteGroup(id: string): Promise<void> { await http.delete(`/api/groups/${id}`) },
  async addGroupMember(id: string, userId: string): Promise<void> { await http.put(`/api/groups/${id}/members/${userId}`) },
  async removeGroupMember(id: string, userId: string): Promise<void> { await http.delete(`/api/groups/${id}/members/${userId}`) },
```

and **delete** `setUserRole` (api.ts:199).

- Add `permissions` to the profile type, and change `useAuth` to expose `permissions` from the
  `["user-profile"]` query rather than from the token. Gate `UserMenu` on `permissions.manageUsers`
  (Manage Users) and `permissions.managePlatform` (platform settings, maintenance).

- [ ] **Step 4: Run test to verify it passes**

Run:
```bash
cd apps/web && npx vitest run && npx tsc --noEmit
```
Expected: all green. Any test still importing `isAdminFromToken` must be updated, not skipped.

- [ ] **Step 5: Commit**

```bash
git add apps/web/src
git commit -m "feat(web): read permissions from the profile instead of the JWT"
```

---

## Task 10: Web - the Groups tab

**Files:**
- Create: `apps/web/src/components/GroupsTab.tsx`
- Create: `apps/web/src/components/GroupsTab.test.tsx`
- Modify: `apps/web/src/components/ManageUsersModal.tsx`
- Modify: `apps/web/src/locales/{en,es,fr,de}/*.json`

The modal becomes two tabs: **Users** (today's table, with the account-type column replaced by the groups
each user belongs to) and **Groups**.

- [ ] **Step 1: Write the failing test**

Create `apps/web/src/components/GroupsTab.test.tsx`:

```tsx
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach, type Mock } from "vitest";

vi.mock("../lib/api", () => ({
  api: {
    listGroups: vi.fn(),
    createGroup: vi.fn(),
    deleteGroup: vi.fn(),
    listUsers: vi.fn().mockResolvedValue([]),
  },
}));

import { api } from "../lib/api";
import GroupsTab from "./GroupsTab";

const systemGroup = {
  id: "g1", name: "Platform Administrators", permissions: 7, isSystem: true, memberIds: ["u1"],
};
const ordinary = { id: "g2", name: "Engineering", permissions: 1, isSystem: false, memberIds: [] };

describe("GroupsTab", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    (api.listGroups as Mock).mockResolvedValue([systemGroup, ordinary]);
  });

  it("lists groups", async () => {
    render(<GroupsTab />);
    expect(await screen.findByText("Platform Administrators")).toBeTruthy();
    expect(screen.getByText("Engineering")).toBeTruthy();
  });

  it("offers no Delete for the system group", async () => {
    render(<GroupsTab />);
    await screen.findByText("Platform Administrators");
    const row = screen.getByTestId("group-row-g1");
    expect(row.querySelector('[aria-label="Delete group"]')).toBeNull();
  });

  it("deletes an ordinary group", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    render(<GroupsTab />);
    await screen.findByText("Engineering");
    fireEvent.click(screen.getByTestId("group-row-g2").querySelector('[aria-label="Delete group"]')!);
    await waitFor(() => expect(api.deleteGroup).toHaveBeenCalledWith("g2"));
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd apps/web && npx vitest run src/components/GroupsTab.test.tsx`
Expected: FAIL - `./GroupsTab` cannot be resolved.

- [ ] **Step 3: Write minimal implementation**

Create `apps/web/src/components/GroupsTab.tsx`:

```tsx
import { useState } from "react";
import { useQuery, useQueryClient, useMutation } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, type Group } from "../lib/api";

/// The three PlatformPermission bits, mirrored from the server enum. Append-only: never renumber.
const PERM = { manageRooms: 1, manageUsers: 2, managePlatform: 4 } as const;

/// Groups administration. A system group (Platform Administrators) has fixed name and permissions - the
/// seeder owns them - and cannot be deleted, so those controls are absent rather than merely disabled.
export default function GroupsTab() {
  const { t } = useTranslation("account");
  const qc = useQueryClient();
  const { data: groups = [] } = useQuery({ queryKey: ["groups"], queryFn: api.listGroups });
  const [name, setName] = useState("");

  const invalidate = () => qc.invalidateQueries({ queryKey: ["groups"] });
  const create = useMutation({
    mutationFn: (n: string) => api.createGroup({ name: n, permissions: 0 }),
    onSuccess: () => { setName(""); void invalidate(); },
  });
  const remove = useMutation({ mutationFn: api.deleteGroup, onSuccess: invalidate });
  const update = useMutation({
    mutationFn: ({ id, g }: { id: string; g: Group }) =>
      api.updateGroup(id, { name: g.name, description: g.description, icon: g.icon, color: g.color, permissions: g.permissions }),
    onSuccess: invalidate,
  });

  function togglePermission(g: Group, bit: number) {
    update.mutate({ id: g.id, g: { ...g, permissions: g.permissions ^ bit } });
  }

  function onDelete(g: Group) {
    if (window.confirm(t("confirmDeleteGroup", { name: g.name }))) remove.mutate(g.id);
  }

  return (
    <div className="space-y-3">
      <table className="w-full text-sm">
        <thead>
          <tr className="text-left text-xs uppercase text-gray-500 dark:text-gray-400">
            <th className="py-1">{t("groupName")}</th>
            <th>{t("permManageRooms")}</th>
            <th>{t("permManageUsers")}</th>
            <th>{t("permManagePlatform")}</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {groups.map((g) => (
            <tr key={g.id} data-testid={`group-row-${g.id}`} className="border-t dark:border-gray-700">
              <td className="py-1.5">{g.name}</td>
              {([PERM.manageRooms, PERM.manageUsers, PERM.managePlatform] as const).map((bit) => (
                <td key={bit}>
                  <input
                    type="checkbox"
                    checked={(g.permissions & bit) !== 0}
                    disabled={g.isSystem}
                    onChange={() => togglePermission(g, bit)}
                    aria-label={`${g.name} ${bit}`}
                  />
                </td>
              ))}
              <td className="text-right">
                {!g.isSystem && (
                  <button
                    type="button"
                    aria-label="Delete group"
                    title={t("deleteGroup")}
                    onClick={() => onDelete(g)}
                    className="rounded px-2 py-1 text-xs text-red-600 hover:bg-red-50 dark:hover:bg-red-900/30"
                  >
                    {t("deleteGroup")}
                  </button>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      <form
        className="flex gap-2"
        onSubmit={(e) => { e.preventDefault(); if (name.trim()) create.mutate(name.trim()); }}
      >
        <input
          value={name}
          onChange={(e) => setName(e.target.value)}
          placeholder={t("newGroupName")}
          aria-label={t("newGroupName")}
          className="flex-1 rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800"
        />
        <button type="submit" disabled={!name.trim()} className="rounded bg-gray-900 px-3 py-1 text-sm text-white disabled:opacity-50 dark:bg-gray-100 dark:text-gray-900">
          {t("addGroup")}
        </button>
      </form>
    </div>
  );
}
```

> `aria-label="Delete group"` is asserted by the test verbatim; the visible label goes through i18n. If you
> prefer the accessible name to be localized too, change the test in Step 1 first, not afterwards.

Then wire it into `ManageUsersModal.tsx` as a second tab, and replace the per-user account-type cell with
the names of the groups that user belongs to - derive them from `listGroups()`'s `memberIds`, so the modal
needs no new endpoint:

```tsx
const groupsOf = (userId: string) => groups.filter((g) => g.memberIds.includes(userId)).map((g) => g.name);
```

Add every new i18n key (`groupName`, `permManageRooms`, `permManageUsers`, `permManagePlatform`,
`deleteGroup`, `confirmDeleteGroup`, `newGroupName`, `addGroup`, plus the two tab labels) to all four
locale catalogues: `apps/web/src/locales/{en,es,fr,de}/account.json`. Plain hyphens, no em dashes.

- [ ] **Step 4: Run test to verify it passes**

Run:
```bash
cd apps/web && npx vitest run && npx tsc --noEmit
```
Expected: all green.

- [ ] **Step 5: Commit**

```bash
git add apps/web/src
git commit -m "feat(web): Groups tab in the Manage Users modal"
```

---

## Task 11: Docs and release

**Files:**
- Modify: `docs/Data_Schema.md`, `docs/Overall_Synopsis_of_Platform.md`
- Modify: `README.md`, `docs/features.md`, `apps/web/src/lib/releases.ts`
- Modify: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`

Per `CLAUDE.md`: every PR ships exactly one release, and a **functional enhancement bumps Minor +1 and
resets Build to 0**. This is an enhancement.

- [ ] **Step 1: Update the two reference docs**

`Data_Schema.md`: add `UserGroups` and `UserGroupMembers` (every column, key, index, cascade), the
`PlatformPermission` flags enum, and a row in the migration-history table for `AddUserGroups`.

`Overall_Synopsis_of_Platform.md`: replace the roles description with the group/permission model. State
that permissions are resolved from the database per request, that `Platform Administrators` is seeded and
protected, and that the Identity role tables remain but are unused.

- [ ] **Step 2: Update the user-facing feature copy**

Update in lockstep, per `CLAUDE.md`:
- the **Multi-user & roles** row of the README Features table,
- the matching bullet in `docs/features.md`,
- the **Multi-user & roles** row of `CAPABILITIES` in `apps/web/src/lib/releases.ts`.

Rename the concept from roles to groups. Keep the `CAPABILITIES` row to one concise line.

- [ ] **Step 3: Bump the version and add the release entry**

Set the same new Minor version (Build reset to 0) in `version.json`, `apps/web/package.json`,
`apps/desktop/package.json`, and `<Version>` in `src/Diariz.Api/Diariz.Api.csproj`. Add a new
`RELEASES[0]` entry in `apps/web/src/lib/releases.ts` with `version`, `date`, `pr`, `headline`, a
PR-level prose `summary`, and `added` / `changed` bullets. `RELEASES[0].version` must equal
`version.json` - `releases.test.ts` asserts it.

The summary must say plainly that account types became groups, that existing Administrators and Platform
Administrators keep exactly the permissions they had, and that group membership now takes effect
immediately rather than at next sign-in.

- [ ] **Step 4: Verify everything**

```bash
dotnet build Diariz.slnx
dotnet test tests/Diariz.Api.Tests
dotnet test tests/Diariz.Api.IntegrationTests
cd apps/web && npm test && npx tsc --noEmit
```
Expected: all green, no warnings.

- [ ] **Step 5: Commit**

```bash
git add docs README.md apps/web version.json src/Diariz.Api/Diariz.Api.csproj
git commit -m "docs: user groups replace roles; bump version and release notes"
```

---

## Deployment surface

**Server redeploy only.** No `apps/desktop/**` source changes, so no installer release. The desktop
`package.json` bump is the lockstep version mirror.

## Manual verification before opening the PR

1. Boot against a database that predates this change (a copy of dev, not a fresh one). Confirm the seeder
   creates both groups and moves the existing Administrator and Platform Administrator into the right ones.
2. Sign in as the seed user. Confirm Manage Users, platform settings, and maintenance are all reachable.
3. Sign in as a plain Administrator. Confirm Manage Users **is** reachable, the default-quota field
   **renders** (that is the `ReadAdminSettings` policy), and backup/restore is **not** offered.
4. Remove yourself from Platform Administrators via the Groups tab. Confirm the API refuses with a 403
   naming the last-member rule.
5. Add a Standard user to Administrators. Without signing them out, confirm Manage Users becomes reachable
   on their next page load - proving permissions are not baked into their token.
