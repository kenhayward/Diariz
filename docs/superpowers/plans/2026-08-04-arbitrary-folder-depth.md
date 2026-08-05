# Arbitrary Folder Depth Implementation Plan (Phases 1-3)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let folders nest to 8 levels instead of 2, make every folder roll-up span the whole subtree, and give the left nav a breadcrumb that works at any depth.

**Architecture:** The `Section` table is already a self-referencing tree, so there is **no migration**. One pure C# helper (`SectionTree`) replaces five hand-rolled "section plus its direct children" queries and supplies the depth/cycle rules that replace the two-level cap. On the web, `buildRecordingTree` becomes recursive and a new pure `collapsePath` drives a shared `FolderPath` breadcrumb component.

**Tech Stack:** ASP.NET Core (.NET 10) + EF Core, xUnit (unit tests use the EF in-memory provider; integration tests use Testcontainers/Postgres), React 19 + TypeScript + Tailwind v4, Vitest + @testing-library/react, i18next.

## Global Constraints

- **TDD is mandatory.** Write the failing test, run it, watch it fail, then write the minimal code. No production code without a preceding failing test.
- **Test output must be pristine** - a passing run has no errors or warnings.
- **No em dashes or en dashes in user-facing text.** Use a plain hyphen `-` in UI strings, i18n catalogues, release notes, and docs. (`—` and `–` are banned; code comments are unaffected but this plan uses hyphens throughout anyway.)
- **`main` is branch-protected.** Every phase lands as its own Pull Request. Never commit or push to `main`, never merge locally.
- **Each PR ships exactly one release:** bump `/version.json` **and all four mirrors** (`apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj` `<Version>`, `integrations/n8n-nodes-diariz/package.json`), and add one `RELEASES[0]` entry to `apps/web/src/lib/releases.ts` whose `version` equals `version.json`. `versionMirrors.test.ts` and `releases.test.ts` fail the build otherwise.
- **Version at the start of this work is `0.178.0`.** Phase 1 -> `0.178.1` (refactor, Build +1). Phase 2 -> `0.179.0` (functional enhancement, Minor +1 / Build 0). Phase 3 -> `0.180.0`.
- **Maximum folder depth is 8**, with top-level counting as depth 1.
- **No migration is created in this work.** If you find yourself writing one, stop - the design depends on there being none, so that `MaintenanceController.CurrentFormat` need not be bumped and old backups stay restorable.
- **Deployment surface: server redeploy only.** Nothing here touches the desktop shell. State this in each PR description.

---

## File Structure

**Created:**

| File | Responsibility |
| --- | --- |
| `src/Diariz.Api/Services/SectionTree.cs` | The only place that knows how folders nest: subtree, depth, height, and the DB loaders. Pure functions plus two thin async wrappers. |
| `tests/Diariz.Api.Tests/SectionTreeTests.cs` | Unit tests for the pure functions, including cycles. |
| `tests/Diariz.Api.IntegrationTests/SectionSubtreeIntegrationTests.cs` | Proves a grandchild's recordings reach the folder page against real Postgres. |
| `apps/web/src/lib/folderPath.ts` | Pure `collapsePath` - which crumbs survive at a given width budget. |
| `apps/web/src/lib/folderPath.test.ts` | Its tests. |
| `apps/web/src/components/nav/FolderPath.tsx` | Presentational breadcrumb: collapsed path plus a menu of the full ancestor chain. |
| `apps/web/src/components/nav/FolderPath.test.tsx` | Its component tests. |

**Modified:**

| File | Change |
| --- | --- |
| `src/Diariz.Api/Controllers/ChatController.cs:468` | Direct-children query -> `SectionTree.SubtreeIdsAsync` |
| `src/Diariz.Api/Controllers/SectionPageController.cs:45-51` | Same |
| `src/Diariz.Api/Controllers/SectionFormulaResultsController.cs:48-54` | Same |
| `src/Diariz.Api/Services/FormulaRunProcessor.cs:279-283` | Same |
| `src/Diariz.Api/Services/SectionSummaryProcessor.cs:75-82` | Same, and `UserId` scoping -> `RoomId` |
| `src/Diariz.Api/Controllers/SearchController.cs:111-128` | `SubtreeAsync` delegates to `SectionTree` |
| `src/Diariz.Api/Controllers/SectionsController.cs:84-90, 125-142` | Cap checks -> depth / height / descendant rules; three `EndpointDescription` blocks |
| `apps/web/src/lib/recordingTree.ts:28-72` | `buildRecordingTree` becomes recursive |
| `apps/web/src/lib/drillView.ts:62-77` | `sectionCreateTarget` gains depth awareness |
| `apps/web/src/components/RecordingsPanel.tsx:327` | `childrenCanNest` becomes a depth test |
| `apps/web/src/components/nav/SectionRow.tsx` | `canNest` semantics unchanged, now depth-driven |
| `apps/web/src/components/nav/DrillBreadcrumb.tsx` | Renders `FolderPath`; crumbs drill and accept drops |
| `apps/web/src/components/nav/SearchBar.tsx:186-191` | Folder hits show their ancestor path |
| `apps/web/src/pages/SectionDetail.tsx` | Gains a breadcrumb |
| `apps/web/src/locales/en/workspace.json` | New and changed strings |

---

# PHASE 1 - The subtree roll-up (PR 1, version 0.178.1)

**This phase is behaviour-preserving.** At depth 2, "whole subtree" and "direct children" are the same set, so no user-visible change should result. That is the point: the risky semantic change lands while it is still provably a no-op.

Start the branch:

```bash
git checkout main
git pull
git checkout -b feat/folder-subtree-rollup
```

---

### Task 1: The pure `SectionTree.Subtree` helper

**Files:**
- Create: `src/Diariz.Api/Services/SectionTree.cs`
- Test: `tests/Diariz.Api.Tests/SectionTreeTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Diariz.Api.Services.SectionLink` (a `readonly record struct` with `Guid Id` and `Guid? ParentId`) and `SectionTree.Subtree(IReadOnlyCollection<SectionLink> sections, Guid rootId) -> List<Guid>`. The root id is **always** the first element of the result, even when it is not present in `sections`.

- [ ] **Step 1: Write the failing test**

Create `tests/Diariz.Api.Tests/SectionTreeTests.cs`:

```csharp
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class SectionTreeTests
{
    // A fixed tree used across the cases:
    //   customers -> acme -> falcon
    //   podcasts (unrelated top-level)
    private static readonly Guid Customers = Guid.NewGuid();
    private static readonly Guid Acme = Guid.NewGuid();
    private static readonly Guid Falcon = Guid.NewGuid();
    private static readonly Guid Podcasts = Guid.NewGuid();

    private static SectionLink[] Tree() =>
    [
        new(Customers, null),
        new(Acme, Customers),
        new(Falcon, Acme),
        new(Podcasts, null),
    ];

    [Fact]
    public void Subtree_OfALeaf_IsJustItself()
    {
        Assert.Equal([Falcon], SectionTree.Subtree(Tree(), Falcon));
    }

    [Fact]
    public void Subtree_IncludesGrandchildren_NotJustDirectChildren()
    {
        var ids = SectionTree.Subtree(Tree(), Customers);

        Assert.Equal(Customers, ids[0]);           // the root leads
        Assert.Contains(Acme, ids);                // direct child
        Assert.Contains(Falcon, ids);              // grandchild - the whole point
        Assert.DoesNotContain(Podcasts, ids);      // unrelated branch excluded
        Assert.Equal(3, ids.Count);
    }

    [Fact]
    public void Subtree_OfAnUnknownRoot_IsStillJustThatRoot()
    {
        // Callers add the root themselves today; keeping that contract means a deleted
        // folder yields "nothing but itself" rather than an empty set that reads as "everything".
        var unknown = Guid.NewGuid();
        Assert.Equal([unknown], SectionTree.Subtree(Tree(), unknown));
    }

    [Fact]
    public void Subtree_WithACycle_Terminates()
    {
        // Nothing in the schema prevents a ParentId cycle. Without a visited set this spins forever.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        SectionLink[] cyclic = [new(a, b), new(b, a)];

        var ids = SectionTree.Subtree(cyclic, a);

        Assert.Equal(2, ids.Count);
        Assert.Contains(a, ids);
        Assert.Contains(b, ids);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~SectionTreeTests"`
Expected: FAIL to compile - `SectionTree` and `SectionLink` do not exist.

- [ ] **Step 3: Write the minimal implementation**

Create `src/Diariz.Api/Services/SectionTree.cs`:

```csharp
namespace Diariz.Api.Services;

/// <summary>The parent link of one folder, flattened for the pure tree functions below. Deliberately not the
/// <c>Section</c> entity: these functions are provider-agnostic and must stay cheap to unit-test.</summary>
public readonly record struct SectionLink(Guid Id, Guid? ParentId);

/// <summary>The single place that knows how folders nest. Every "the recordings in this folder" query used to
/// hand-roll <c>ParentId == sectionId</c>, which only ever saw DIRECT children - correct while the hierarchy was
/// capped at two levels, silently wrong the moment it was not. The walk is level-by-level rather than a recursive
/// CTE so it translates on every EF provider, including the in-memory one the unit tests use.</summary>
public static class SectionTree
{
    /// <summary>A folder and every folder beneath it, root first. The root is always included, even if it is not
    /// in <paramref name="sections"/> (a folder deleted mid-request reads as "just itself", never as "everything").
    /// A <c>ParentId</c> cycle is not schema-enforced, so the visited set is what stops this spinning.</summary>
    public static List<Guid> Subtree(IReadOnlyCollection<SectionLink> sections, Guid rootId)
    {
        var ids = new List<Guid> { rootId };
        var seen = new HashSet<Guid> { rootId };
        var frontier = new List<Guid> { rootId };

        while (frontier.Count > 0)
        {
            var next = new List<Guid>();
            foreach (var s in sections)
            {
                if (s.ParentId is not Guid p || !frontier.Contains(p)) continue;
                if (!seen.Add(s.Id)) continue;
                ids.Add(s.Id);
                next.Add(s.Id);
            }
            frontier = next;
        }

        return ids;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~SectionTreeTests"`
Expected: PASS, 4 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Services/SectionTree.cs tests/Diariz.Api.Tests/SectionTreeTests.cs
git commit -m "feat: add SectionTree.Subtree, the one place folder nesting is known"
```

---

### Task 2: The DB loaders, and the four room-scoped call sites

**Files:**
- Modify: `src/Diariz.Api/Services/SectionTree.cs`
- Modify: `src/Diariz.Api/Controllers/ChatController.cs:468-470`
- Modify: `src/Diariz.Api/Controllers/SectionPageController.cs:43-51`
- Modify: `src/Diariz.Api/Controllers/SectionFormulaResultsController.cs:46-54`
- Modify: `src/Diariz.Api/Services/FormulaRunProcessor.cs:263-283`
- Modify: `src/Diariz.Api/Controllers/SearchController.cs:111-128`

**Interfaces:**
- Consumes: `SectionTree.Subtree` from Task 1.
- Produces: `SectionTree.LinksAsync(DiarizDbContext db, Guid roomId, CancellationToken ct) -> Task<List<SectionLink>>` and `SectionTree.SubtreeIdsAsync(DiarizDbContext db, Guid roomId, Guid rootId, CancellationToken ct) -> Task<List<Guid>>`. Task 6 adds more members to the same class.

- [ ] **Step 1: Write the failing test**

Append to `tests/Diariz.Api.Tests/SectionTreeTests.cs`, inside the class:

```csharp
    [Fact]
    public async Task SubtreeIdsAsync_ReadsOneRoomsFolders_AndReachesGrandchildren()
    {
        using var db = TestDb.Create();
        var roomId = Guid.NewGuid();
        var otherRoomId = Guid.NewGuid();
        var customers = new Section { Id = Guid.NewGuid(), RoomId = roomId, Name = "Customers" };
        var acme = new Section { Id = Guid.NewGuid(), RoomId = roomId, Name = "Acme", ParentId = customers.Id };
        var falcon = new Section { Id = Guid.NewGuid(), RoomId = roomId, Name = "Falcon", ParentId = acme.Id };
        // Same shape in a different room - must not leak in.
        var decoy = new Section { Id = Guid.NewGuid(), RoomId = otherRoomId, Name = "Decoy", ParentId = customers.Id };
        db.Sections.AddRange(customers, acme, falcon, decoy);
        await db.SaveChangesAsync();

        var ids = await SectionTree.SubtreeIdsAsync(db, roomId, customers.Id, default);

        Assert.Equal(3, ids.Count);
        Assert.Contains(falcon.Id, ids);
        Assert.DoesNotContain(decoy.Id, ids);
    }
```

Add these usings to the top of the file:

```csharp
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter "Name=SubtreeIdsAsync_ReadsOneRoomsFolders_AndReachesGrandchildren"`
Expected: FAIL to compile - `SubtreeIdsAsync` does not exist.

- [ ] **Step 3: Add the loaders**

Add these usings to the top of `src/Diariz.Api/Services/SectionTree.cs`:

```csharp
using Diariz.Domain;
using Microsoft.EntityFrameworkCore;
```

Add to the `SectionTree` class, after `Subtree`:

```csharp
    /// <summary>Every folder link in one room. A room's folder list is small (tens of rows) and already the unit
    /// the nav loads, so pulling it whole and walking it in memory is cheaper than a round trip per level.</summary>
    public static Task<List<SectionLink>> LinksAsync(DiarizDbContext db, Guid roomId, CancellationToken ct) =>
        db.Sections.Where(s => s.RoomId == roomId)
            .Select(s => new SectionLink(s.Id, s.ParentId))
            .ToListAsync(ct);

    /// <summary>The folder plus every folder beneath it, within one room - the set a recording's placement
    /// <c>SectionId</c> must be in to count as "included" in that folder.</summary>
    public static async Task<List<Guid>> SubtreeIdsAsync(
        DiarizDbContext db, Guid roomId, Guid rootId, CancellationToken ct) =>
        Subtree(await LinksAsync(db, roomId, ct), rootId);
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~SectionTreeTests"`
Expected: PASS, 5 tests.

- [ ] **Step 5: Swap `SectionPageController`**

Replace the whole `IncludedSectionIdsAsync` method (`src/Diariz.Api/Controllers/SectionPageController.cs:43-51`) with:

```csharp
    /// <summary>The section id plus **every** section id beneath it (within the same room) - the set a recording's
    /// placement <c>SectionId</c> must be in to count as "included" in this folder.</summary>
    private Task<List<Guid>> IncludedSectionIdsAsync(Guid sectionId, Guid roomId) =>
        SectionTree.SubtreeIdsAsync(_db, roomId, sectionId, default);
```

Confirm `using Diariz.Api.Services;` is present at the top of the file; add it if not.

- [ ] **Step 6: Swap `SectionFormulaResultsController`**

Replace the whole `IncludedSectionIdsAsync` method (`src/Diariz.Api/Controllers/SectionFormulaResultsController.cs:46-54`) with:

```csharp
    /// <summary>The section id plus **every** section id beneath it (within the same room) - the placements that
    /// count as "included" in this folder (mirrors <c>SectionPageController.IncludedSectionIdsAsync</c>).</summary>
    private Task<List<Guid>> IncludedSectionIdsAsync(Guid sectionId, Guid roomId, CancellationToken ct) =>
        SectionTree.SubtreeIdsAsync(_db, roomId, sectionId, ct);
```

- [ ] **Step 7: Swap `ChatController`**

Replace lines `468-470` of `src/Diariz.Api/Controllers/ChatController.cs`:

```csharp
        var childIds = await _db.Sections
            .Where(s => s.RoomId == roomId && s.ParentId == sectionId).Select(s => s.Id).ToListAsync(ct);
        var allIds = childIds.Append(sectionId).ToList();
```

with:

```csharp
        // The folder and everything beneath it - a parent's chat context spans its whole branch.
        var allIds = await SectionTree.SubtreeIdsAsync(_db, roomId, sectionId, ct);
```

- [ ] **Step 8: Swap `FormulaRunProcessor`**

Replace lines `279-283` of `src/Diariz.Api/Services/FormulaRunProcessor.cs`:

```csharp
        // The section id plus its child section ids (within the same room) - the placements that count as included.
        var includedSectionIds = await db.Sections
            .Where(s => s.RoomId == roomId && s.ParentId == sectionId)
            .Select(s => s.Id).ToListAsync(ct);
        includedSectionIds.Add(sectionId);
```

with:

```csharp
        // The section plus every section beneath it (same room) - the placements that count as included.
        var includedSectionIds = await SectionTree.SubtreeIdsAsync(db, roomId, sectionId, ct);
```

Then fix the now-stale doc comment on line `267`: change `one level of nesting deep, ordered by CreatedAt` to `spanning the whole subtree, ordered by CreatedAt`.

- [ ] **Step 9: Delegate `SearchController.SubtreeAsync`**

Replace the body of `SubtreeAsync` (`src/Diariz.Api/Controllers/SearchController.cs:113-128`) with:

```csharp
    private async Task<IReadOnlyList<Guid>> SubtreeAsync(Guid rootId, HashSet<Guid> visibleRooms, CancellationToken ct)
    {
        var root = await _db.Sections.FirstOrDefaultAsync(s => s.Id == rootId, ct);
        if (root is null || !visibleRooms.Contains(root.RoomId)) return [];
        return await SectionTree.SubtreeIdsAsync(_db, root.RoomId, rootId, ct);
    }
```

Keep the existing `<summary>` above it unchanged - it already describes "a folder plus all its descendants". Confirm `using Diariz.Api.Services;` is present.

- [ ] **Step 10: Run the full unit suite**

Run: `dotnet test tests/Diariz.Api.Tests`
Expected: PASS, no new failures, no warnings. Every existing folder-page / formula / chat test still passes, because at depth 2 the set is identical.

- [ ] **Step 11: Build the whole solution**

Run: `dotnet build Diariz.slnx`
Expected: Build succeeded, 0 warnings. (Unit-only test runs miss compile breaks in the integration project; building the solution catches them.)

- [ ] **Step 12: Commit**

```bash
git add src/Diariz.Api tests/Diariz.Api.Tests/SectionTreeTests.cs
git commit -m "refactor: folder roll-ups span the whole subtree, not just direct children"
```

---

### Task 3: `SectionSummaryProcessor` onto room scoping

The fifth call site is the odd one: it scopes the **section** lookup by `UserId` while the other four use `RoomId`. At depth 2 in a personal room the difference is invisible; a shared-room folder summary over a deeper tree would expose it.

**Files:**
- Modify: `src/Diariz.Api/Services/SectionSummaryProcessor.cs:75-82`

**Interfaces:**
- Consumes: `SectionTree.SubtreeIdsAsync` from Task 2.
- Produces: no signature change. `IncludedRecordingsAsync(DiarizDbContext db, Section section) -> Task<List<RecordingRef>>` keeps its shape and its `internal` visibility.

> **Corrected during execution (2026-08-05).** This task originally claimed no unit test was possible - `IncludedRecordingsAsync` is `internal`, and the repo has **no `InternalsVisibleTo`** - and pushed the proof into an integration test. That integration test was bogus: it composed `SectionTree.SubtreeIdsAsync` directly, so it could never exercise `SectionSummaryProcessor` and could never go red. The Task 3 implementer caught it and stopped.
>
> The constraint stands - do **not** add `InternalsVisibleTo`, and do **not** widen `IncludedRecordingsAsync` to `public`. But the right public seam already exists: **`SectionSummaryProcessor.ProcessAsync` is public**, and the class doc states it is static precisely so it can be "unit-tested with fake clients + an in-memory DbContext". Every fake needed is already in `tests/Diariz.Api.TestSupport/Fakes.cs`.

- [ ] **Step 1: Write the failing test**

Create `tests/Diariz.Api.Tests/SectionSummaryProcessorTests.cs`, driving `SectionSummaryProcessor.ProcessAsync` with `FakeSummarizationClient`, `FakeMeetingMinutesClient`, `FakeSummarizationSettingsResolver` and `FakeHubContext`. `tests/Diariz.Api.Tests/RecordingAiWebhookEmitTests.cs:53` shows the calling convention for the sibling `SummarizationProcessor.ProcessAsync`; `FormulaRunProcessorTests.SeedRecordingWithTranscript` shows how to seed a recording with a transcription and segments.

Seed a personal `Room` owned by the user (the placement join requires `Kind == RoomKind.Personal` and `OwnerUserId == section.UserId`), a three-level chain `Customers > Acme > Falcon`, and one recording with a transcription placed in `Falcon`. Assert the run reached that recording - what `FakeSummarizationClient` was asked to summarise is the cleanest signal. Assert on observable output, never on internals.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~SectionSummaryProcessorTests"`
Expected: FAIL - the current `UserId`-scoped direct-children query cannot see a recording two levels down. If it passes before the fix, the test is not exercising the change: stop and fix the test.

- [ ] **Step 3: Write the implementation**

Replace lines `75-82` of `src/Diariz.Api/Services/SectionSummaryProcessor.cs`:

```csharp
    /// <summary>The recordings filed directly under the section or under any of its child sections
    /// (ownership-scoped). Explicit query (not a filtered Include) so Npgsql and the in-memory provider agree.</summary>
    internal static async Task<List<RecordingRef>> IncludedRecordingsAsync(DiarizDbContext db, Section section)
    {
        var allIds = await db.Sections
            .Where(s => s.UserId == section.UserId && s.ParentId == section.Id)
            .Select(s => s.Id).ToListAsync();
        allIds.Add(section.Id);
```

with:

```csharp
    /// <summary>The recordings filed under the section or anywhere beneath it. The folder walk is scoped by
    /// <c>RoomId</c>, matching the other four roll-up sites - it used to be scoped by <c>UserId</c>, which is
    /// indistinguishable in a personal room at two levels and wrong for a shared room over a deeper tree.
    /// Explicit query (not a filtered Include) so Npgsql and the in-memory provider agree.</summary>
    internal static async Task<List<RecordingRef>> IncludedRecordingsAsync(DiarizDbContext db, Section section)
    {
        var allIds = await SectionTree.SubtreeIdsAsync(db, section.RoomId, section.Id, default);
```

Leave the placement join below it (`rm.OwnerUserId == section.UserId && rm.Kind == RoomKind.Personal`) **unchanged**. Widening that to the section's own room is a separate behaviour change and is out of scope here.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Diariz.Api.Tests`
Expected: PASS - the new test goes green, and no pre-existing test regresses.

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Services/SectionSummaryProcessor.cs tests/Diariz.Api.Tests/SectionSummaryProcessorTests.cs
git commit -m "fix: folder summaries walk the subtree by room, matching the other roll-ups"
```

---

### Task 4: Integration proof against real Postgres

The in-memory provider does not translate relational queries faithfully, so the roll-up needs proving against the real thing - this is exactly the class of silent wrong-set bug the phase exists to prevent.

**Files:**
- Create: `tests/Diariz.Api.IntegrationTests/SectionSubtreeIntegrationTests.cs`

**Interfaces:**
- Consumes: `SectionTree.SubtreeIdsAsync`; `ContainersFixture`, `IntegrationCollection`, `Http.Context`, `RoomScope` from the existing harness.
- Produces: nothing consumed later.

- [ ] **Step 1: Write the failing test**

Create `tests/Diariz.Api.IntegrationTests/SectionSubtreeIntegrationTests.cs`:

```csharp
using Diariz.Api.Services;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Domain.Entities;

namespace Diariz.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public class SectionSubtreeIntegrationTests(ContainersFixture fx)
{
    [Fact]
    public async Task SubtreeIds_ReachThreeLevelsDown_AndStopAtTheRoomBoundary()
    {
        Guid roomId, customersId, falconId, acmeId;
        await using (var db = fx.CreateDbContext())
        {
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
            var room = new Room { Id = Guid.NewGuid(), Name = $"P {Guid.NewGuid():N}", Kind = RoomKind.Personal, OwnerUserId = user.Id };
            // The decoy MUST be Shared with no owner. Postgres enforces one personal room per user via a
            // filtered unique index on Room.OwnerUserId (DiarizDbContext.cs:129), which the in-memory provider
            // never sees - two personal rooms for one user dies at seeding with 23505 before the test can run.
            var other = new Room { Id = Guid.NewGuid(), Name = $"O {Guid.NewGuid():N}", Kind = RoomKind.Shared };
            var customers = new Section { Id = Guid.NewGuid(), UserId = user.Id, RoomId = room.Id, Name = "Customers" };
            var acme = new Section { Id = Guid.NewGuid(), UserId = user.Id, RoomId = room.Id, Name = "Acme", ParentId = customers.Id };
            var falcon = new Section { Id = Guid.NewGuid(), UserId = user.Id, RoomId = room.Id, Name = "Falcon", ParentId = acme.Id };
            // Another room's folder claiming the same parent - the room filter must exclude it.
            var decoy = new Section { Id = Guid.NewGuid(), UserId = user.Id, RoomId = other.Id, Name = "Decoy", ParentId = customers.Id };
            db.AddRange(user, room, other, customers, acme, falcon, decoy);
            await db.SaveChangesAsync();
            (roomId, customersId, falconId, acmeId) = (room.Id, customers.Id, falcon.Id, acme.Id);
        }

        await using var verify = fx.CreateDbContext();
        var ids = await SectionTree.SubtreeIdsAsync(verify, roomId, customersId, default);

        Assert.Equal(3, ids.Count);
        Assert.Equal(customersId, ids[0]);
        Assert.Contains(acmeId, ids);
        Assert.Contains(falconId, ids);   // the grandchild - the bug this phase prevents
    }

    [Fact]
    public async Task DeletingATopFolder_CascadesThreeLevels_AndUngroupsTheDeepestRecording()
    {
        Guid userId, customersId, falconId, recId;
        await using (var db = fx.CreateDbContext())
        {
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
            var room = new Room { Id = Guid.NewGuid(), Name = $"P {Guid.NewGuid():N}", Kind = RoomKind.Personal, OwnerUserId = user.Id };
            var customers = new Section { Id = Guid.NewGuid(), UserId = user.Id, RoomId = room.Id, Name = "Customers" };
            var acme = new Section { Id = Guid.NewGuid(), UserId = user.Id, RoomId = room.Id, Name = "Acme", ParentId = customers.Id };
            var falcon = new Section { Id = Guid.NewGuid(), UserId = user.Id, RoomId = room.Id, Name = "Falcon", ParentId = acme.Id };
            var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = "k" };
            db.AddRange(user, room, customers, acme, falcon, rec);
            await db.SaveChangesAsync();
            await new RoomScope(db).PlaceInMainRoomAsync(rec.Id, user.Id, falcon.Id);
            (userId, customersId, falconId, recId) = (user.Id, customers.Id, falcon.Id, rec.Id);
        }

        await using (var db = fx.CreateDbContext())
        {
            db.Sections.Remove(await db.Sections.FindAsync(customersId) ?? throw new InvalidOperationException());
            await db.SaveChangesAsync();
        }

        await using var verify = fx.CreateDbContext();
        // Postgres cascades a self-referencing FK recursively, so the grandchild goes too.
        Assert.Null(await verify.Sections.FindAsync(falconId));
        var scope = new RoomScope(verify);
        var roomId = await scope.PersonalRoomIdAsync(userId);
        Assert.Null(await scope.SectionIdAsync(roomId, recId)); // ungrouped, not deleted
        Assert.NotNull(await verify.Recordings.FindAsync(recId));
    }
}
```

> **Corrected during execution (2026-08-05).** This task originally carried a third test, `FolderSummary_IncludedSet_ReachesAGrandchildAndIsRoomScoped`, described as Task 3's red-first proof. It was bogus - it composed `SectionTree.SubtreeIdsAsync` directly rather than driving `SectionSummaryProcessor`, so it asserted only that Task 1's code works and could never go red for Task 3. It has been removed; Task 3 now carries a real unit test through the public `ProcessAsync` seam. Add `using Diariz.Api.Services;` and `using Microsoft.EntityFrameworkCore;` if the two remaining tests need them.

- [ ] **Step 2: Run the tests**

Run: `dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~SectionSubtreeIntegrationTests"`
Expected: PASS, 2 tests. **Docker must be running.** Tasks 1-2 already implemented `SubtreeIdsAsync`, so both pass on arrival - this task is the real-Postgres proof of behaviour the in-memory provider cannot faithfully model (the recursive self-FK cascade especially), not a red-first cycle.

- [ ] **Step 3: Commit**

```bash
git add tests/Diariz.Api.IntegrationTests/SectionSubtreeIntegrationTests.cs
git commit -m "test: prove subtree roll-up and 3-level cascade against real Postgres"
```

---

### Task 5: Release chores for PR 1

**Files:**
- Modify: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`
- Modify: `apps/web/src/lib/releases.ts`
- Modify: `docs/Overall_Synopsis_of_Platform.md:624`

**Interfaces:**
- Consumes: nothing.
- Produces: nothing.

- [ ] **Step 1: Bump the version and all four mirrors to `0.178.1`**

Set `"version": "0.178.1"` in `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, and `integrations/n8n-nodes-diariz/package.json`; set `<Version>0.178.1</Version>` in `src/Diariz.Api/Diariz.Api.csproj`.

- [ ] **Step 2: Find the PR number**

Run: `gh pr list --state all --limit 1 --json number --jq '.[0].number'`

Add 1 to that. Dependabot and issues share the sequence, so treat it as provisional: after `gh pr create` in Step 6, check the number it prints and correct `releases.ts` with an amended commit if it differs.

- [ ] **Step 3: Add the release entry**

At the top of the `RELEASES` array in `apps/web/src/lib/releases.ts`, using the PR number from Step 2:

```ts
  {
    version: "0.178.1",
    date: "2026-08-04",
    pr: <number from step 2>,
    headline: "Folder roll-ups follow the whole folder tree",
    summary:
      "Everything a folder rolls up - its summary, minutes, formula runs, chat context, and the actions, notes and attachments on its page - now follows the folder tree all the way down rather than stopping one level in. With folders limited to two levels those were the same thing, so nothing changes for you today; this is the groundwork for deeper folders. Folder summaries in a shared room also now follow that room's folders rather than your personal ones.",
    fixed: [
      "A folder's page, summary, minutes, formula runs and chat context now include recordings filed in folders further down, not only in its immediate sub-folders.",
      "Folder summaries in a shared room now resolve that room's folder tree instead of your personal one.",
    ],
  },
```

- [ ] **Step 4: Update the architecture doc**

In `docs/Overall_Synopsis_of_Platform.md`, around line 624, replace:

```
  `FormulaRunProcessor.RunOverSectionAsync` resolves the folder's recording set **room-aware, one level deep**
  (the section + its direct sub-sections, via `RoomRecordings` placement scoped to `section.RoomId` - the same
```

with:

```
  `FormulaRunProcessor.RunOverSectionAsync` resolves the folder's recording set **room-aware, across the whole
  subtree** (the section + every folder beneath it, via `SectionTree.SubtreeIdsAsync` and the `RoomRecordings`
  placement scoped to `section.RoomId` - the same
```

- [ ] **Step 5: Run the web tests and the full build**

Run: `cd apps/web && npm test && npm run build`
Expected: PASS - `versionMirrors.test.ts` and `releases.test.ts` confirm the bump is consistent.

Run: `cd ../.. && dotnet build Diariz.slnx && dotnet test tests/Diariz.Api.Tests`
Expected: Build succeeded, tests PASS.

- [ ] **Step 6: Commit, push, open the PR**

```bash
git add -A
git commit -m "chore: release 0.178.1"
git push -u origin feat/folder-subtree-rollup
gh pr create --title "Folder roll-ups span the whole subtree" --body "$(cat <<'EOF'
Replaces five hand-rolled "section plus its direct children" queries with one `SectionTree` helper that walks the whole subtree, and moves `SectionSummaryProcessor` onto room scoping to match the other four.

**Behaviour-preserving today.** Folders are still capped at two levels, where "whole subtree" and "direct children" are the same set. This lands the semantic change while it is provably a no-op, ahead of lifting the cap.

Also resolves an existing disagreement: the nav's folder count badge already promised the whole subtree while the folder page aggregated one level.

**Deployment surface:** server redeploy only. No desktop release, no migration.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

- [ ] **Step 7: Correct the PR number if needed**

If the PR number `gh` printed differs from your guess in Step 2, fix `releases.ts` and push:

```bash
git add apps/web/src/lib/releases.ts
git commit -m "chore: correct release PR number"
git push
```

---

# PHASE 2 - Lift the cap to 8 (PR 2, version 0.179.0)

Wait for PR 1 to merge, then:

```bash
git checkout main
git pull
git checkout -b feat/deep-folders
```

---

### Task 6: The depth and height rules

**Files:**
- Modify: `src/Diariz.Api/Services/SectionTree.cs`
- Modify: `tests/Diariz.Api.Tests/SectionTreeTests.cs`

**Interfaces:**
- Consumes: `SectionLink`, `SectionTree.Subtree` from Task 1.
- Produces: `SectionTree.MaxDepth` (`const int` = 8), `SectionTree.Depth(IReadOnlyCollection<SectionLink>, Guid) -> int` (top-level = 1, unknown id = 0), `SectionTree.Height(IReadOnlyCollection<SectionLink>, Guid) -> int` (a leaf = 1).

- [ ] **Step 1: Write the failing tests**

Append to the `SectionTreeTests` class:

```csharp
    [Fact]
    public void Depth_CountsTopLevelAsOne()
    {
        Assert.Equal(1, SectionTree.Depth(Tree(), Customers));
        Assert.Equal(2, SectionTree.Depth(Tree(), Acme));
        Assert.Equal(3, SectionTree.Depth(Tree(), Falcon));
    }

    [Fact]
    public void Depth_OfAnUnknownId_IsZero()
    {
        Assert.Equal(0, SectionTree.Depth(Tree(), Guid.NewGuid()));
    }

    [Fact]
    public void Depth_WithACycle_Terminates()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        SectionLink[] cyclic = [new(a, b), new(b, a)];
        Assert.True(SectionTree.Depth(cyclic, a) > 0); // the assertion that matters is that it returns at all
    }

    [Fact]
    public void Height_OfALeaf_IsOne()
    {
        Assert.Equal(1, SectionTree.Height(Tree(), Falcon));
    }

    [Fact]
    public void Height_CountsTheDeepestBranchIncludingTheRoot()
    {
        Assert.Equal(3, SectionTree.Height(Tree(), Customers)); // Customers > Acme > Falcon
        Assert.Equal(2, SectionTree.Height(Tree(), Acme));
    }

    [Fact]
    public void MaxDepth_IsEight()
    {
        Assert.Equal(8, SectionTree.MaxDepth);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~SectionTreeTests"`
Expected: FAIL to compile - `Depth`, `Height` and `MaxDepth` do not exist.

- [ ] **Step 3: Write the implementation**

Add to the `SectionTree` class, after `Subtree`:

```csharp
    /// <summary>How deep folders may nest. Top-level is depth 1, so 8 means eight levels of folder. A guardrail,
    /// not a design constraint: it bounds the breadcrumb and the folder pickers, and turns a cycle that somehow
    /// evaded the descendant check into a rejected request rather than a hung one.</summary>
    public const int MaxDepth = 8;

    /// <summary>The folder's level, counting top-level as 1. Zero for an id that is not in the list. The visited
    /// set bounds a <c>ParentId</c> cycle, which the schema does not prevent.</summary>
    public static int Depth(IReadOnlyCollection<SectionLink> sections, Guid id)
    {
        var byId = sections.ToDictionary(s => s.Id);
        if (!byId.TryGetValue(id, out var current)) return 0;

        var depth = 1;
        var seen = new HashSet<Guid> { id };
        while (current.ParentId is Guid parentId && seen.Add(parentId) && byId.TryGetValue(parentId, out current))
            depth++;
        return depth;
    }

    /// <summary>How many levels the subtree rooted here spans, counting the root as 1. Moving a folder moves its
    /// whole branch, so this is what a reparent has to add to the target's depth.</summary>
    public static int Height(IReadOnlyCollection<SectionLink> sections, Guid rootId)
    {
        var subtree = Subtree(sections, rootId).ToHashSet();
        var rootDepth = Depth(sections, rootId);
        if (rootDepth == 0) return 1; // unknown root: it is its own single level

        var deepest = rootDepth;
        foreach (var id in subtree)
        {
            var d = Depth(sections, id);
            if (d > deepest) deepest = d;
        }
        return deepest - rootDepth + 1;
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~SectionTreeTests"`
Expected: PASS, 11 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Services/SectionTree.cs tests/Diariz.Api.Tests/SectionTreeTests.cs
git commit -m "feat: add folder depth and height rules with a max depth of 8"
```

---

### Task 7: `Create` accepts any depth up to 8

**Files:**
- Modify: `src/Diariz.Api/Controllers/SectionsController.cs:67-102`
- Modify: `tests/Diariz.Api.Tests/SectionsControllerTests.cs:210-221`

**Interfaces:**
- Consumes: `SectionTree.LinksAsync`, `SectionTree.Depth`, `SectionTree.MaxDepth` from Task 6.
- Produces: no signature change.

- [ ] **Step 1: Rewrite the cap test as a depth test**

In `tests/Diariz.Api.Tests/SectionsControllerTests.cs`, replace the whole `Create_UnderASubSection_RejectsThirdLevel` test (lines `210-221`) with these two:

```csharp
    [Fact]
    public async Task Create_UnderASubSection_NowNestsAThirdLevel()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var parent = await SeedSection(db, userId, "Customers");
        var child = await SeedSection(db, userId, "Acme", parentId: parent.Id);

        var result = await Build(db, userId).Create(new CreateSectionRequest("Project Falcon", child.Id));

        var dto = Assert.IsType<SectionDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(child.Id, dto.ParentId);
    }

    [Fact]
    public async Task Create_BeyondMaxDepth_ReturnsBadRequest()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();

        // Build a chain exactly MaxDepth deep, then try to add one more under the deepest.
        Guid? parentId = null;
        for (var i = 0; i < SectionTree.MaxDepth; i++)
            parentId = (await SeedSection(db, userId, $"L{i}", parentId)).Id;

        var result = await Build(db, userId).Create(new CreateSectionRequest("TooDeep", parentId));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~SectionsControllerTests"`
Expected: `Create_UnderASubSection_NowNestsAThirdLevel` FAILS (gets `BadRequestObjectResult`, the old cap). `Create_BeyondMaxDepth_ReturnsBadRequest` passes for the wrong reason - the old cap rejects everything past level 2. Both are made right by Step 3.

- [ ] **Step 3: Write the implementation**

In `src/Diariz.Api/Controllers/SectionsController.cs`, replace lines `84-90`:

```csharp
        // A sub-section's parent must be in the caller's room and must itself be top-level (two-level cap).
        if (req.ParentId is { } parentId)
        {
            var parent = await _db.Sections.FirstOrDefaultAsync(s => s.Id == parentId && s.RoomId == roomId);
            if (parent is null) return NotFound();
            if (parent.ParentId is not null) return BadRequest("Sections can only be nested one level deep.");
        }
```

with:

```csharp
        // The parent must be in the caller's room, and adding a level beneath it must stay within MaxDepth.
        if (req.ParentId is { } parentId)
        {
            var parent = await _db.Sections.FirstOrDefaultAsync(s => s.Id == parentId && s.RoomId == roomId);
            if (parent is null) return NotFound();
            var links = await SectionTree.LinksAsync(_db, roomId, ct);
            if (SectionTree.Depth(links, parentId) >= SectionTree.MaxDepth)
                return BadRequest($"Folders can only be nested {SectionTree.MaxDepth} levels deep.");
        }
```

Then update the endpoint documentation. Replace lines `69-75`:

```csharp
    [EndpointDescription(
        "Adds a folder to a room, or a sub-folder when you pass `parentId`. **Idempotent by name**: if a " +
        "folder with the same name already exists under the same parent, that one is returned instead of a " +
        "duplicate being created - so re-running an import does not litter the tree. Compare the returned id " +
        "with what you expected if that matters to you.\n\n" +
        "Nesting is capped at one level, so a parent that is itself a sub-folder is rejected with 400. Needs " +
        "`ManageContents` in the room; you always hold it in your own personal room.")]
```

with:

```csharp
    [EndpointDescription(
        "Adds a folder to a room, or a sub-folder when you pass `parentId`. **Idempotent by name**: if a " +
        "folder with the same name already exists under the same parent, that one is returned instead of a " +
        "duplicate being created - so re-running an import does not litter the tree. Compare the returned id " +
        "with what you expected if that matters to you.\n\n" +
        "Folders nest up to **8 levels** deep (top level counts as 1); going deeper is rejected with 400. Needs " +
        "`ManageContents` in the room; you always hold it in your own personal room.")]
```

And the List endpoint's description, lines `48-52`:

```csharp
    [EndpointDescription(
        "The folder tree of one room, flat and in display order. Defaults to your personal room; pass `roomId` " +
        "for a shared room you belong to. Build the tree from `parentId` - null means top level, and nesting " +
        "only ever goes **one level deep**, so a folder with a parent can never have children of its own.\n\n" +
        "Each room has its own independent folders. A non-member gets 404 rather than learning the room exists.")]
```

becomes:

```csharp
    [EndpointDescription(
        "The folder tree of one room, flat and in display order. Defaults to your personal room; pass `roomId` " +
        "for a shared room you belong to. Build the tree from `parentId` - null means top level, and folders " +
        "nest up to **8 levels** deep.\n\n" +
        "Each room has its own independent folders. A non-member gets 404 rather than learning the room exists.")]
```

Confirm `using Diariz.Api.Services;` is present at the top of the file.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~SectionsControllerTests"`
Expected: PASS. Note `Create_InSharedRoom_WithManageContents_ScopesToThatRoom` still passes - it only nests one level.

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Controllers/SectionsController.cs tests/Diariz.Api.Tests/SectionsControllerTests.cs
git commit -m "feat: create folders up to 8 levels deep"
```

---

### Task 8: `Reorder` gains the height and descendant rules

The move rule is **not** the create rule: dragging a folder moves its whole branch, so `depth(newParent) + height(moved) <= MaxDepth`. And with the cap gone, nothing else prevents a cycle.

**Files:**
- Modify: `src/Diariz.Api/Controllers/SectionsController.cs:104-153`
- Modify: `tests/Diariz.Api.Tests/SectionsControllerTests.cs:264-290`

**Interfaces:**
- Consumes: `SectionTree.LinksAsync`, `Depth`, `Height`, `Subtree`, `MaxDepth`.
- Produces: no signature change.

- [ ] **Step 1: Rewrite the two cap tests**

In `tests/Diariz.Api.Tests/SectionsControllerTests.cs`, replace both `Reorder_UnderASubSection_RejectsThirdLevel` and `Reorder_MovingAParentWithChildren_UnderAnother_IsRejected` (lines `264-290`) with:

```csharp
    [Fact]
    public async Task Reorder_UnderASubSection_NowNestsAThirdLevel()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var parent = await SeedSection(db, userId, "Customers");
        var child = await SeedSection(db, userId, "Acme", parentId: parent.Id);
        var loose = await SeedSection(db, userId, "Loose");

        var result = await Build(db, userId).Reorder(new ReorderSectionsRequest(child.Id, [loose.Id]));

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(child.Id, (await db.Sections.FindAsync(loose.Id))!.ParentId);
    }

    [Fact]
    public async Task Reorder_MovingAParentWithChildren_NowCarriesItsBranch()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var top = await SeedSection(db, userId, "Top");
        var hasChild = await SeedSection(db, userId, "HasChild");
        var grandchild = await SeedSection(db, userId, "Kid", parentId: hasChild.Id);

        var result = await Build(db, userId).Reorder(new ReorderSectionsRequest(top.Id, [hasChild.Id]));

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(top.Id, (await db.Sections.FindAsync(hasChild.Id))!.ParentId);
        // The branch travels with it - Kid is untouched and is now three levels down.
        Assert.Equal(hasChild.Id, (await db.Sections.FindAsync(grandchild.Id))!.ParentId);
    }

    [Fact]
    public async Task Reorder_IntoItsOwnDescendant_IsRejected()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var parent = await SeedSection(db, userId, "Customers");
        var child = await SeedSection(db, userId, "Acme", parentId: parent.Id);

        // Moving Customers under its own child would orphan the whole branch from the tree.
        var result = await Build(db, userId).Reorder(new ReorderSectionsRequest(child.Id, [parent.Id]));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null((await db.Sections.FindAsync(parent.Id))!.ParentId); // unchanged
    }

    [Fact]
    public async Task Reorder_WhenTheBranchWouldNotFit_IsRejected()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();

        // A target chain MaxDepth-1 deep, and a separate 2-level branch. 7 + 2 > 8, so the move is refused
        // even though the target itself is a legal place for a leaf.
        Guid? targetId = null;
        for (var i = 0; i < SectionTree.MaxDepth - 1; i++)
            targetId = (await SeedSection(db, userId, $"L{i}", targetId)).Id;
        var branch = await SeedSection(db, userId, "Branch");
        await SeedSection(db, userId, "BranchKid", parentId: branch.Id);

        var result = await Build(db, userId).Reorder(new ReorderSectionsRequest(targetId, [branch.Id]));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null((await db.Sections.FindAsync(branch.Id))!.ParentId); // unchanged
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~SectionsControllerTests"`
Expected: `Reorder_UnderASubSection_NowNestsAThirdLevel`, `Reorder_MovingAParentWithChildren_NowCarriesItsBranch` and `Reorder_IntoItsOwnDescendant_IsRejected` FAIL. (The first two hit the old cap; the third currently succeeds and creates a cycle - which is exactly the bug being fixed.)

- [ ] **Step 3: Write the implementation**

In `src/Diariz.Api/Controllers/SectionsController.cs`, replace lines `125-142`:

```csharp
        if (req.ParentId is { } parentId)
        {
            if (ids.Contains(parentId)) return BadRequest("A section cannot be its own parent.");
            var parent = await _db.Sections.FirstOrDefaultAsync(s => s.Id == parentId && s.RoomId == roomId);
            if (parent is null) return NotFound();
            if (parent.ParentId is not null) return BadRequest("Sections can only be nested one level deep.");
        }

        var sections = await _db.Sections.Where(s => ids.Contains(s.Id) && s.RoomId == roomId).ToListAsync();
        if (sections.Count != ids.Count) return NotFound();

        // Moving a section under a parent is only allowed if that section has no children of its own.
        if (req.ParentId is not null)
        {
            var haveChildren = await _db.Sections.AnyAsync(
                s => s.RoomId == roomId && s.ParentId != null && ids.Contains(s.ParentId.Value));
            if (haveChildren) return BadRequest("A section with sub-sections can't become a sub-section.");
        }
```

with:

```csharp
        if (req.ParentId is { } parentId)
        {
            if (ids.Contains(parentId)) return BadRequest("A section cannot be its own parent.");
            var parent = await _db.Sections.FirstOrDefaultAsync(s => s.Id == parentId && s.RoomId == roomId);
            if (parent is null) return NotFound();
        }

        var sections = await _db.Sections.Where(s => ids.Contains(s.Id) && s.RoomId == roomId).ToListAsync();
        if (sections.Count != ids.Count) return NotFound();

        // Two rules replace the old two-level cap, and they are not the same rule.
        if (req.ParentId is { } target)
        {
            var links = await SectionTree.LinksAsync(_db, roomId, ct);

            // 1. Cycles. The cap used to make these impossible; nothing else does. Moving a folder into its own
            //    descendant would detach the whole branch from the tree - unreachable except through search.
            foreach (var id in ids)
                if (SectionTree.Subtree(links, id).Contains(target))
                    return BadRequest("A folder can't be moved inside itself.");

            // 2. Depth. A move carries the folder's whole branch, so the target's depth plus the branch's height
            //    is what must fit - a legal target can still be too deep for a tall branch.
            var targetDepth = SectionTree.Depth(links, target);
            foreach (var id in ids)
                if (targetDepth + SectionTree.Height(links, id) > SectionTree.MaxDepth)
                    return BadRequest($"That folder is too deep to hold this one (max {SectionTree.MaxDepth} levels).");
        }
```

Then replace the `<summary>` on lines `104-106`:

```csharp
    /// <summary>Drag-and-drop for sections: set the parent and 0-based position of each listed section in
    /// one call (reorder among siblings and/or reparent). Rejects moves that would nest more than one level
    /// deep — either targeting a parent that itself has a parent, or moving a section that has children.</summary>
```

with:

```csharp
    /// <summary>Drag-and-drop for sections: set the parent and 0-based position of each listed section in
    /// one call (reorder among siblings and/or reparent). Rejects a move into the folder's own descendant (a
    /// cycle) and one whose branch would not fit within <see cref="SectionTree.MaxDepth"/>.</summary>
```

And the `EndpointDescription` on lines `109-116`:

```csharp
    [EndpointDescription(
        "Sets the parent and 0-based position of each listed folder in one call, covering both resequencing " +
        "among siblings and moving folders under a new parent. Pass a null `parentId` to move them to the top " +
        "level.\n\n" +
        "The one-level nesting cap is enforced here too, and in two ways: the target parent may not itself be " +
        "a sub-folder, and a folder that **has** sub-folders may not become one (both 400). A folder cannot be " +
        "its own parent. Every listed id must exist in the room, otherwise the whole call 404s and nothing " +
        "moves. Needs `ManageContents`.")]
```

becomes:

```csharp
    [EndpointDescription(
        "Sets the parent and 0-based position of each listed folder in one call, covering both resequencing " +
        "among siblings and moving folders under a new parent. Pass a null `parentId` to move them to the top " +
        "level.\n\n" +
        "A folder moves with its whole branch, so two rules apply (both 400): the target may not be the folder " +
        "itself or anything beneath it, and the target's depth plus the moved branch's height may not exceed " +
        "**8** levels - so a legal target can still be too deep for a tall branch. Every listed id must exist " +
        "in the room, otherwise the whole call 404s and nothing moves. Needs `ManageContents`.")]
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~SectionsControllerTests"`
Expected: PASS.

- [ ] **Step 5: Run the whole backend and build the solution**

Run: `dotnet test tests/Diariz.Api.Tests && dotnet build Diariz.slnx`
Expected: PASS, Build succeeded, 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Controllers/SectionsController.cs tests/Diariz.Api.Tests/SectionsControllerTests.cs
git commit -m "feat: reparent folders at any depth, guarding cycles and branch height"
```

---

### Task 9: `buildRecordingTree` becomes recursive

**Files:**
- Modify: `apps/web/src/lib/recordingTree.ts:1-72`
- Modify: `apps/web/src/lib/recordingTree.test.ts`

**Interfaces:**
- Consumes: `SectionDto`, `RecordingSummary` from `./types`.
- Produces: `buildRecordingTree(recordings, sections) -> RecordingTree` (unchanged signature). `SectionNode` keeps `{ id, name, items, children }`; `children` may now be arbitrarily deep.

- [ ] **Step 1: Write the failing test**

Append to `apps/web/src/lib/recordingTree.test.ts`, inside the `describe("buildRecordingTree", ...)` block:

```ts
  it("nests to three levels and deeper", () => {
    const sections = [
      sec("customers", "Customers"),
      sec("acme", "Acme", "customers"),
      sec("falcon", "Falcon", "acme"),
    ];
    const recordings = [rec("r-deep", "falcon")];

    const tree = buildRecordingTree(recordings, sections);

    const customers = tree.sections.find((s) => s.id === "customers")!;
    const acme = customers.children[0];
    expect(acme.id).toBe("acme");
    expect(acme.children[0].id).toBe("falcon");
    expect(acme.children[0].items.map((r) => r.id)).toEqual(["r-deep"]);
  });

  it("survives a parentId cycle instead of hanging", () => {
    // Nothing in the schema prevents one; a naive recursion would never return.
    const sections = [sec("a", "A", "b"), sec("b", "B", "a")];

    const tree = buildRecordingTree([], sections);

    // Neither is top-level, so nothing is rendered - the assertion that matters is that we got here at all.
    expect(tree.sections).toEqual([]);
  });
```

Check the existing helper names at the top of that file - they are `sec(id, name, parentId, position)` and `rec(id, sectionId, sectionName)`. Use them as defined there.

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd apps/web && npx vitest run src/lib/recordingTree.test.ts`
Expected: FAIL - `acme.children[0]` is `undefined`, because the current build only goes one level.

- [ ] **Step 3: Write the implementation**

In `apps/web/src/lib/recordingTree.ts`, replace the doc comment on lines `4-6`:

```ts
/// A section node in the two-level recordings tree. Top-level sections may have `children`
/// (sub-sections); sub-sections never do (the hierarchy is capped at two levels). `items` are the
/// recordings filed directly under this section — recordings may live at either level.
```

with:

```ts
/// A section node in the recordings tree, which nests to any depth (the API caps it at 8 levels).
/// `items` are the recordings filed directly under this section - recordings may live at any level.
```

Then replace lines `25-71` (from the `/// Build the two-level tree...` comment through `return { sections: sectionNodes, ungrouped };`) with:

```ts
/// Build the recordings tree. Recordings are filed under their `sectionId` at whatever depth that
/// section sits; unknown section ids (e.g. the sections list hasn't loaded yet) fall back to a
/// synthetic top-level section using the recording's own `sectionName`.
export function buildRecordingTree(recordings: RecordingSummary[], sections: SectionDto[]): RecordingTree {
  const known = new Map(sections.map((s) => [s.id, s]));
  const recsBySection = new Map<string, RecordingSummary[]>();
  const ungrouped: RecordingSummary[] = [];
  for (const r of recordings) {
    if (!r.sectionId) {
      ungrouped.push(r);
      continue;
    }
    const arr = recsBySection.get(r.sectionId) ?? [];
    arr.push(r);
    recsBySection.set(r.sectionId, arr);
  }

  // Index children by parent once, so the recursive build is a lookup per node rather than a scan.
  // Treat a null/undefined parent - or one we don't know - as top-level (defensive against partial data).
  const childrenOfParent = new Map<string, SectionDto[]>();
  const tops: SectionDto[] = [];
  for (const s of sections) {
    if (s.parentId && known.has(s.parentId)) {
      const arr = childrenOfParent.get(s.parentId) ?? [];
      arr.push(s);
      childrenOfParent.set(s.parentId, arr);
    } else if (!s.parentId) {
      tops.push(s);
    }
  }
  tops.sort(bySiblingOrder);

  // `seen` bounds a parentId cycle, which the schema does not prevent - without it this never returns.
  const seen = new Set<string>();
  const build = (s: SectionDto): SectionNode => {
    seen.add(s.id);
    const children = (childrenOfParent.get(s.id) ?? [])
      .filter((c) => !seen.has(c.id))
      .sort(bySiblingOrder)
      .map(build);
    return { id: s.id, name: s.name, items: recsBySection.get(s.id) ?? [], children };
  };

  const sectionNodes = tops.map(build);

  // Recordings pointing at a section we don't know yet → synthetic top-level groups (load-order safety).
  for (const [sectionId, items] of recsBySection) {
    if (!known.has(sectionId)) {
      sectionNodes.push({ id: sectionId, name: items[0]?.sectionName ?? "Section", items, children: [] });
    }
  }

  return { sections: sectionNodes, ungrouped };
}
```

Also update the `RecordingTree.sections` comment on line `15` from `/// Top-level sections in display order; each may carry direct recordings and sub-sections.` to `/// Top-level sections in display order; each may carry direct recordings and sub-sections, to any depth.`

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd apps/web && npx vitest run src/lib/recordingTree.test.ts src/lib/drillView.test.ts`
Expected: PASS. `drillView.test.ts` exercises `childrenOf`, `breadcrumbOf` and `recordingCountOf` over this tree - they already walk generically, so they should be unaffected.

- [ ] **Step 5: Commit**

```bash
git add apps/web/src/lib/recordingTree.ts apps/web/src/lib/recordingTree.test.ts
git commit -m "feat: build the recordings tree to any depth"
```

---

### Task 10: The nav stops blocking the third level

**Files:**
- Modify: `apps/web/src/lib/drillView.ts:58-77`
- Modify: `apps/web/src/lib/drillView.test.ts:81-102`
- Modify: `apps/web/src/components/RecordingsPanel.tsx:326-327, 415-427`
- Modify: `apps/web/src/components/nav/SectionRow.tsx:33-37`
- Modify: `apps/web/src/locales/en/workspace.json:417`

**Interfaces:**
- Consumes: `SectionDto`.
- Produces: `sectionCreateTarget(sections, sectionId) -> SectionCreateTarget` (unchanged signature, unchanged union: `{kind:"root"} | {kind:"child", parent} | {kind:"blocked"}`), plus a new exported `MAX_FOLDER_DEPTH = 8` and `depthOf(sections, sectionId) -> number` from `drillView.ts`.

- [ ] **Step 1: Write the failing tests**

In `apps/web/src/lib/drillView.test.ts`, first extend the fixture at the top. Replace lines `12-17`:

```ts
// Customers ▸ Ambu, plus a loose recording at the root.
const sections = [
  section("customers", "Customers"),
  section("ambu", "Ambu", "customers"),
  section("podcasts", "Podcasts", null, 1),
];
```

with:

```ts
// Customers > Ambu, plus a loose recording at the root.
const sections = [
  section("customers", "Customers"),
  section("ambu", "Ambu", "customers"),
  section("podcasts", "Podcasts", null, 1),
];

// A chain exactly MAX_FOLDER_DEPTH deep, for the cap cases.
const deepChain: SectionDto[] = Array.from({ length: MAX_FOLDER_DEPTH }, (_, i) =>
  section(`d${i}`, `L${i}`, i === 0 ? null : `d${i - 1}`),
);
```

Add `MAX_FOLDER_DEPTH, depthOf` to the import on line 3:

```ts
import { childrenOf, breadcrumbOf, recordingCountOf, sectionCreateTarget, depthOf, MAX_FOLDER_DEPTH } from "./drillView";
```

Then replace the whole `describe("sectionCreateTarget", ...)` block (lines `81-102`) with:

```ts
describe("depthOf", () => {
  it("counts the root as 0 and a top-level folder as 1", () => {
    expect(depthOf(sections, null)).toBe(0);
    expect(depthOf(sections, "customers")).toBe(1);
    expect(depthOf(sections, "ambu")).toBe(2);
  });

  it("is 0 for an unknown id", () => {
    expect(depthOf(sections, "gone")).toBe(0);
  });
});

describe("sectionCreateTarget", () => {
  it("at the root: a new top-level section", () => {
    expect(sectionCreateTarget(sections, null)).toEqual({ kind: "root" });
  });

  it("inside a top-level section: a sub-section of it", () => {
    expect(sectionCreateTarget(sections, "customers")).toEqual({
      kind: "child",
      parent: sections[0],
    });
  });

  // The cap is now 8 levels, not 1, so a sub-section is an ordinary parent.
  it("inside a sub-section: a sub-section of that", () => {
    expect(sectionCreateTarget(sections, "ambu")).toEqual({
      kind: "child",
      parent: sections[1],
    });
  });

  it("at the maximum depth: blocked", () => {
    const deepest = deepChain[MAX_FOLDER_DEPTH - 1];
    expect(sectionCreateTarget(deepChain, deepest.id)).toEqual({ kind: "blocked" });
  });

  // Drilled into a folder deleted from another tab: creating under a ghost parent would only 404.
  it("blocked for an unknown id rather than falling back to the root", () => {
    expect(sectionCreateTarget(sections, "gone")).toEqual({ kind: "blocked" });
  });
});
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd apps/web && npx vitest run src/lib/drillView.test.ts`
Expected: FAIL to compile - `depthOf` and `MAX_FOLDER_DEPTH` are not exported.

- [ ] **Step 3: Write the implementation**

In `apps/web/src/lib/drillView.ts`, replace lines `58-77` (from `/// Where a new folder created from the toolbar...` to the end of `sectionCreateTarget`) with:

```ts
/// How deep folders may nest, mirroring `SectionTree.MaxDepth` on the API. Top level is 1, so this is the
/// number of folder levels. Kept in sync by hand: the two constants are in different languages, and the
/// server is the one that enforces it - this copy only decides what the UI offers.
export const MAX_FOLDER_DEPTH = 8;

/// The level a folder sits at: 0 for the room root, 1 for a top-level folder. Zero for an unknown id.
/// Guards against a `parentId` cycle, which nothing in the schema prevents.
export function depthOf(sections: SectionDto[], sectionId: string | null): number {
  if (sectionId === null) return 0;
  const byId = new Map(sections.map((s) => [s.id, s]));
  let current = byId.get(sectionId);
  if (!current) return 0;

  let depth = 1;
  const seen = new Set<string>([sectionId]);
  while (current.parentId && !seen.has(current.parentId)) {
    seen.add(current.parentId);
    const parent = byId.get(current.parentId);
    if (!parent) break;
    current = parent;
    depth++;
  }
  return depth;
}

/// Where a new folder created from the toolbar should go, given where you are browsing. `blocked` covers
/// both ends of the same problem: the drill is at the depth cap (`SectionsController.Create` would 400) or
/// inside an id that is no longer in the tree (deleted from another tab - the level renders empty, and
/// creating under a ghost parent would only 404).
export type SectionCreateTarget =
  | { kind: "root" }
  | { kind: "child"; parent: SectionDto }
  | { kind: "blocked" };

/// Unlike the rest of this module, this one **does** encode the depth cap - it has to, because it decides
/// what the API will accept.
export function sectionCreateTarget(
  sections: SectionDto[],
  sectionId: string | null,
): SectionCreateTarget {
  if (sectionId === null) return { kind: "root" };
  const parent = sections.find((s) => s.id === sectionId);
  if (!parent) return { kind: "blocked" };
  if (depthOf(sections, sectionId) >= MAX_FOLDER_DEPTH) return { kind: "blocked" };
  return { kind: "child", parent };
}
```

Also update the module header comment on lines `6-7`:

```ts
/// Everything here walks `parentId` generically. The domain caps sections at two levels (enforced in
/// `SectionsController`), but nothing below assumes that — lifting the cap should not touch the nav.
```

becomes:

```ts
/// Everything here walks `parentId` generically, so it works at any depth. The API caps folders at
/// `MAX_FOLDER_DEPTH` levels; only `sectionCreateTarget` below knows that.
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd apps/web && npx vitest run src/lib/drillView.test.ts`
Expected: PASS.

- [ ] **Step 5: Let the panel nest at any depth**

In `apps/web/src/components/RecordingsPanel.tsx`, replace lines `326-327`:

```tsx
  // Only top-level folders may take sub-folders (the domain caps the hierarchy at two levels).
  const childrenCanNest = drill.sectionId === null;
```

with:

```tsx
  // A folder row on this level may take sub-folders as long as one more level still fits. The rows are one
  // level below the drill position, so their own depth is the drill's depth + 1.
  const childrenCanNest = depthOf(sections, drill.sectionId) + 1 < MAX_FOLDER_DEPTH;
```

Add `depthOf, MAX_FOLDER_DEPTH` to the `drillView` import on line 22:

```tsx
import { childrenOf, breadcrumbOf, recordingCountOf, sectionCreateTarget, depthOf, MAX_FOLDER_DEPTH } from "../lib/drillView";
```

Then fix the root drop handler on lines `396-399`, which currently only reparents to the top level when `childrenCanNest`. The root always accepts a folder, whatever the depth cap says about its children:

```tsx
                const draggedSection = e.dataTransfer.getData(SECTION_MIME);
                if (draggedSection) {
                  if (childrenCanNest) nestSection(null, draggedSection); // root: promote to top level
                  return;
                }
```

becomes:

```tsx
                const draggedSection = e.dataTransfer.getData(SECTION_MIME);
                if (draggedSection) {
                  // The level's background reparents to the level itself - at the root that is a promotion to
                  // top level, which is always legal regardless of how deep this level's children may go.
                  nestSection(drill.sectionId, draggedSection);
                  return;
                }
```

- [ ] **Step 6: Update the `SectionRow` comment**

In `apps/web/src/components/nav/SectionRow.tsx`, replace lines `33-37`:

```tsx
  /// Everything underneath, including sub-folders' recordings - the row promises what you'll find inside.
  count: number;
  /// Whether this folder may take sub-folders (the domain caps the hierarchy at two levels), which decides
  /// both the "New sub-section" action and what a dropped folder does here.
  canNest: boolean;
```

with:

```tsx
  /// Everything underneath, including sub-folders' recordings - the row promises what you'll find inside.
  count: number;
  /// Whether this folder may take sub-folders (false once it sits at the depth cap), which decides both the
  /// "New sub-section" action and what a dropped folder does here.
  canNest: boolean;
```

- [ ] **Step 7: Update the capped string**

In `apps/web/src/locales/en/workspace.json`, replace line 417:

```json
  "newSectionNestCapped": "Sections can only be nested one level deep",
```

with:

```json
  "newSectionNestCapped": "Folders can only be nested 8 levels deep",
```

- [ ] **Step 8: Run the web suite and the build**

Run: `cd apps/web && npm test && npm run build`
Expected: PASS, build succeeds. If `RecordingsPanel.test.tsx` asserts the old capped behaviour, update those assertions to match - a sub-folder row now offers "New sub-section".

- [ ] **Step 9: Commit**

```bash
git add apps/web/src
git commit -m "feat: nav creates and nests folders to the depth cap"
```

---

### Task 11: Release chores for PR 2

**Files:**
- Modify: the five version files, `apps/web/src/lib/releases.ts`, `docs/Data_Schema.md`, `docs/features.md`, `README.md`, `integrations/n8n-nodes-diariz/nodes/Diariz/generated/openapi.snapshot.json`

**Interfaces:**
- Consumes: nothing. Produces: nothing.

- [ ] **Step 1: Bump to `0.179.0`**

Set `0.179.0` in `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `integrations/n8n-nodes-diariz/package.json`, and `<Version>` in `src/Diariz.Api/Diariz.Api.csproj`.

- [ ] **Step 2: Add the release entry**

Get the PR number as in Task 5 Step 2, then add to the top of `RELEASES`:

```ts
  {
    version: "0.179.0",
    date: "2026-08-04",
    pr: <number>,
    headline: "Folders nest as deep as you need",
    summary:
      "Folders used to stop at two levels, which was not enough once a library grew - you could have Customers and a customer name, but nowhere to put the project. Folders now nest up to 8 levels, so Customers > Acme Corp > Project Falcon works, and you can drag a folder and its whole contents to a new home anywhere in the tree.",
    added: [
      "Folders nest up to 8 levels deep, in the left nav and through the API.",
      "Moving a folder carries its sub-folders with it.",
    ],
    changed: [
      "A folder's roll-ups - summary, minutes, formula runs, chat, actions, notes and attachments - cover every level beneath it.",
    ],
  },
```

- [ ] **Step 3: Update `docs/Data_Schema.md`**

Replace line `616`:

```
| `ParentId` | uuid FK → Sections null | null = top-level; non-null = a sub-section (one level only). **Cascade** on parent delete |
```

with:

```
| `ParentId` | uuid FK → Sections null | null = top-level; non-null = a sub-section, nesting up to 8 levels. **Cascade** on parent delete |
```

And replace lines `620-622`:

```
Index: `(UserId, Name)`, `(ParentId)`. Sections nest **one level deep** (a sub-section can't be a parent;
enforced in `SectionsController`). Deleting a section **Cascade**-deletes its sub-sections and **SetNull**s
the recordings of itself and those sub-sections (ungroups, not deletes).
```

with:

```
Index: `(UserId, Name)`, `(ParentId)`. Sections nest up to **8 levels deep** (`SectionTree.MaxDepth`, enforced
in `SectionsController`: create checks the parent's depth, reparent checks the target's depth plus the moved
branch's height, and rejects a move into the folder's own descendant). Deleting a section **Cascade**-deletes
its whole subtree - Postgres cascades the self-referencing FK recursively - and **SetNull**s the recordings of
every folder in it (ungroups, not deletes). No migration was needed for the deeper tree: the self-referencing
`ParentId` already supported it, so older backups remain restorable.
```

- [ ] **Step 4: Update `docs/features.md`**

Replace the bullet on line `324`:

```
- **Organise** recordings into **sections and sub-sections** (one level of nesting) with drag-and-drop
```

with:

```
- **Organise** recordings into **folders nested up to 8 levels deep** (Customers > Acme Corp > Project Falcon)
  with drag-and-drop
```

- [ ] **Step 5: Update the README Features table**

In `README.md`, in the **Organise & merge** row (line `44`), replace `Sections and sub-sections with drag-and-drop;` with `Folders nested up to 8 levels deep with drag-and-drop;`.

- [ ] **Step 6: Update the About-box capabilities table**

In `apps/web/src/lib/releases.ts`, find the `CAPABILITIES` markdown table and update the folder/organise row to say folders nest up to 8 levels. Keep it to one concise line in the existing `| Feature | Description |` shape.

- [ ] **Step 7: Regenerate the n8n OpenAPI snapshot**

The three `EndpointDescription` blocks changed in Tasks 7-8 feed this file. Check `integrations/n8n-nodes-diariz/` for the generation script (look in its `package.json` scripts) and run it; if there is none, hand-edit `nodes/Diariz/generated/openapi.snapshot.json` so the three folder-endpoint descriptions match the new strings exactly.

- [ ] **Step 8: Full verification**

Run: `cd apps/web && npm test && npm run build`
Run: `cd ../.. && dotnet build Diariz.slnx && dotnet test tests/Diariz.Api.Tests`
Run: `dotnet test tests/Diariz.Api.IntegrationTests` (Docker required)
Expected: all PASS.

- [ ] **Step 9: Commit, push, open the PR**

```bash
git add -A
git commit -m "chore: release 0.179.0"
git push -u origin feat/deep-folders
gh pr create --title "Folders nest up to 8 levels" --body "$(cat <<'EOF'
Lifts the two-level folder cap to 8 levels, so `Customers > Acme Corp > Project Falcon` works.

The two-level cap was silently doing a second job: it made a `ParentId` cycle impossible. It is replaced by three rules in `SectionsController`, not deleted - create checks the parent's depth, and reparent checks both that the target is not the folder itself or a descendant (cycles) and that the target's depth plus the moved branch's height fits within 8. The last one matters: a move carries the whole branch, so a legal target can still be too deep for a tall branch.

On the web, `buildRecordingTree` is recursive (with a cycle guard) and the nav's two depth-2 assumptions are gone. The drill-in list, breadcrumb and count badge already walked `parentId` generically and needed no changes.

**No migration** - the self-referencing `ParentId` already supported this, so older backups remain restorable and `MaintenanceController.CurrentFormat` is unchanged.

**Deployment surface:** server redeploy only. No desktop release.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

---

# PHASE 3 - The breadcrumb (PR 3, version 0.180.0)

Wait for PR 2 to merge, then:

```bash
git checkout main
git pull
git checkout -b feat/folder-breadcrumb
```

---

### Task 12: The pure `collapsePath`

**Files:**
- Create: `apps/web/src/lib/folderPath.ts`
- Create: `apps/web/src/lib/folderPath.test.ts`

**Interfaces:**
- Consumes: nothing.
- Produces: `PathCrumb` (`{ id: string; name: string }`), `PathSegment` (`PathCrumb | "ellipsis"`), and `collapsePath(crumbs: PathCrumb[], maxVisible: number) -> PathSegment[]`. The **last** crumb always survives; the ellipsis is not counted against `maxVisible`.

- [ ] **Step 1: Write the failing test**

Create `apps/web/src/lib/folderPath.test.ts`:

```ts
import { describe, it, expect } from "vitest";
import { collapsePath, type PathCrumb } from "./folderPath";

const crumb = (id: string): PathCrumb => ({ id, name: id.toUpperCase() });
const path = (n: number) => Array.from({ length: n }, (_, i) => crumb(`c${i}`));

describe("collapsePath", () => {
  it("leaves a path that fits untouched, with no ellipsis", () => {
    const p = path(3);
    expect(collapsePath(p, 3)).toEqual(p);
  });

  it("leaves a path shorter than the budget untouched", () => {
    const p = path(2);
    expect(collapsePath(p, 4)).toEqual(p);
  });

  it("keeps the first crumb and the tail, collapsing the middle", () => {
    const p = path(5); // c0 c1 c2 c3 c4
    expect(collapsePath(p, 3)).toEqual([p[0], "ellipsis", p[3], p[4]]);
  });

  it("always keeps the current folder, however tight the budget", () => {
    const p = path(5);
    expect(collapsePath(p, 1)).toEqual([p[4]]);
  });

  it("returns an empty path unchanged", () => {
    expect(collapsePath([], 3)).toEqual([]);
  });

  it("treats a zero or negative budget as one crumb", () => {
    const p = path(4);
    expect(collapsePath(p, 0)).toEqual([p[3]]);
  });
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd apps/web && npx vitest run src/lib/folderPath.test.ts`
Expected: FAIL - `./folderPath` does not exist.

- [ ] **Step 3: Write the implementation**

Create `apps/web/src/lib/folderPath.ts`:

```ts
/// Which crumbs survive when a folder path is too long for the space it has. Pure, and separate from the
/// component, because the collapse rule is the piece most likely to need tuning against real folder names -
/// and tuning it should not mean re-testing a React tree.

export interface PathCrumb {
  id: string;
  name: string;
}

/// A rendered path is crumbs with at most one gap marker. The marker is not a crumb: it carries no id,
/// because the menu shows the full chain anyway and a second way to reach it would not earn its pixels.
export type PathSegment = PathCrumb | "ellipsis";

/// Collapse `crumbs` (root first, current last) to at most `maxVisible` crumbs by dropping from the middle.
/// The first crumb anchors the path and the **last** is the folder you are actually in, so the tail is what
/// survives a tight budget: at `maxVisible` 1 you get the current folder alone, never the root alone.
/// The ellipsis does not count against the budget.
export function collapsePath(crumbs: PathCrumb[], maxVisible: number): PathSegment[] {
  if (crumbs.length === 0) return [];

  const budget = Math.max(1, maxVisible);
  if (crumbs.length <= budget) return [...crumbs];
  if (budget === 1) return [crumbs[crumbs.length - 1]];

  // One slot for the root, the rest for the tail.
  const tail = crumbs.slice(crumbs.length - (budget - 1));
  return [crumbs[0], "ellipsis", ...tail];
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `cd apps/web && npx vitest run src/lib/folderPath.test.ts`
Expected: PASS, 6 tests.

- [ ] **Step 5: Commit**

```bash
git add apps/web/src/lib/folderPath.ts apps/web/src/lib/folderPath.test.ts
git commit -m "feat: add collapsePath, the folder breadcrumb collapse rule"
```

---

### Task 13: The `FolderPath` component

**Files:**
- Create: `apps/web/src/components/nav/FolderPath.tsx`
- Create: `apps/web/src/components/nav/FolderPath.test.tsx`
- Modify: `apps/web/src/locales/en/workspace.json`

**Interfaces:**
- Consumes: `collapsePath`, `PathCrumb`, `PathSegment` from Task 12; `ChevronDownIcon`, `ChevronRightIcon` from `../icons`.
- Produces: default export `FolderPath`, props `{ crumbs: PathCrumb[]; maxVisible?: number; onSelect?: (id: string) => void; extraItems?: { label: string; onClick: () => void }[]; onCrumbDrop?: (crumbId: string, recordingId: string) => void }`. `maxVisible` defaults to 3.

- [ ] **Step 1: Add the i18n strings**

In `apps/web/src/locales/en/workspace.json`, after line `412` (`"drillEmpty"`), add:

```json
  "folderPathMenu": "Show full folder path",
  "folderPathCollapsed": "Hidden folders",
```

- [ ] **Step 2: Write the failing test**

Create `apps/web/src/components/nav/FolderPath.test.tsx`:

```tsx
import { describe, it, expect, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import FolderPath from "./FolderPath";
import type { PathCrumb } from "../../lib/folderPath";

const crumbs: PathCrumb[] = [
  { id: "customers", name: "Customers" },
  { id: "acme", name: "Acme Corp" },
  { id: "falcon", name: "Project Falcon" },
  { id: "phase2", name: "Phase 2" },
];

describe("FolderPath", () => {
  it("renders a short path in full", async () => {
    render(<FolderPath crumbs={crumbs.slice(0, 2)} maxVisible={3} />);
    expect(screen.getByText("Customers")).toBeTruthy();
    expect(screen.getByText("Acme Corp")).toBeTruthy();
  });

  it("collapses the middle of a long path but keeps the current folder", () => {
    render(<FolderPath crumbs={crumbs} maxVisible={2} />);
    expect(screen.getByText("Customers")).toBeTruthy();
    expect(screen.getByText("Phase 2")).toBeTruthy();
    expect(screen.queryByText("Acme Corp")).toBeNull();
  });

  it("calls onSelect with the crumb id when a crumb is clicked", async () => {
    const onSelect = vi.fn();
    render(<FolderPath crumbs={crumbs} maxVisible={4} onSelect={onSelect} />);

    await userEvent.click(screen.getByText("Acme Corp"));

    expect(onSelect).toHaveBeenCalledWith("acme");
  });

  it("lists every ancestor in the menu, including ones collapsed out of the path", async () => {
    const onSelect = vi.fn();
    render(<FolderPath crumbs={crumbs} maxVisible={2} onSelect={onSelect} />);

    await userEvent.click(screen.getByLabelText("Show full folder path"));

    // Acme Corp is hidden from the path but must be reachable from the menu.
    await userEvent.click(screen.getByRole("menuitem", { name: "Acme Corp" }));
    expect(onSelect).toHaveBeenCalledWith("acme");
  });

  it("puts extra items at the top of the menu", async () => {
    const onClick = vi.fn();
    render(<FolderPath crumbs={crumbs} extraItems={[{ label: "Open section page", onClick }]} />);

    await userEvent.click(screen.getByLabelText("Show full folder path"));
    await userEvent.click(screen.getByRole("menuitem", { name: "Open section page" }));

    expect(onClick).toHaveBeenCalled();
  });
});
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `cd apps/web && npx vitest run src/components/nav/FolderPath.test.tsx`
Expected: FAIL - `./FolderPath` does not exist.

- [ ] **Step 4: Write the implementation**

Create `apps/web/src/components/nav/FolderPath.tsx`:

```tsx
import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { collapsePath, type PathCrumb } from "../../lib/folderPath";
import { ChevronDownIcon, ChevronRightIcon } from "../icons";

/// A folder path, collapsed to fit, with a menu listing the full ancestor chain.
///
/// Presentational on purpose: it takes crumbs and callbacks and knows nothing about drilling, routing or
/// rooms. Three places need a folder path - the nav's drill breadcrumb, a folder's own page, and search
/// results - and they disagree about what clicking one means, so that decision stays with the caller.
///
/// The **trailing chevron** is the menu trigger and is always present, so the full hierarchy is one click
/// away whether or not the path is collapsed. The collapsed `…` is a plain indicator, not a second trigger:
/// two controls opening the same menu is not affordable in a strip this narrow.
export default function FolderPath({
  crumbs,
  maxVisible = 3,
  onSelect,
  extraItems = [],
  onCrumbDrop,
}: {
  /// Root first, current folder last.
  crumbs: PathCrumb[];
  maxVisible?: number;
  /// Clicking a crumb or a menu entry. Omit to render the path as static text.
  onSelect?: (id: string) => void;
  /// Menu entries shown above the ancestor chain (the nav puts "Open section page" here).
  extraItems?: { label: string; onClick: () => void }[];
  /// A recording dropped onto a crumb - the cheap way to move something up a level.
  onCrumbDrop?: (crumbId: string, recordingId: string) => void;
}) {
  const { t } = useTranslation("workspace");
  const [open, setOpen] = useState(false);
  const wrapRef = useRef<HTMLDivElement>(null);

  // Close on an outside click or Escape, like the other menus in the panel.
  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (wrapRef.current && !wrapRef.current.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && setOpen(false);
    document.addEventListener("mousedown", onDown);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onDown);
      document.removeEventListener("keydown", onKey);
    };
  }, [open]);

  if (crumbs.length === 0) return null;

  const segments = collapsePath(crumbs, maxVisible);

  return (
    <div ref={wrapRef} className="relative flex min-w-0 flex-1 items-center gap-0.5">
      <nav className="flex min-w-0 flex-1 items-center gap-0.5 overflow-hidden" aria-label={t("folderPathMenu")}>
        {segments.map((seg, i) => (
          <span key={seg === "ellipsis" ? `gap-${i}` : seg.id} className="flex min-w-0 items-center gap-0.5">
            {i > 0 && (
              <span className="shrink-0 text-gray-300 dark:text-gray-600" aria-hidden>
                <ChevronRightIcon size={11} />
              </span>
            )}
            {seg === "ellipsis" ? (
              <span className="shrink-0 text-[11px] text-gray-400 dark:text-gray-500" title={t("folderPathCollapsed")}>
                &hellip;
              </span>
            ) : onSelect ? (
              <button
                type="button"
                onClick={() => onSelect(seg.id)}
                onDragOver={onCrumbDrop ? (e) => e.preventDefault() : undefined}
                onDrop={
                  onCrumbDrop
                    ? (e) => {
                        const dragged = e.dataTransfer.getData("text/plain");
                        if (!dragged) return;
                        e.stopPropagation();
                        onCrumbDrop(seg.id, dragged);
                      }
                    : undefined
                }
                className="min-w-0 truncate text-[11.5px] text-gray-600 hover:underline dark:text-gray-300"
              >
                {seg.name}
              </button>
            ) : (
              <span className="min-w-0 truncate text-[11.5px] text-gray-600 dark:text-gray-300">{seg.name}</span>
            )}
          </span>
        ))}
      </nav>

      <button
        type="button"
        aria-label={t("folderPathMenu")}
        aria-expanded={open}
        onClick={() => setOpen((v) => !v)}
        className="shrink-0 rounded p-0.5 text-gray-400 hover:bg-gray-100 dark:text-gray-500 dark:hover:bg-gray-800"
      >
        <ChevronDownIcon size={12} />
      </button>

      {open && (
        <div
          role="menu"
          className="absolute right-0 top-full z-20 mt-1 min-w-[12rem] rounded border bg-white py-1 shadow-lg dark:border-gray-700 dark:bg-gray-900"
        >
          {extraItems.map((item) => (
            <button
              key={item.label}
              type="button"
              role="menuitem"
              onClick={() => {
                setOpen(false);
                item.onClick();
              }}
              className="block w-full px-3 py-1 text-left text-xs text-blue-600 hover:bg-gray-50 dark:text-blue-400 dark:hover:bg-gray-800"
            >
              {item.label}
            </button>
          ))}
          {extraItems.length > 0 && <div className="my-1 border-t dark:border-gray-700" />}
          {/* The FULL chain, not the collapsed one - the menu is how a hidden ancestor stays reachable.
              Indented by depth so the shape of the path is legible without repeating parent names. */}
          {crumbs.map((c, depth) => (
            <button
              key={c.id}
              type="button"
              role="menuitem"
              onClick={() => {
                setOpen(false);
                onSelect?.(c.id);
              }}
              style={{ paddingLeft: `${0.75 + depth * 0.6}rem` }}
              className="block w-full truncate py-1 pr-3 text-left text-xs text-gray-700 hover:bg-gray-50 dark:text-gray-200 dark:hover:bg-gray-800"
            >
              {c.name}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `cd apps/web && npx vitest run src/components/nav/FolderPath.test.tsx`
Expected: PASS, 5 tests.

- [ ] **Step 6: Commit**

```bash
git add apps/web/src/components/nav/FolderPath.tsx apps/web/src/components/nav/FolderPath.test.tsx apps/web/src/locales/en/workspace.json
git commit -m "feat: add the FolderPath breadcrumb component"
```

---

### Task 14: Wire `FolderPath` into the nav breadcrumb

**Files:**
- Modify: `apps/web/src/components/nav/DrillBreadcrumb.tsx`
- Modify: `apps/web/src/components/nav/DrillBreadcrumb.test.tsx`
- Modify: `apps/web/src/components/RecordingsPanel.tsx:379-384`

**Interfaces:**
- Consumes: `FolderPath` from Task 13, `breadcrumbOf` from `drillView`.
- Produces: `DrillBreadcrumb` gains one optional prop, `onRecordingDrop?: (sectionId: string, recordingId: string) => void`. Existing props are unchanged.

- [ ] **Step 1: Write the failing test**

Open `apps/web/src/components/nav/DrillBreadcrumb.test.tsx` and add these cases (keep the existing ones; adjust any that assert the old parent-over-current layout):

```tsx
  it("shows the whole ancestor path, not just the parent", () => {
    const sections = [
      { id: "customers", name: "Customers", parentId: null, position: 0 },
      { id: "acme", name: "Acme Corp", parentId: "customers", position: 0 },
      { id: "falcon", name: "Project Falcon", parentId: "acme", position: 0 },
    ] as SectionDto[];

    render(
      <MemoryRouter>
        <DrillBreadcrumb sections={sections} sectionId="falcon" basePath="" onDrill={() => {}} />
      </MemoryRouter>,
    );

    expect(screen.getByText("Customers")).toBeTruthy();
    expect(screen.getByText("Project Falcon")).toBeTruthy();
  });

  it("drills to an ancestor when its crumb is clicked", async () => {
    const onDrill = vi.fn();
    const sections = [
      { id: "customers", name: "Customers", parentId: null, position: 0 },
      { id: "acme", name: "Acme Corp", parentId: "customers", position: 0 },
    ] as SectionDto[];

    render(
      <MemoryRouter>
        <DrillBreadcrumb sections={sections} sectionId="acme" basePath="" onDrill={onDrill} />
      </MemoryRouter>,
    );

    await userEvent.click(screen.getByText("Customers"));

    expect(onDrill).toHaveBeenCalledWith("customers");
  });
```

Make sure the file imports `userEvent` from `@testing-library/user-event` and `SectionDto` from `../../lib/types`.

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd apps/web && npx vitest run src/components/nav/DrillBreadcrumb.test.tsx`
Expected: FAIL - only the parent and current names render today, and clicking "Customers" does nothing.

- [ ] **Step 3: Write the implementation**

Replace the body of `apps/web/src/components/nav/DrillBreadcrumb.tsx` from line `17` to the end with:

```tsx
export default function DrillBreadcrumb({
  sections,
  sectionId,
  basePath,
  onDrill,
  onRecordingDrop,
}: {
  sections: SectionDto[];
  sectionId: string | null;
  basePath: string;
  onDrill: (sectionId: string | null) => void;
  /// A recording dragged onto an ancestor crumb - moves it up without engaging a modal.
  onRecordingDrop?: (sectionId: string, recordingId: string) => void;
}) {
  const { t } = useTranslation("workspace");
  const navigate = useNavigate();
  const drillSearch = useDrillSearch();
  if (sectionId === null) return null;

  const chain = breadcrumbOf(sections, sectionId);
  const current = chain[chain.length - 1];
  // An unknown id (the folder was deleted while we were inside it) still renders the row, so the back
  // button remains a way out rather than stranding the user in an empty list.
  const parent = chain.length > 1 ? chain[chain.length - 2] : null;
  const color = sectionColor(sectionId);

  return (
    <div className="flex items-center gap-1.5 border-b px-2 py-2 dark:border-gray-800">
      <button
        type="button"
        aria-label={t("drillBack")}
        onClick={() => onDrill(parent?.id ?? null)}
        className="shrink-0 rounded border p-1 text-gray-500 hover:bg-gray-100 dark:border-gray-700 dark:text-gray-400 dark:hover:bg-gray-800"
      >
        <ArrowLeftIcon size={14} />
      </button>

      <span
        style={{ "--sc-light": color.light, "--sc-dark": color.dark } as React.CSSProperties}
        className="shrink-0 text-[var(--sc-light)] dark:text-[var(--sc-dark)]"
      >
        <FolderIcon size={14} />
      </span>

      {/* Clicking a crumb DRILLS to that level; "Open section page" (in the menu) navigates the middle
          panel. Those stay distinct targets - collapsing them would make a folder's page unreachable once
          you had drilled into it. The page link carries `?in=` so opening it does not pop the list home. */}
      <FolderPath
        crumbs={chain.map((s) => ({ id: s.id, name: s.name }))}
        maxVisible={2}
        onSelect={(id) => onDrill(id)}
        onCrumbDrop={onRecordingDrop}
        extraItems={
          current
            ? [
                {
                  label: t("drillOpenSectionPage"),
                  onClick: () =>
                    navigate({ pathname: `${basePath}/sections/${current.id}`, search: drillSearch }),
                },
              ]
            : []
        }
      />
    </div>
  );
}
```

Replace the imports at the top of the file (lines `1-7`) with:

```tsx
import { useNavigate } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { breadcrumbOf } from "../../lib/drillView";
import { useDrillSearch } from "../../lib/drillRoute";
import { sectionColor } from "../../lib/sectionColors";
import { ArrowLeftIcon, FolderIcon } from "../icons";
import FolderPath from "./FolderPath";
import type { SectionDto } from "../../lib/types";
```

Update the component's doc comment (lines `9-16`) to:

```tsx
/// The drill-in list's header row: a back button, the folder path, and a menu carrying the full ancestor
/// chain plus a link to the folder's own page.
///
/// Clicking a crumb and "Open section page" are deliberately **distinct targets** and must stay that way: a
/// crumb browses to that level (`onDrill`), while "Open section page" navigates the middle panel to the
/// folder itself. Collapsing them would make it impossible to reach a folder's page once you had drilled in.
///
/// Renders nothing at the room's top level - there is nowhere to go back to, and no page to open.
```

- [ ] **Step 4: Pass the drop handler from the panel**

In `apps/web/src/components/RecordingsPanel.tsx`, replace the `<DrillBreadcrumb ... />` block (lines `379-384`) with:

```tsx
            <DrillBreadcrumb
              sections={sections}
              sectionId={drill.sectionId}
              basePath={basePath}
              onDrill={drill.drillTo}
              onRecordingDrop={(sectionId, recordingId) => drop(sectionId, [], recordingId, null)}
            />
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `cd apps/web && npx vitest run src/components/nav && npm test`
Expected: PASS. If `RecordingsPanel.test.tsx` asserted the old two-line breadcrumb, update those assertions.

- [ ] **Step 6: Commit**

```bash
git add apps/web/src/components/nav apps/web/src/components/RecordingsPanel.tsx
git commit -m "feat: nav breadcrumb shows the full folder path at any depth"
```

---

### Task 15: A breadcrumb on the folder page

Open a deep folder's page directly and there is currently nothing telling you where it sits.

**Files:**
- Modify: `apps/web/src/pages/SectionDetail.tsx`

**Interfaces:**
- Consumes: `FolderPath` from Task 13, `breadcrumbOf` from `drillView`, `api.listSections`.
- Produces: nothing consumed later.

- [ ] **Step 1: Write the failing test**

Add a new `describe` block to `apps/web/src/pages/SectionDetail.test.tsx`. It reuses the file's existing `renderPage(section)` helper (which mocks `api.getSection` and mounts the page at `/sections/sec-1`, so the folder under test has id `sec-1`), `loaded()`, and the already-mocked `api.listSections`:

```tsx
describe("SectionDetail breadcrumb", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
  });

  it("shows the folder's ancestor path", async () => {
    (api.listSections as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "customers", name: "Customers", parentId: null, position: 0 },
      { id: "acme", name: "Acme Corp", parentId: "customers", position: 0 },
      { id: "sec-1", name: "Project Falcon", parentId: "acme", position: 0 },
    ]);

    // roomId must be set - the ancestors query is `enabled` on it.
    renderPage({ ...base, roomId: "room-1" });
    await loaded();

    expect(await screen.findByText("Customers")).toBeTruthy();
    expect(screen.getByText("Acme Corp")).toBeTruthy();
  });

  it("shows no path for a top-level folder", async () => {
    (api.listSections as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "sec-1", name: "Customers", parentId: null, position: 0 },
    ]);

    renderPage({ ...base, roomId: "room-1" });
    await loaded();

    // A path consisting only of the folder itself says nothing, so nothing is rendered.
    expect(screen.queryByLabelText("Show full folder path")).toBeNull();
  });
});
```

If `base` is not exported/visible at that point in the file, move the new `describe` below its definition. Confirm the folder-detail fixture's own `name` does not collide with an ancestor name you assert on.

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd apps/web && npx vitest run src/pages/SectionDetail.test.tsx`
Expected: FAIL - no ancestor path is rendered.

- [ ] **Step 3: Write the implementation**

Add these imports to `apps/web/src/pages/SectionDetail.tsx`:

```tsx
import { useNavigate } from "react-router-dom";
import { breadcrumbOf } from "../lib/drillView";
import { useRoomBasePath } from "../lib/rooms";
import FolderPath from "../components/nav/FolderPath";
```

Inside the component, alongside the existing queries, add:

```tsx
  const navigate = useNavigate();
  const basePath = useRoomBasePath();
  // The folder's own room, so a shared-room folder resolves its own ancestors rather than the caller's
  // personal ones (the same trap FolderRecordingList hit in issue #295).
  const { data: siblingSections = [] } = useQuery({
    queryKey: ["sections", section?.roomId ?? null],
    queryFn: () => api.listSections(section?.roomId),
    enabled: !!section?.roomId,
  });
  const ancestors = id ? breadcrumbOf(siblingSections, id) : [];
```

Use whatever the existing variable for the fetched detail is called - if it is not `section`, adapt `section?.roomId`. Then render the path immediately above the folder title heading:

```tsx
      {ancestors.length > 1 && (
        <div className="mb-1 flex items-center">
          <FolderPath
            crumbs={ancestors.map((s) => ({ id: s.id, name: s.name }))}
            maxVisible={4}
            onSelect={(sectionId) => navigate(`${basePath}/sections/${sectionId}`)}
          />
        </div>
      )}
```

`ancestors.length > 1` keeps a top-level folder's page unchanged - a path of just itself says nothing.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd apps/web && npx vitest run src/pages/SectionDetail.test.tsx`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add apps/web/src/pages/SectionDetail.tsx apps/web/src/pages/SectionDetail.test.tsx
git commit -m "feat: show a folder's ancestor path on its page"
```

---

### Task 16: Folder search results show where the folder is

Search's folder hits render the folder **name alone**. At two levels that was tolerable; with deeper trees a search for "Phase 2" can return several identical-looking rows in different customers. The API already returns the ancestor names (`FolderHit.breadcrumb`) - the UI simply never rendered them.

**Files:**
- Modify: `apps/web/src/components/nav/SearchBar.tsx:172-193`
- Modify: `apps/web/src/components/nav/SearchBar.test.tsx`

**Interfaces:**
- Consumes: `FolderPath` from Task 13. `FolderHit.breadcrumb` is `string[]` - ancestor **names** only, root-first, excluding the folder's own name - so it is mapped to `PathCrumb`s with index-based ids. Those ids are React keys only: no `onSelect` is passed, so the path renders as static text, which is what a result row wants.
- Produces: nothing consumed later.

- [ ] **Step 1: Write the failing test**

Add to `apps/web/src/components/nav/SearchBar.test.tsx`, following the existing mock setup in that file (the mocked `api.search` returns `{ folders, hits, ... }`):

```tsx
  it("shows a folder hit's ancestor path so identically-named folders are distinguishable", async () => {
    mockSearch({
      folders: [
        { id: "p1", name: "Phase 2", parentId: "falcon", roomId: "r", roomName: "Personal",
          breadcrumb: ["Customers", "Acme Corp", "Project Falcon"], recordingCount: 3 },
      ],
      hits: [],
    });

    renderSearchBar();
    await userEvent.type(screen.getByRole("searchbox"), "Phase 2");

    expect(await screen.findByText("Phase 2")).toBeTruthy();
    expect(screen.getByText("Customers")).toBeTruthy();     // the path is rendered
    expect(screen.getByText("Project Falcon")).toBeTruthy(); // the nearest ancestor survives collapsing
  });
```

Adapt `mockSearch` / `renderSearchBar` to whatever the existing helpers in that file are called, and use the existing query-input accessor rather than `getByRole("searchbox")` if it differs.

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd apps/web && npx vitest run src/components/nav/SearchBar.test.tsx`
Expected: FAIL - "Customers" is not in the document; only the folder's own name renders.

- [ ] **Step 3: Write the implementation**

In `apps/web/src/components/nav/SearchBar.tsx`, replace the folder-hit button's inner content (lines `186-191`) so the name and path stack:

```tsx
              <FolderIcon size={14} />
              <span className="flex min-w-0 flex-1 flex-col">
                <span className="min-w-0 truncate text-[12.5px] font-semibold">{f.name}</span>
                {/* Where it lives. Without this, deep trees produce several identical-looking rows.
                    Static text - no onSelect - because the row itself is already the click target. */}
                {f.breadcrumb.length > 0 && (
                  <FolderPath
                    crumbs={f.breadcrumb.map((name, i) => ({ id: `${f.id}-${i}`, name }))}
                    maxVisible={2}
                  />
                )}
              </span>
              <span className="shrink-0 text-[11px] tabular-nums text-gray-400">{f.recordingCount}</span>
              <span className="shrink-0 text-gray-400">
                <ChevronRightIcon size={14} />
              </span>
```

Add the import: `import FolderPath from "./FolderPath";`

`FolderPath` renders its own trailing menu button. Inside this row that button sits within the outer `<button>`, which is invalid HTML (nested interactive elements) - so pass no `onSelect` **and** confirm the rendered menu trigger is acceptable here. If nesting proves a problem in the DOM or in tests, render the names directly instead:

```tsx
                {f.breadcrumb.length > 0 && (
                  <span className="min-w-0 truncate text-[10px] text-gray-400 dark:text-gray-500">
                    {collapsePath(
                      f.breadcrumb.map((name, i) => ({ id: `${f.id}-${i}`, name })),
                      2,
                    )
                      .map((s) => (s === "ellipsis" ? "..." : s.name))
                      .join(" / ")}
                  </span>
                )}
```

with `import { collapsePath } from "../../lib/folderPath";`. **Prefer this second form** - it reuses the same collapse rule without nesting a button inside a button, and a search row needs no menu.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd apps/web && npx vitest run src/components/nav/SearchBar.test.tsx`
Expected: PASS. If you used the second (text) form, adjust the test to assert on the joined string, e.g. `expect(screen.getByText(/Customers/)).toBeTruthy()`.

- [ ] **Step 5: Commit**

```bash
git add apps/web/src/components/nav/SearchBar.tsx apps/web/src/components/nav/SearchBar.test.tsx
git commit -m "feat: folder search results show the folder's ancestor path"
```

---

### Task 17: Release chores for PR 3

**Files:**
- Modify: the five version files, `apps/web/src/lib/releases.ts`, `docs/features.md`, `README.md`, `apps/web/src/content/help/en/` (the folder-organisation article)

**Interfaces:**
- Consumes: nothing. Produces: nothing.

- [ ] **Step 1: Bump to `0.180.0`**

Set `0.180.0` in all five files.

- [ ] **Step 2: Add the release entry**

Get the PR number as in Task 5 Step 2, then add to the top of `RELEASES`:

```ts
  {
    version: "0.180.0",
    date: "2026-08-04",
    pr: <number>,
    headline: "See where you are in a deep folder tree",
    summary:
      "With folders now nesting several levels deep, the list header shows the whole path rather than just the folder above you. Click any part of it to jump straight to that level, or open the menu at the end for the full hierarchy. Folder pages carry the same path, so opening one from a link no longer leaves you guessing where it sits. You can also drag a recording onto any part of the path to move it up.",
    added: [
      "The folder list header shows the full path, collapsing the middle when it does not fit.",
      "Click any folder in the path to jump to that level; the menu at the end lists the whole hierarchy.",
      "Drag a recording onto a folder in the path to move it there.",
      "Folder pages now show their ancestor path.",
      "Folder search results show where each folder lives, so two folders with the same name are told apart.",
    ],
  },
```

- [ ] **Step 3: Update the docs**

In `docs/features.md`, extend the organise bullet edited in Phase 2 to mention the path header. In `README.md`, the **Organise & merge** row already says the list "drills in one folder at a time with a breadcrumb back out" - update it to say the breadcrumb shows the full path and each part is clickable. Keep both to one concise line.

- [ ] **Step 4: Update the help article**

Find the help article covering organising recordings into folders (`ls apps/web/src/content/help/en/`) and update the part describing how you move between folders - the behaviour a user relies on has changed. Content must be **ASCII only** and keep its `title` / `summary` / `group` / `order` front matter. `helpContent.test.ts` enforces this.

- [ ] **Step 5: Full verification**

Run: `cd apps/web && npm test && npm run build`
Run: `cd ../.. && dotnet build Diariz.slnx && dotnet test tests/Diariz.Api.Tests`
Expected: all PASS, build clean.

- [ ] **Step 6: Commit, push, open the PR**

```bash
git add -A
git commit -m "chore: release 0.180.0"
git push -u origin feat/folder-breadcrumb
gh pr create --title "Folder breadcrumb shows the full path" --body "$(cat <<'EOF'
Replaces the nav's parent-over-current breadcrumb with a full folder path that collapses its middle when it does not fit, and gives folder pages the same path.

The collapse rule is a pure function (`collapsePath`) with its own tests, because it is the piece most likely to need tuning against real folder names. The trailing chevron opens a menu listing the **full** ancestor chain, which is how a collapsed-out folder stays reachable; the `…` is a plain indicator, not a second trigger.

Clicking a crumb drills to that level and "Open section page" navigates the middle panel - kept as distinct targets, or a folder's page becomes unreachable once you have drilled into it. Crumbs are also drop targets, so dragging a recording onto an ancestor moves it up.

**Deployment surface:** server redeploy only. No desktop release, no migration.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

---

## What this plan does NOT cover

Phases 4 and 5 of the spec are deliberately out of scope and need their own plans once this is in use:

- **Phase 4:** the cut/paste clipboard for recordings and folders, and the bulk move endpoint (`POST /api/recordings/section`). Until it lands, moving something across branches is still only possible one level at a time by dragging - the deep tree is buildable but awkward to reorganise.
- **Phase 5:** rebuilding `MoveToSectionModal` and `RecordingsSection`'s flat `Parent > Child` pickers as
  **drill-down** pickers, so a deep tree is pleasant to navigate rather than a long list of long path strings.

  > **Corrected during execution (2026-08-05).** This bullet originally also deferred *recursing*
  > `orderedSections` and `FolderRecordingList`, on the claim that both "degrade gracefully rather than
  > breaking". That claim was false and Phase 2's whole-branch review caught it. `orderedSections` silently
  > omitted every folder below depth 2, so the pickers could not select the folders the same release let you
  > create; and `FolderRecordingList` would render its "no recordings" empty state directly beneath an AI
  > summary of recordings filed deeper down. Both were fixed in Phase 2. What remains here is only the
  > drill-down redesign, which genuinely is a comfort improvement rather than a correctness one.
