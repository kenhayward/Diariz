# Pinned Action Items Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the cross-meeting Actions views opt-in - an action reaches them only once someone pins it, while every extracted action stays visible on its own recording's page exactly as today.

**Architecture:** One additive boolean column on `RecordingActions`. Both aggregated list endpoints gain an optional `pinned` query filter whose default is unchanged, so nothing already calling them breaks; the web client passes `pinned=true`. A new bulk `POST /api/actions/pin` mirrors the existing `POST /api/actions/complete` exactly, including its owner-only gate. Three React components gain a per-row pin toggle.

**Tech Stack:** ASP.NET Core 10 + EF Core (Npgsql), xUnit + Testcontainers, React 19 + TypeScript + Vite, vitest + @testing-library/react, i18next.

**Spec:** [docs/superpowers/specs/2026-08-28-pinned-action-items-design.md](../specs/2026-08-28-pinned-action-items-design.md)

## Global Constraints

- **TDD is mandatory.** No production code without a failing test that preceded it. Every guard below must be **mutation-verified**: after it passes, introduce the exact regression it exists to catch, watch it fail, revert. A guard never seen to fail is not a guard.
- **Never `git add -A`.** Stage explicit paths only. This repo sweeps hundreds of agent scratch files otherwise.
- **No em or en dashes in user-facing text.** Plain hyphen `-` only, in UI strings, locale catalogues, and release notes. `apps/web/src/lib/noFancyDashes.test.ts` enforces this. Code, comments and internal docs are exempt.
- **Never put production data anywhere in the repo.** Invent fixture names (`Ada`, `Grace`, `Alice`, `Bob`, `Sam`).
- **`main` is branch protected.** Work lands via a PR. Never commit or push to `main`, never merge locally.
- **Branch:** `claude/pinned-action-items`, already created off `origin/main` at `d3e80b2f`. The spec is already committed to it as `013ccee8`.
- **Target version:** `0.260.0` -> **`0.261.0`** (functional enhancement, minor bump). Applied once, in Task 11.
- **Do not fix merge dropping `Completed`/`CompletedAt`.** That is [issue #676](https://github.com/kenhayward/Diariz/issues/676), a separate pre-existing bug. Task 5 carries `Pinned` across merge and touches nothing else in that block.
- **Web tests do not run through `tsc`** (`tsconfig.json` excludes them), so a wrong fixture type will not fail the build. Fixtures must still be correct by hand.
- **`apps/web/src/components/ActionsTab.test.tsx` stubs i18next as `t: (k) => k`,** so assertions in that file match translation **keys**, not English text.

**Commands:**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~ActionsController"
```

```bash
cd apps/web && npx vitest run src/components/ActionsTable.test.tsx
```

Note: `dotnet test --filter "Name=X"` does not work in this repo despite what CLAUDE.md says. Always use `FullyQualifiedName~X`.

---

### Task 1: The column, the migration, and the `pinned` filter on `GET /api/actions`

**Files:**
- Modify: `src/Diariz.Domain/Entities/RecordingAction.cs`
- Create: `src/Diariz.Domain/Migrations/<timestamp>_AddActionPinned.cs` (+ `.Designer.cs`, generated)
- Modify: `src/Diariz.Domain/Migrations/DiarizDbContextModelSnapshot.cs` (generated)
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs:484-486`
- Modify: `src/Diariz.Api/Controllers/ActionsController.cs:44-66`
- Test: `tests/Diariz.Api.Tests/ActionsControllerTests.cs`

**Interfaces:**
- Produces: `RecordingAction.Pinned` (`bool`), `ActionListItemDto` gains a trailing required `bool Pinned`, and `ActionsController.List([FromQuery] Guid? roomId = null, [FromQuery] bool? pinned = null)`.

- [ ] **Step 1: Write the two failing tests**

Add to `tests/Diariz.Api.Tests/ActionsControllerTests.cs`, after the existing `List_ReturnsAllOwnedActions_WithRecordingNames_ExcludesOtherUsers`:

```csharp
    [Fact]
    public async Task List_WithPinnedTrue_ReturnsOnlyPinnedActions()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var rec = AddRecording(db, me, "Standup");
        db.RecordingActions.AddRange(
            new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "Book the room", Ordinal = 0, Pinned = true },
            new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "Chase the invoice", Ordinal = 1 });
        await db.SaveChangesAsync();

        var dtos = (await Build(db, me).List(pinned: true)).Value!;

        Assert.Single(dtos);
        Assert.Equal("Book the room", dtos[0].Text);
        Assert.True(dtos[0].Pinned);
    }

    [Fact]
    public async Task List_WithNoPinnedParameter_StillReturnsEveryAction()
    {
        // The published default, deliberately unchanged. GET /api/actions is in the OpenAPI document and is
        // what the n8n community node calls as "List action items across meetings". Narrowing it to
        // pinned-only would break live workflows silently, and an npm-published node cannot be corrected
        // after the fact. This test is the guard on that decision.
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var rec = AddRecording(db, me, "Standup");
        db.RecordingActions.AddRange(
            new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "Book the room", Ordinal = 0, Pinned = true },
            new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "Chase the invoice", Ordinal = 1 });
        await db.SaveChangesAsync();

        var dtos = (await Build(db, me).List()).Value!;

        Assert.Equal(2, dtos.Count);
        Assert.Contains(dtos, d => d.Text == "Chase the invoice" && !d.Pinned);
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~ActionsControllerTests"`

Expected: **compile error** - `RecordingAction` has no property `Pinned`, `ActionListItemDto` has no `Pinned`, and `List` has no `pinned` parameter. A compile failure is a valid red here; the test cannot express the behaviour without the API existing.

- [ ] **Step 3: Add the entity property**

In `src/Diariz.Domain/Entities/RecordingAction.cs`, after the `CompletedAt` property:

```csharp
    /// <summary>Whether the user has pinned this action into the cross-meeting Actions views. Defaults to
    /// false: an action is visible on its own recording's page from the moment it is extracted, and reaches
    /// the Actions tab and the folder Actions tab only once someone pins it. Reversible, and owner-only to
    /// set (see <c>ActionsController.Pin</c>), exactly like <see cref="Completed"/>.</summary>
    public bool Pinned { get; set; }
```

- [ ] **Step 4: Generate the migration**

```bash
dotnet ef migrations add AddActionPinned --project src/Diariz.Domain --startup-project src/Diariz.Api
```

Check the generated `Up` reads exactly this - if it contains anything else, the model has drifted and you must stop and investigate rather than editing the migration by hand:

```csharp
            migrationBuilder.AddColumn<bool>(
                name: "Pinned",
                table: "RecordingActions",
                type: "boolean",
                nullable: false,
                defaultValue: false);
```

- [ ] **Step 5: Add `Pinned` to the DTO**

In `src/Diariz.Api/Contracts/ApiDtos.cs`, replace the `ActionListItemDto` record (currently at line 484) with:

```csharp
public record ActionListItemDto(
    Guid Id, Guid RecordingId, string RecordingName, string Text, string Actor, string Deadline,
    int Ordinal, bool Completed, DateTimeOffset? CompletedAt, DateTimeOffset CreatedAt, Guid RecordedByUserId,
    bool Pinned);
```

Required, not defaulted: both of its construction sites are real actions lists and both supply it.

- [ ] **Step 6: Add the filter to the controller**

In `src/Diariz.Api/Controllers/ActionsController.cs`, change the `List` signature and query. Replace the method's signature line and the LINQ block:

```csharp
    public async Task<ActionResult<IReadOnlyList<ActionListItemDto>>> List(
        [FromQuery] Guid? roomId = null, [FromQuery] bool? pinned = null)
    {
```

and, further down, the query:

```csharp
        var actions = await (
            from a in _db.RecordingActions
            join r in recs on a.RecordingId equals r.Id
            // Opt-in filter. Omitted means every action - the published default the n8n node relies on.
            where !pinned.HasValue || a.Pinned == pinned.Value
            orderby r.CreatedAt descending, a.Ordinal
            select new ActionListItemDto(
                a.Id, a.RecordingId, r.Name ?? r.Title, a.Text, a.Actor, a.Deadline,
                a.Ordinal, a.Completed, a.CompletedAt, a.CreatedAt, r.UserId, a.Pinned)).ToListAsync();
        return actions;
```

Also extend the `[EndpointDescription]` on `List` - append this paragraph to the existing string (this text ships to the n8n node and the API reference, so plain hyphens only):

```csharp
        "Pass `pinned=true` to get only the actions someone has pinned into the Actions views, which is what " +
        "the app itself shows. Omit it and you get every action, pinned or not - the default is unchanged.")]
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~ActionsControllerTests"`

Expected: PASS, including the pre-existing tests in that class.

- [ ] **Step 8: Mutation-verify the load-bearing guard**

Change the `where` clause to `where a.Pinned` (unconditionally pinned-only), re-run, and confirm `List_WithNoPinnedParameter_StillReturnsEveryAction` **fails**. Then revert.

If it passes with that mutation, the guard is worthless and must be rewritten before continuing.

- [ ] **Step 9: Build the whole solution**

Run: `dotnet build Diariz.slnx`

Expected: no errors. A unit-only test run misses compile breaks in the integration project, which constructs controllers too.

- [ ] **Step 10: Commit**

```bash
git add src/Diariz.Domain/Entities/RecordingAction.cs src/Diariz.Domain/Migrations src/Diariz.Api/Contracts/ApiDtos.cs src/Diariz.Api/Controllers/ActionsController.cs tests/Diariz.Api.Tests/ActionsControllerTests.cs
git commit -m "feat(api): add RecordingAction.Pinned and an opt-in pinned filter on GET /api/actions"
```

---

### Task 2: `POST /api/actions/pin`

**Files:**
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs` (beside `CompleteActionsRequest`, line 488)
- Modify: `src/Diariz.Api/Controllers/ActionsController.cs` (new action after `Complete`)
- Test: `tests/Diariz.Api.Tests/ActionsControllerTests.cs`

**Interfaces:**
- Consumes: `RecordingAction.Pinned` from Task 1.
- Produces: `PinActionsRequest(IReadOnlyList<Guid> Ids, bool Pinned)` and `ActionsController.Pin(PinActionsRequest req)` returning `NoContent`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Diariz.Api.Tests/ActionsControllerTests.cs`:

```csharp
    [Fact]
    public async Task Pin_SetsAndClearsTheFlag()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var rec = AddRecording(db, me, "Standup");
        var action = new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "Book the room", Ordinal = 0 };
        db.RecordingActions.Add(action);
        await db.SaveChangesAsync();

        await Build(db, me).Pin(new PinActionsRequest(new[] { action.Id }, true));
        Assert.True((await db.RecordingActions.FindAsync(action.Id))!.Pinned);

        await Build(db, me).Pin(new PinActionsRequest(new[] { action.Id }, false));
        Assert.False((await db.RecordingActions.FindAsync(action.Id))!.Pinned);
    }

    [Fact]
    public async Task Pin_IgnoresActionsBelongingToAnotherUser()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var mine = AddRecording(db, me, "Standup");
        var theirs = AddRecording(db, Guid.NewGuid(), "Their meeting");
        var ours = new RecordingAction { Id = Guid.NewGuid(), RecordingId = mine.Id, Text = "Mine", Ordinal = 0 };
        var hers = new RecordingAction { Id = Guid.NewGuid(), RecordingId = theirs.Id, Text = "Not mine", Ordinal = 0 };
        db.RecordingActions.AddRange(ours, hers);
        await db.SaveChangesAsync();

        // A mixed selection still works: the owned id is pinned, the other silently skipped rather than 403.
        await Build(db, me).Pin(new PinActionsRequest(new[] { ours.Id, hers.Id }, true));

        Assert.True((await db.RecordingActions.FindAsync(ours.Id))!.Pinned);
        Assert.False((await db.RecordingActions.FindAsync(hers.Id))!.Pinned);
    }

    [Fact]
    public async Task Pin_WithEmptyIdList_SucceedsWithoutDoingAnything()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();

        var result = await Build(db, me).Pin(new PinActionsRequest(Array.Empty<Guid>(), true));

        Assert.IsType<NoContentResult>(result);
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~ActionsControllerTests"`

Expected: compile error - `PinActionsRequest` and `Pin` do not exist.

- [ ] **Step 3: Add the request record**

In `src/Diariz.Api/Contracts/ApiDtos.cs`, immediately after `CompleteActionsRequest`:

```csharp
/// <summary>Pin a set of actions into the cross-meeting Actions views, or unpin them. Ids not owned by the
/// caller are ignored - same contract as <see cref="CompleteActionsRequest"/>.</summary>
public record PinActionsRequest(IReadOnlyList<Guid> Ids, bool Pinned);
```

- [ ] **Step 4: Add the endpoint**

In `src/Diariz.Api/Controllers/ActionsController.cs`, after the `Complete` method:

```csharp
    /// <summary>Pin the given actions into the cross-meeting Actions views (or unpin them). Ids the caller
    /// doesn't own are silently ignored - deliberately identical to <see cref="Complete"/>, since both set
    /// state that governs how an action appears outside its own recording.</summary>
    [HttpPost("pin")]
    [EndpointSummary("Pin or unpin action items")]
    [EndpointDescription(
        "Pins several actions at once so they appear in the cross-meeting Actions views, or unpins them with " +
        "`pinned: false`. An action is always visible on its own recording's page - pinning is what promotes " +
        "it into the Actions tab and the folder Actions tab, which show pinned items only.\n\n" +
        "Ids that are not yours are silently skipped rather than failing the call, so a mixed selection still " +
        "works; an empty list succeeds without doing anything.")]
    public async Task<IActionResult> Pin(PinActionsRequest req)
    {
        var ids = req.Ids?.ToHashSet() ?? new HashSet<Guid>();
        if (ids.Count == 0) return NoContent();

        var owned = await (
            from a in _db.RecordingActions
            join r in _db.Recordings on a.RecordingId equals r.Id
            where ids.Contains(a.Id) && r.UserId == UserId
            select a).ToListAsync();

        foreach (var a in owned) a.Pinned = req.Pinned;
        await _db.SaveChangesAsync();
        return NoContent();
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~ActionsControllerTests"`

Expected: PASS.

- [ ] **Step 6: Mutation-verify the ownership guard**

Remove `&& r.UserId == UserId` from the `where` clause, re-run, and confirm `Pin_IgnoresActionsBelongingToAnotherUser` **fails**. Revert.

- [ ] **Step 7: Commit**

```bash
git add src/Diariz.Api/Contracts/ApiDtos.cs src/Diariz.Api/Controllers/ActionsController.cs tests/Diariz.Api.Tests/ActionsControllerTests.cs
git commit -m "feat(api): add POST /api/actions/pin"
```

---

### Task 3: The `pinned` filter on `GET /api/sections/{id}/actions`

**Files:**
- Modify: `src/Diariz.Api/Controllers/SectionPageController.cs:104-128`
- Test: `tests/Diariz.Api.Tests/SectionPageControllerTests.cs`

**Interfaces:**
- Consumes: `RecordingAction.Pinned`, `ActionListItemDto.Pinned` from Task 1.
- Produces: `SectionPageController.Actions(Guid id, [FromQuery] bool? pinned = null)`.

- [ ] **Step 1: Write the failing test**

Add to `tests/Diariz.Api.Tests/SectionPageControllerTests.cs`. Read the file's existing `Build` helper and its section/recording setup first, and follow the same construction as the test at line 133 - the fixture shape below is the behaviour to assert, not a substitute for the file's own helpers:

```csharp
    [Fact]
    public async Task Actions_WithPinnedTrue_ReturnsOnlyPinnedActionsAcrossTheFolder()
    {
        // Same rule as the main Actions tab: the recording page is the only place an unpinned action
        // appears anywhere. A folder is an aggregation of recordings, so it follows that rule too.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        // ... build a section and place a recording in it, using this file's existing helpers ...
        db.RecordingActions.AddRange(
            new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "Book the room", Ordinal = 0, Pinned = true },
            new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "Chase the invoice", Ordinal = 1 });
        await db.SaveChangesAsync();

        var pinnedOnly = (await Build(db, userId).Actions(section.Id, pinned: true)).Value!;
        var everything = (await Build(db, userId).Actions(section.Id)).Value!;

        Assert.Single(pinnedOnly);
        Assert.Equal("Book the room", pinnedOnly[0].Text);
        Assert.True(pinnedOnly[0].Pinned);
        Assert.Equal(2, everything.Count); // the endpoint's own default stays unchanged
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~SectionPageControllerTests"`

Expected: compile error - `Actions` takes no `pinned` argument.

- [ ] **Step 3: Add the parameter and the filter**

In `src/Diariz.Api/Controllers/SectionPageController.cs`, change the signature:

```csharp
    public async Task<ActionResult<IReadOnlyList<ActionListItemDto>>> Actions(Guid id, [FromQuery] bool? pinned = null)
```

and the query's `where` and `select`:

```csharp
            where p.RoomId == roomId && p.SectionId.HasValue && allIds.Contains(p.SectionId.Value)
                  // Opt-in filter, same contract as GET /api/actions: omitted means every action.
                  && (!pinned.HasValue || a.Pinned == pinned.Value)
            orderby r.CreatedAt descending, a.Ordinal
            select new ActionListItemDto(
                a.Id, a.RecordingId, r.Name ?? r.Title, a.Text, a.Actor, a.Deadline,
                a.Ordinal, a.Completed, a.CompletedAt, a.CreatedAt, r.UserId, a.Pinned)).ToListAsync();
```

Append to its `[EndpointDescription]`:

```csharp
        "Pass `pinned=true` for only the pinned actions, which is what the folder's Actions tab shows; omit " +
        "it for every action in the folder.")]
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~SectionPageControllerTests"`

Expected: PASS.

- [ ] **Step 5: Mutation-verify**

Drop the `&& (!pinned.HasValue || a.Pinned == pinned.Value)` clause, re-run, confirm the new test fails on `Assert.Single`. Revert.

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Controllers/SectionPageController.cs tests/Diariz.Api.Tests/SectionPageControllerTests.cs
git commit -m "feat(api): add the pinned filter to the folder actions endpoint"
```

---

### Task 4: `RecordingActionDto.Pinned`, and extraction leaves fresh actions unpinned

**Files:**
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs:447-449`
- Modify: `src/Diariz.Api/Controllers/RecordingActionsController.cs:58`
- Modify: `src/Diariz.Api/Controllers/RecordingsController.cs:210`
- Test: `tests/Diariz.Api.Tests/RecordingActionsControllerTests.cs`

**Interfaces:**
- Produces: `RecordingActionDto(..., bool Completed = false, DateTimeOffset? CompletedAt = null, bool Pinned = false)`.

The parameter is **trailing and defaulted**, exactly as `Completed`/`CompletedAt` were added and for the same reason: five of its seven construction sites are export, chat and formula projections that do not track this state, and must stay untouched.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Diariz.Api.Tests/RecordingActionsControllerTests.cs`:

```csharp
    [Fact]
    public async Task List_CarriesThePinnedFlag()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var rec = new Recording { Id = Guid.NewGuid(), UserId = me, BlobKey = "k", Title = "Standup" };
        db.Recordings.Add(rec);
        db.RecordingActions.AddRange(
            new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "Book the room", Ordinal = 0, Pinned = true },
            new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "Chase the invoice", Ordinal = 1 });
        await db.SaveChangesAsync();

        // The recording's own page shows every action regardless of pin - that is the whole point of the
        // design, so this also guards against the filter leaking down to the per-recording endpoint.
        var dtos = (await BuildController(db, me).List(rec.Id)).Value!;

        Assert.Equal(2, dtos.Count);
        Assert.True(dtos.Single(d => d.Text == "Book the room").Pinned);
        Assert.False(dtos.Single(d => d.Text == "Chase the invoice").Pinned);
    }
```

Use whatever the file's existing controller-construction helper is called; read the top of the file first and match it rather than introducing `BuildController` if a differently-named helper already exists.

Then add the guard on the accepted re-extract behaviour:

```csharp
    [Fact]
    public async Task Extract_LeavesTheReplacementActionsUnpinned()
    {
        // Accepted by design: extraction replaces the whole list with new rows, and pins go the same way
        // completion already does. Asserted so that changing it later is a deliberate act, not a drift.
        // Build the controller with the fake actions client this file already uses, seed a transcript and a
        // pinned action, then:
        var fresh = (await BuildController(db, me).Extract(rec.Id)).Value!;

        Assert.All(fresh, a => Assert.False(a.Pinned));
    }
```

Fill in the setup from the file's existing `Extract` test - it already builds a transcription, segments and a fake `IActionsClient`. Copy that setup verbatim and add `Pinned = true` to the seeded action.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~RecordingActionsControllerTests"`

Expected: compile error - `RecordingActionDto` has no `Pinned`.

- [ ] **Step 3: Add the DTO field**

In `src/Diariz.Api/Contracts/ApiDtos.cs`:

```csharp
public record RecordingActionDto(
    Guid Id, string Text, string Actor, string Deadline, int Ordinal,
    bool Completed = false, DateTimeOffset? CompletedAt = null, bool Pinned = false);
```

- [ ] **Step 4: Pass the real value at the two sites that track it**

`src/Diariz.Api/Controllers/RecordingActionsController.cs:58`:

```csharp
            .Select(a => new RecordingActionDto(a.Id, a.Text, a.Actor, a.Deadline, a.Ordinal, a.Completed, a.CompletedAt, a.Pinned))
```

`src/Diariz.Api/Controllers/RecordingsController.cs:210`:

```csharp
            .Select(a => new RecordingActionDto(a.Id, a.Text, a.Actor, a.Deadline, a.Ordinal, a.Completed, a.CompletedAt, a.Pinned))
```

Also update the static `ToDto` helper in `RecordingActionsController.cs:41`:

```csharp
    private static RecordingActionDto ToDto(RecordingAction a) =>
        new(a.Id, a.Text, a.Actor, a.Deadline, a.Ordinal, a.Completed, a.CompletedAt, a.Pinned);
```

**Leave the other five construction sites alone** (`ChatController.cs:568`, `ChatController.cs:609`, `RecordingsController.cs:691`, `RecordingsController.cs:2124`, `FormulaRunProcessor.cs:479`). They are export and chat projections; the default covers them.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~RecordingActionsControllerTests"`

Expected: PASS.

- [ ] **Step 6: Mutation-verify**

Change `RecordingActionsController.cs:58` back to omit `a.Pinned`, re-run, and confirm `List_CarriesThePinnedFlag` fails on the `Assert.True`. Revert.

- [ ] **Step 7: Commit**

```bash
git add src/Diariz.Api/Contracts/ApiDtos.cs src/Diariz.Api/Controllers/RecordingActionsController.cs src/Diariz.Api/Controllers/RecordingsController.cs tests/Diariz.Api.Tests/RecordingActionsControllerTests.cs
git commit -m "feat(api): carry the pinned flag on a recording's own action list"
```

---

### Task 5: Merge carries the pin onto the survivor

**Files:**
- Modify: `src/Diariz.Api/Controllers/RecordingsController.cs:1918-1922`
- Test: `tests/Diariz.Api.Tests/` - the file containing the existing merge tests. Find it with `grep -rln "Merge" tests/Diariz.Api.Tests`.

**Interfaces:**
- Consumes: `RecordingAction.Pinned` from Task 1.

Merge copies each merged-away recording's actions into **new rows** on the survivor. Without this, a pinned action silently disappears from the Actions tab when a user merges two parts of one meeting.

- [ ] **Step 1: Write the failing test**

Add to the existing merge test file, following its established setup for building two recordings and calling `Merge`:

```csharp
    [Fact]
    public async Task Merge_CarriesThePinnedFlagOntoTheSurvivor()
    {
        // A pinned action must not vanish from the Actions tab because the user merged two halves of one
        // meeting. Merging is a filing operation.
        // ... build survivor + second recording using this file's existing merge setup ...
        db.RecordingActions.Add(new RecordingAction
        {
            Id = Guid.NewGuid(), RecordingId = second.Id, Text = "Book the room", Ordinal = 0, Pinned = true,
        });
        await db.SaveChangesAsync();

        await BuildController(db, me).Merge(new MergeRecordingsRequest(new[] { survivor.Id, second.Id }));

        var moved = await db.RecordingActions.SingleAsync(a => a.RecordingId == survivor.Id && a.Text == "Book the room");
        Assert.True(moved.Pinned);
    }
```

Read the file's existing merge test for the exact `MergeRecordingsRequest` shape and controller helper name before writing this - do not guess them.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~Merge"`

Expected: FAIL - `Assert.True(moved.Pinned)` gets `false`, because the copy does not carry the flag.

- [ ] **Step 3: Carry the flag**

In `src/Diariz.Api/Controllers/RecordingsController.cs`, in the action-copy loop:

```csharp
            _db.RecordingActions.Add(new RecordingAction
            {
                Id = Guid.NewGuid(), RecordingId = survivor.Id,
                Text = a.Text, Actor = a.Actor, Deadline = a.Deadline, Ordinal = nextActionOrdinal++,
                Pinned = a.Pinned,
            });
```

**Change nothing else in this block.** `Completed`/`CompletedAt` are also dropped here, which is [issue #676](https://github.com/kenhayward/Diariz/issues/676) - a separate pre-existing bug with its own fix and its own release entry. Fixing it here would smuggle an unrelated behaviour change into this PR.

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~Merge"`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Controllers/RecordingsController.cs tests/Diariz.Api.Tests
git commit -m "feat(api): carry an action's pin across a recording merge"
```

---

### Task 6: Integration tests against real Postgres

**Files:**
- Modify: `tests/Diariz.Api.IntegrationTests/ActionsIntegrationTests.cs`
- Modify: `tests/Diariz.Api.IntegrationTests/SectionPageIntegrationTests.cs`

Requires Docker. The in-memory provider does not translate relational queries faithfully, so the `pinned` filter needs verifying where it actually runs.

- [ ] **Step 1: Write the failing test for the library endpoint**

Add to `tests/Diariz.Api.IntegrationTests/ActionsIntegrationTests.cs`, following the existing `List_And_Complete_AcrossRecordings_RoundTripThroughRealDb` structure of one `await using` block per phase:

```csharp
    [Fact]
    public async Task Pin_ThenListPinned_RoundTripsThroughRealDb()
    {
        Guid userId, pinnedId, plainId;
        await using (var db = fx.CreateDbContext())
        {
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
            var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = "k1", Title = "Standup" };
            var a1 = new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "Book the room", Ordinal = 0 };
            var a2 = new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "Chase the invoice", Ordinal = 1 };
            db.AddRange(user, rec, a1, a2);
            await db.SaveChangesAsync();
            (userId, pinnedId, plainId) = (user.Id, a1.Id, a2.Id);
        }

        await using (var db = fx.CreateDbContext())
            await Build(db, userId).Pin(new PinActionsRequest(new[] { pinnedId }, true));

        await using (var db = fx.CreateDbContext())
        {
            var pinnedOnly = (await Build(db, userId).List(pinned: true)).Value!;
            Assert.Single(pinnedOnly);
            Assert.Equal(pinnedId, pinnedOnly[0].Id);
            Assert.True(pinnedOnly[0].Pinned);

            // The endpoint's own default is unchanged - both come back when the filter is omitted.
            var everything = (await Build(db, userId).List()).Value!;
            Assert.Equal(2, everything.Count);
            Assert.Contains(everything, d => d.Id == plainId && !d.Pinned);
        }
    }
```

- [ ] **Step 2: Write the failing test for the folder endpoint**

Add to `tests/Diariz.Api.IntegrationTests/SectionPageIntegrationTests.cs`, using that file's existing helpers for creating a room, a section, a sub-section and placing recordings:

```csharp
    [Fact]
    public async Task Actions_WithPinnedTrue_FiltersAcrossTheFolderAndItsSubFolders()
    {
        // Guards the filter where it actually runs: the folder query joins RoomRecordings and walks the
        // sub-folder id set, so the pinned predicate sits inside a shape the in-memory provider does not
        // reproduce.
        // ... build a section with a sub-section, a recording in each, one pinned action in each ...
        var pinnedOnly = (await Build(db, userId).Actions(parent.Id, pinned: true)).Value!;
        var everything = (await Build(db, userId).Actions(parent.Id)).Value!;

        Assert.Equal(2, pinnedOnly.Count);      // one from the folder, one from the sub-folder
        Assert.All(pinnedOnly, d => Assert.True(d.Pinned));
        Assert.Equal(4, everything.Count);
    }
```

- [ ] **Step 3: Run them to verify they fail, then pass**

Run: `dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~Actions"`

These should pass immediately given Tasks 1-3, which is expected for an integration layer that verifies already-implemented behaviour on a real provider. **Mutation-verify both** instead of relying on a red: drop the `pinned` predicate in each controller, watch each test fail, revert.

- [ ] **Step 4: Commit**

```bash
git add tests/Diariz.Api.IntegrationTests/ActionsIntegrationTests.cs tests/Diariz.Api.IntegrationTests/SectionPageIntegrationTests.cs
git commit -m "test(api): verify the pinned filter and pin endpoint on real Postgres"
```

---

### Task 7: Web foundation - types, API client, and locale keys

**Files:**
- Modify: `apps/web/src/lib/types.ts:455-463` and `:519-531`
- Modify: `apps/web/src/lib/api.ts` (action methods around lines 700-725, 823-856, 1086-1089)
- Modify: `apps/web/src/locales/en/workspace.json`, `es/`, `fr/`, `de/`
- Test: `apps/web/src/locales.test.ts` (existing parity gate - no new test file)

**Interfaces:**
- Produces: `RecordingAction.pinned: boolean`, `ActionListItem.pinned: boolean`, `api.pinActions(ids: string[], pinned: boolean): Promise<void>`, `api.listAllActions(roomId?: string | null, pinned?: boolean)`, `api.listSectionActions(id: string, pinned?: boolean)`, and the locale keys `pinActionAria`, `unpinActionAria`, `colPin`, plus rewritten `noActionsAll` and `folderNoActions`.

- [ ] **Step 1: Add the English keys only, and run the parity gate to watch it fail**

In `apps/web/src/locales/en/workspace.json`, add beside `colDone` (line 524):

```json
  "colPin": "Pin",
  "pinActionAria": "Pin action {{row}}",
  "unpinActionAria": "Unpin action {{row}}",
```

and replace the two empty-state strings. `folderNoActions` (line 216):

```json
  "folderNoActions": "No pinned actions in this folder. Pin an action on its meeting to track it here.",
```

`noActionsAll` (line 560):

```json
  "noActionsAll": "No pinned actions yet. Open a meeting, then pin the actions you want to track and they will appear here.",
```

Both are plain hyphens only and are the copy a user sees on day one, when every list is empty by design.

- [ ] **Step 2: Run the locale parity gate to verify it fails**

Run: `cd apps/web && npx vitest run src/locales.test.ts`

Expected: FAIL - `es`, `fr` and `de` are missing `colPin`, `pinActionAria` and `unpinActionAria`.

- [ ] **Step 3: Add the three other languages**

`es/workspace.json`:

```json
  "colPin": "Fijar",
  "pinActionAria": "Fijar la acción {{row}}",
  "unpinActionAria": "Dejar de fijar la acción {{row}}",
```
```json
  "folderNoActions": "No hay acciones fijadas en esta carpeta. Fija una acción en su reunión para seguirla aquí.",
```
```json
  "noActionsAll": "Aún no hay acciones fijadas. Abre una reunión, fija las acciones que quieras seguir y aparecerán aquí.",
```

`fr/workspace.json`:

```json
  "colPin": "Épingler",
  "pinActionAria": "Épingler l'action {{row}}",
  "unpinActionAria": "Détacher l'action {{row}}",
```
```json
  "folderNoActions": "Aucune action épinglée dans ce dossier. Épinglez une action depuis sa réunion pour la suivre ici.",
```
```json
  "noActionsAll": "Aucune action épinglée pour l'instant. Ouvrez une réunion, épinglez les actions à suivre et elles apparaîtront ici.",
```

`de/workspace.json`:

```json
  "colPin": "Anheften",
  "pinActionAria": "Aktion {{row}} anheften",
  "unpinActionAria": "Aktion {{row}} lösen",
```
```json
  "folderNoActions": "Keine angehefteten Aktionen in diesem Ordner. Hefte eine Aktion in ihrer Besprechung an, um sie hier zu verfolgen.",
```
```json
  "noActionsAll": "Noch keine angehefteten Aktionen. Öffne eine Besprechung, hefte die Aktionen an, die du verfolgen willst, und sie erscheinen hier.",
```

Keep each key in the same position within its file as the English one, so the four catalogues stay diffable side by side.

- [ ] **Step 4: Run the parity gate and the dash gate**

Run: `cd apps/web && npx vitest run src/locales.test.ts src/lib/noFancyDashes.test.ts`

Expected: PASS both. If `noFancyDashes` fails, an em or en dash slipped into one of the new strings - replace it with a plain hyphen.

- [ ] **Step 5: Add `pinned` to both web types**

`apps/web/src/lib/types.ts`, in `RecordingAction`:

```typescript
export interface RecordingAction {
  id: string;
  text: string;
  actor: string;
  deadline: string;
  ordinal: number;
  completed: boolean;
  completedAt: string | null;
  /// Whether this action is pinned into the cross-meeting Actions views. It is always visible here, on its
  /// own recording; pinning is what promotes it into the Actions tab and the folder Actions tab.
  pinned: boolean;
}
```

and in `ActionListItem`, after `recordedByUserId`:

```typescript
  pinned: boolean;
```

- [ ] **Step 6: Add and extend the API client methods**

In `apps/web/src/lib/api.ts`, change `listAllActions` (around line 825):

```typescript
  /// Every action across the recordings the user can see (the "Actions" tab). With a roomId it is scoped to
  /// that shared room's recordings; without one, the user's own library. `pinned` filters to the actions
  /// someone has pinned - the app always passes true, but the endpoint's own default is every action, which
  /// is what the published API and the n8n node rely on.
  async listAllActions(roomId?: string | null, pinned?: boolean): Promise<ActionListItem[]> {
    const params: Record<string, string | boolean> = {};
    if (roomId) params.roomId = roomId;
    if (pinned !== undefined) params.pinned = pinned;
    const { data } = await http.get<ActionListItem[]>(`/api/actions`, { params });
    return data;
  },
```

Add beside `completeActions` (around line 853):

```typescript
  /// Pin a set of actions into the cross-meeting Actions views, or unpin them. Works across recordings;
  /// ids not owned are ignored, exactly like completeActions.
  async pinActions(ids: string[], pinned: boolean): Promise<void> {
    await http.post(`/api/actions/pin`, { ids, pinned });
  },
```

Change `listSectionActions` (around line 1086):

```typescript
  async listSectionActions(id: string, pinned?: boolean): Promise<ActionListItem[]> {
    const { data } = await http.get<ActionListItem[]>(`/api/sections/${id}/actions`, {
      params: pinned === undefined ? undefined : { pinned },
    });
    return data;
  },
```

Note the object-parameter form for `listAllActions`: axios omits `undefined` values, and passing an empty object is equivalent to passing none.

- [ ] **Step 7: Typecheck and run the full web suite**

Run: `cd apps/web && npm run build`

Expected: the `tsc` step fails on every component and page that constructs a `RecordingAction` or `ActionListItem` without `pinned`, plus `RecordingsPanel.test.tsx` is unaffected (tests are excluded from `tsc`). Fix each **production** site by threading the real value through - do not add a default to the interface, since a missing `pinned` should be a compile error, not a silent `false`.

Run: `cd apps/web && npx vitest run`

Expected: `RecordingsPanel.test.tsx` **fails** at line 279 - `expect(api.listAllActions).toHaveBeenCalledWith("eng-room")` no longer matches, because the call now carries a second argument. That failure is expected and is fixed in Task 9. Leave it failing and note it.

- [ ] **Step 8: Commit**

```bash
git add apps/web/src/lib/types.ts apps/web/src/lib/api.ts apps/web/src/locales
git commit -m "feat(web): add the pinned field, pin client method, and pin copy in four languages"
```

---

### Task 8: The pin column on the recording page (`ActionsTable`)

**Files:**
- Modify: `apps/web/src/components/ActionsTable.tsx`
- Modify: `apps/web/src/pages/RecordingDetail.tsx` (around lines 743-780 for the handler, 1296-1303 for the render)
- Test: `apps/web/src/components/ActionsTable.test.tsx`

**Interfaces:**
- Consumes: `RecordingAction.pinned`, `api.pinActions` from Task 7; `colPin`/`pinActionAria`/`unpinActionAria`.
- Produces: `ActionsTable` gains an `onTogglePin: (id: string, pinned: boolean) => void` prop.

This is the primary place a user decides what to track, so the control leads the row rather than hiding at the end.

- [ ] **Step 1: Write the failing tests**

In `apps/web/src/components/ActionsTable.test.tsx`, first add `pinned: false` to the `action` fixture factory, then add `onTogglePin: vi.fn()` to the `build` helper's `handlers` object. Then:

```typescript
  it("shows an unpinned action's pin control as the pin affordance", () => {
    build([action()]);
    expect(screen.getByLabelText("Pin action 1")).toBeTruthy();
  });

  it("shows a pinned action's control as the unpin affordance", () => {
    build([action({ pinned: true })]);
    expect(screen.getByLabelText("Unpin action 1")).toBeTruthy();
  });

  it("pins an unpinned action through onTogglePin", () => {
    const h = build([action()]);
    fireEvent.click(screen.getByLabelText("Pin action 1"));
    expect(h.onTogglePin).toHaveBeenCalledWith("a1", true);
  });

  it("unpins a pinned action through onTogglePin", () => {
    const h = build([action({ pinned: true })]);
    fireEvent.click(screen.getByLabelText("Unpin action 1"));
    expect(h.onTogglePin).toHaveBeenCalledWith("a1", false);
  });

  it("still lists unpinned actions - the recording page shows everything", () => {
    // The whole design rests on this: pinning changes the cross-meeting views, never this one.
    build([action({ id: "a1", text: "Book the room", pinned: true }), action({ id: "a2", text: "Chase the invoice" })]);
    expect((screen.getByLabelText("Action 1") as HTMLInputElement).value).toBe("Book the room");
    expect((screen.getByLabelText("Action 2") as HTMLInputElement).value).toBe("Chase the invoice");
  });
```

This test file does **not** stub i18next, so assertions use the English strings from `en/workspace.json`.

- [ ] **Step 2: Run them to verify they fail**

Run: `cd apps/web && npx vitest run src/components/ActionsTable.test.tsx`

Expected: FAIL - `Unable to find a label with the text of: Pin action 1`.

- [ ] **Step 3: Add the column and the control**

In `apps/web/src/components/ActionsTable.tsx`, add `onTogglePin` to the component's props type and destructuring:

```typescript
  onTogglePin,
```
```typescript
  onTogglePin: (id: string, pinned: boolean) => void;
```

Add the header cell as the **first** column and re-balance the widths:

```tsx
              <th className="w-[5%] pb-1 pr-1 font-medium text-center">{t("colPin")}</th>
              <th className="w-[6%] pb-1 pr-1 font-medium text-center">{t("colDone")}</th>
              <th className="w-[36%] pb-1 pr-2 font-medium">{t("colAction")}</th>
              <th className="w-[15%] pb-1 pr-2 font-medium">{t("colActor")}</th>
              <th className="w-[18%] pb-1 pr-2 font-medium">{t("colDeadline")}</th>
              <th className="w-[15%] pb-1 pr-2 font-medium">{t("colCompletedDate")}</th>
              <th className="w-[5%] pb-1" aria-hidden />
```

Pass the handler down to `ActionRow` (add it to that component's props type too), and add the cell as the first `<td>` in the row:

```tsx
      <td className="py-1 pr-1 text-center">
        <button
          type="button"
          aria-label={action.pinned ? t("unpinActionAria", { row }) : t("pinActionAria", { row })}
          aria-pressed={action.pinned}
          onClick={() => onTogglePin(action.id, !action.pinned)}
          className={`mt-1 rounded px-1 ${
            action.pinned
              ? "text-blue-600 dark:text-blue-400"
              : "text-gray-300 hover:text-gray-500 dark:text-gray-600 dark:hover:text-gray-400"
          }`}
        >
          <PinIcon filled={action.pinned} />
        </button>
      </td>
```

Add the icon at the bottom of the file, beside the existing `Cell` helper:

```tsx
/// A pin glyph, solid when pinned and outline when not - the bi-state affordance itself, so the two states
/// differ by more than colour alone.
function PinIcon({ filled }: { filled: boolean }) {
  return (
    <svg
      width="14"
      height="14"
      viewBox="0 0 24 24"
      fill={filled ? "currentColor" : "none"}
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden
    >
      <path d="M12 17v5" />
      <path d="M9 10.76a2 2 0 0 1-1.11 1.79l-1.78.9A2 2 0 0 0 5 15.24V16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-.76a2 2 0 0 0-1.11-1.79l-1.78-.9A2 2 0 0 1 15 10.76V7a1 1 0 0 1 1-1 2 2 0 0 0 0-4H8a2 2 0 0 0 0 4 1 1 0 0 1 1 1Z" />
    </svg>
  );
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd apps/web && npx vitest run src/components/ActionsTable.test.tsx`

Expected: PASS, including the file's pre-existing tests.

- [ ] **Step 5: Wire the recording page**

In `apps/web/src/pages/RecordingDetail.tsx`, add a handler beside the existing `toggleActionComplete` (around line 770). Read that function first and mirror its error handling and query invalidation exactly:

```typescript
  async function toggleActionPin(actionId: string, pinned: boolean) {
    if (!id) return;
    try {
      await api.pinActions([actionId], pinned);
      // The cross-meeting Actions tab is the whole point of pinning, so refresh it as well as this page.
      qc.invalidateQueries({ queryKey: ["recording", id] });
      qc.invalidateQueries({ queryKey: ["actions", "all"] });
    } catch (e) {
      setError(apiErrorMessage(e));
    }
  }
```

Use whatever the surrounding functions use for the query client, the error setter and the recording query key - copy from `toggleActionComplete` rather than assuming the names above.

Pass it to the component (around line 1296):

```tsx
        <ActionsTable
          actions={rec.actions}
          onAdd={addAction}
          onUpdate={updateAction}
          onToggleComplete={toggleActionComplete}
          onTogglePin={toggleActionPin}
          onDelete={removeAction}
        />
```

- [ ] **Step 6: Run the page's own tests**

Run: `cd apps/web && npx vitest run src/pages/RecordingDetail.test.tsx`

Expected: PASS. If the file's api mock is a `vi.mock` factory that omits `pinActions`, add it as `pinActions: vi.fn().mockResolvedValue(undefined)`. Check first whether any existing test in that file guards a method **by its absence from the mock factory** - if so, convert that test to an explicit call assertion rather than leaving a guard that this change silently destroys.

- [ ] **Step 7: Mutation-verify**

Change the control's `onClick` to `onTogglePin(action.id, action.pinned)` (dropping the negation), re-run, and confirm both toggle tests fail. Revert.

- [ ] **Step 8: Commit**

```bash
git add apps/web/src/components/ActionsTable.tsx apps/web/src/components/ActionsTable.test.tsx apps/web/src/pages/RecordingDetail.tsx
git commit -m "feat(web): pin an action from its recording page"
```

---

### Task 9: The Actions tab shows pinned actions only

**Files:**
- Modify: `apps/web/src/components/ActionsTab.tsx`
- Modify: `apps/web/src/components/RecordingsPanel.tsx:155-167, 426-431`
- Test: `apps/web/src/components/ActionsTab.test.tsx`
- Test: `apps/web/src/components/RecordingsPanel.test.tsx:274-280`

**Interfaces:**
- Consumes: `ActionListItem.pinned`, `api.pinActions`, `api.listAllActions(roomId, pinned)` from Task 7.
- Produces: `ActionsTab` gains `myUserId: string | null` and `onTogglePin: (id: string, pinned: boolean) => void`.

`ActionsTab` has no notion of row ownership today. In a shared room its rows can belong to other people's recordings, and pinning is owner-only, so without the gate it would offer a control the API silently ignores.

- [ ] **Step 1: Write the failing tests for the component**

In `apps/web/src/components/ActionsTab.test.tsx`, add `recordedByUserId: "me"` and `pinned: true` to the `action` fixture (the current fixture omits `recordedByUserId` entirely), and change `renderTab` to accept overrides:

```typescript
function renderTab(over: Partial<ActionListItem> = {}, myUserId: string | null = "me") {
  const onTogglePin = vi.fn();
  render(
    <MemoryRouter>
      <ActionsTab
        actions={[{ ...action, ...over }]}
        persons={["Sam"]}
        person={null}
        onPerson={() => {}}
        myUserId={myUserId}
        onTogglePin={onTogglePin}
      />
    </MemoryRouter>,
  );
  return onTogglePin;
}
```

Then add a describe block. **This file stubs i18next as `t: (k) => k`, so labels are raw keys:**

```typescript
describe("ActionsTab pinning", () => {
  it("offers an unpin control on my own action", () => {
    renderTab();
    expect(screen.getByLabelText("unpinActionAria")).toBeTruthy();
  });

  it("unpins through onTogglePin", async () => {
    const onTogglePin = renderTab();
    await userEvent.click(screen.getByLabelText("unpinActionAria"));
    expect(onTogglePin).toHaveBeenCalledWith("a1", false);
  });

  it("disables the control on someone else's action, because pinning is owner-only", async () => {
    // In a shared room the tab lists other people's recordings. The API silently ignores a pin on an
    // action you do not own, so offering a live control here would be a button that does nothing.
    const onTogglePin = renderTab({ recordedByUserId: "someone-else" });
    const control = screen.getByLabelText("unpinActionAria") as HTMLButtonElement;
    expect(control.disabled).toBe(true);
    await userEvent.click(control);
    expect(onTogglePin).not.toHaveBeenCalled();
  });
});
```

Add `import userEvent from "@testing-library/user-event";` at the top. **`userEvent` is required here, not `fireEvent`:** `fireEvent.click` fires handlers on disabled elements, so the third test would pass for a reason the browser never reproduces.

- [ ] **Step 2: Run them to verify they fail**

Run: `cd apps/web && npx vitest run src/components/ActionsTab.test.tsx`

Expected: FAIL - no element with label `unpinActionAria`.

- [ ] **Step 3: Add the control to the component**

In `apps/web/src/components/ActionsTab.tsx`, extend the props:

```typescript
  /// The signed-in user's id, compared against each row's `recordedByUserId`. Pinning is owner-only, so a
  /// row from someone else's recording (possible in a shared room) gets a disabled control.
  myUserId: string | null;
  onTogglePin: (id: string, pinned: boolean) => void;
```

Inside the `actions.map` callback, add alongside the existing `isSel` and `completedDate`:

```typescript
            const isOwner = myUserId != null && a.recordedByUserId === myUserId;
```

and add the control as the first child of the `<div className="flex items-start gap-2">`, before the select-mode checkbox:

```tsx
                  <button
                    type="button"
                    disabled={!isOwner}
                    aria-label={a.pinned ? t("unpinActionAria", { row: a.text }) : t("pinActionAria", { row: a.text })}
                    aria-pressed={a.pinned}
                    onClick={(e) => {
                      e.stopPropagation();
                      if (isOwner) onTogglePin(a.id, !a.pinned);
                    }}
                    className={`mt-0.5 shrink-0 rounded px-0.5 ${
                      isOwner
                        ? "text-blue-600 hover:text-blue-800 dark:text-blue-400 dark:hover:text-blue-200"
                        : "cursor-default text-gray-300 dark:text-gray-600"
                    }`}
                  >
                    <PinGlyph />
                  </button>
```

`e.stopPropagation()` matters: the row's own `onClick` selects it, and pinning must not also change the selection.

Add the glyph at the bottom of the file:

```tsx
/// A solid pin. Every row in this list is pinned by definition, so there is no outline state to show here.
function PinGlyph() {
  return (
    <svg width="13" height="13" viewBox="0 0 24 24" fill="currentColor" aria-hidden>
      <path d="M12 17v5M9 10.76a2 2 0 0 1-1.11 1.79l-1.78.9A2 2 0 0 0 5 15.24V16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-.76a2 2 0 0 0-1.11-1.79l-1.78-.9A2 2 0 0 1 15 10.76V7a1 1 0 0 1 1-1 2 2 0 0 0 0-4H8a2 2 0 0 0 0 4 1 1 0 0 1 1 1Z" />
    </svg>
  );
}
```

- [ ] **Step 4: Run the component tests to verify they pass**

Run: `cd apps/web && npx vitest run src/components/ActionsTab.test.tsx`

Expected: PASS.

- [ ] **Step 5: Update the panel's assertion to the new contract**

In `apps/web/src/components/RecordingsPanel.test.tsx`, the test at line 274 currently asserts the old signature and is failing from Task 7. Change both of its `listAllActions` assertions, and the one in the test below it:

```typescript
    fireEvent.click(screen.getByRole("tab", { name: "Actions" }));
    // The tab is opt-in: it asks for pinned actions only. Without this the tab would silently go back to
    // listing everything, and every other test here would still pass.
    await waitFor(() => expect(api.listAllActions).toHaveBeenCalledWith("eng-room", true));
```

and in `keeps Actions + Tags owner-scoped (no roomId) in the personal room`:

```typescript
    await waitFor(() => expect(api.listAllActions).toHaveBeenCalledWith(undefined, true));
```

Add `pinActions: vi.fn().mockResolvedValue(undefined)` to that file's `vi.mock("../lib/api")` factory, beside `completeActions` at line 73.

- [ ] **Step 6: Run it to verify it fails for the right reason**

Run: `cd apps/web && npx vitest run src/components/RecordingsPanel.test.tsx`

Expected: FAIL with the received call being `("eng-room")` rather than `("eng-room", true)` - the panel does not yet pass the flag.

- [ ] **Step 7: Make the panel ask for pinned actions**

In `apps/web/src/components/RecordingsPanel.tsx`, add the auth import at the top:

```typescript
import { useAuth } from "../auth";
```

and inside the component, near the other hooks:

```typescript
  const { id: myId } = useAuth();
```

Change the query (line 156):

```typescript
  const { data: allActions = [] } = useQuery({
    queryKey: ["actions", "all", aggRoomId ?? null],
    // Pinned only: the tab is an opt-in list, not an inventory. Unpinned actions stay on their recording.
    queryFn: () => api.listAllActions(aggRoomId, true),
    enabled: tab === "actions",
  });
```

and the render (line 430):

```tsx
            <ActionsTab
              actions={visibleActions}
              persons={persons}
              person={personFilter}
              onPerson={setPersonFilter}
              myUserId={myId}
              onTogglePin={async (actionId, pinned) => {
                await api.pinActions([actionId], pinned);
                qc.invalidateQueries({ queryKey: ["actions", "all"] });
                qc.invalidateQueries({ queryKey: ["recording"] });
              }}
            />
```

Use the query client variable already in scope in that component - read the surrounding code for its name rather than assuming `qc`.

- [ ] **Step 8: Run the panel tests to verify they pass**

Run: `cd apps/web && npx vitest run src/components/RecordingsPanel.test.tsx`

Expected: PASS.

- [ ] **Step 9: Mutation-verify the opt-in guard**

Change `api.listAllActions(aggRoomId, true)` back to `api.listAllActions(aggRoomId)`, re-run, and confirm both panel assertions fail. Revert. This is the guard that the feature exists at all - if it cannot fail, nothing else in this task proves the tab is opt-in.

- [ ] **Step 10: Commit**

```bash
git add apps/web/src/components/ActionsTab.tsx apps/web/src/components/ActionsTab.test.tsx apps/web/src/components/RecordingsPanel.tsx apps/web/src/components/RecordingsPanel.test.tsx
git commit -m "feat(web): the Actions tab lists pinned actions only"
```

---

### Task 10: The folder Actions tab shows pinned actions only

**Files:**
- Modify: `apps/web/src/components/FolderActionsTable.tsx`
- Modify: `apps/web/src/pages/SectionDetail.tsx:84, 286-293`
- Test: `apps/web/src/components/FolderActionsTable.test.tsx`

**Interfaces:**
- Consumes: `ActionListItem.pinned`, `api.listSectionActions(id, pinned)`, `api.pinActions` from Task 7.
- Produces: `FolderActionsTable` gains `onTogglePin: (id: string, pinned: boolean) => void`.

This component already receives `myUserId` and already gates edit, complete and delete on it, so the pin follows an established pattern here rather than introducing one.

- [ ] **Step 1: Write the failing tests**

In `apps/web/src/components/FolderActionsTable.test.tsx`, add `pinned: true` to its action fixture and `onTogglePin: vi.fn()` to its handler set, then:

```typescript
  it("unpins my own row through onTogglePin", async () => {
    const h = build([item({ id: "a1", pinned: true, recordedByUserId: "me" })], "me");
    await userEvent.click(screen.getByLabelText("Unpin action 1"));
    expect(h.onTogglePin).toHaveBeenCalledWith("a1", false);
  });

  it("disables the pin control on another user's row", async () => {
    // Consistent with the Done checkbox and the editable cells in this same table: a co-viewer's row from
    // someone else's recording is read-only.
    const h = build([item({ id: "a1", pinned: true, recordedByUserId: "someone-else" })], "me");
    const control = screen.getByLabelText("Unpin action 1") as HTMLButtonElement;
    expect(control.disabled).toBe(true);
    await userEvent.click(control);
    expect(h.onTogglePin).not.toHaveBeenCalled();
  });
```

Read the file's existing `build` and `item` helpers and match their signatures; the names above are illustrative of the shape, not necessarily the file's own. Import `userEvent` - again required, since `fireEvent` ignores `disabled`.

- [ ] **Step 2: Run them to verify they fail**

Run: `cd apps/web && npx vitest run src/components/FolderActionsTable.test.tsx`

Expected: FAIL - no such label.

- [ ] **Step 3: Add the control**

In `apps/web/src/components/FolderActionsTable.tsx`, add `onTogglePin` to both the component's and `Row`'s props types and pass it through. Add a header cell after `colMeeting` and re-balance:

```tsx
            <th className="w-[16%] pb-1 pr-2 font-medium">{t("colMeeting")}</th>
            <th className="w-[5%] pb-1 pr-1 text-center font-medium">{t("colPin")}</th>
            <th className="w-[6%] pb-1 pr-1 text-center font-medium">{t("colDone")}</th>
            <th className="w-[32%] pb-1 pr-2 font-medium">{t("colAction")}</th>
            <th className="w-[13%] pb-1 pr-2 font-medium">{t("colActor")}</th>
            <th className="w-[16%] pb-1 pr-2 font-medium">{t("colDeadline")}</th>
            <th className="w-[7%] pb-1 pr-2 font-medium">{t("colCompletedDate")}</th>
            <th className="w-[5%] pb-1" aria-hidden />
```

and the matching `<td>` in `Row`, immediately after the meeting-link cell:

```tsx
      <td className="py-1 pr-1 text-center">
        <button
          type="button"
          disabled={!isOwner}
          aria-label={action.pinned ? t("unpinActionAria", { row }) : t("pinActionAria", { row })}
          aria-pressed={action.pinned}
          onClick={() => isOwner && onTogglePin(action.id, !action.pinned)}
          className={`mt-1 rounded px-1 ${
            isOwner ? "text-blue-600 dark:text-blue-400" : "cursor-default text-gray-300 dark:text-gray-600"
          }`}
        >
          <svg width="13" height="13" viewBox="0 0 24 24" fill="currentColor" aria-hidden>
            <path d="M12 17v5M9 10.76a2 2 0 0 1-1.11 1.79l-1.78.9A2 2 0 0 0 5 15.24V16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-.76a2 2 0 0 0-1.11-1.79l-1.78-.9A2 2 0 0 1 15 10.76V7a1 1 0 0 1 1-1 2 2 0 0 0 0-4H8a2 2 0 0 0 0 4 1 1 0 0 1 1 1Z" />
          </svg>
        </button>
      </td>
```

- [ ] **Step 4: Run the component tests to verify they pass**

Run: `cd apps/web && npx vitest run src/components/FolderActionsTable.test.tsx`

Expected: PASS.

- [ ] **Step 5: Make the folder page ask for pinned actions**

In `apps/web/src/pages/SectionDetail.tsx`, change the query at line 84:

```typescript
  // Pinned only, the same rule as the main Actions tab - a folder is an aggregation of recordings, and the
  // recording page stays the only place an unpinned action appears.
  const { data: actions } = useQuery({
    queryKey: ["section-actions", id],
    queryFn: () => api.listSectionActions(id!, true),
    enabled: !!id,
  });
```

and add the handler to the render at line 286, matching the `run(...)` helper the sibling handlers already use:

```tsx
        onTogglePin={(actionId, pinned) => run(() => api.pinActions([actionId], pinned), "workspace:errUpdateAction", ["section-actions", id])}
```

- [ ] **Step 6: Run the page's tests**

Run: `cd apps/web && npx vitest run src/pages/SectionDetail.test.tsx`

Expected: PASS. Add `pinActions` and update `listSectionActions` in that file's api mock if it has one.

- [ ] **Step 7: Run the whole web suite**

Run: `cd apps/web && npx vitest run`

Expected: all green, no `act(...)` warnings, no console errors. `src/test-setup.ts` fails any test that breaks the act contract, so a warning here is a failure, not noise.

- [ ] **Step 8: Commit**

```bash
git add apps/web/src/components/FolderActionsTable.tsx apps/web/src/components/FolderActionsTable.test.tsx apps/web/src/pages/SectionDetail.tsx
git commit -m "feat(web): the folder Actions tab lists pinned actions only"
```

---

### Task 11: Docs, the release checklist, and the n8n node

**Files:**
- Modify: `version.json` and its seven mirrors
- Modify: `apps/web/src/lib/releaseNotes/current.ts`
- Modify: `apps/web/src/lib/appInfo.ts` (the `CAPABILITIES` **Action items** row)
- Modify: `README.md` (the **Action items** Features row, line 35)
- Modify: `docs/features.md` (the action-items bullet, around line 705-712)
- Modify: `docs/Overall_Synopsis_of_Platform.md` (the "Action management (cross-meeting)" section, around line 933)
- Modify: `docs/Data_Schema.md` (migration-history table + the `RecordingActions` column table)
- Modify: `apps/web/src/content/help/en/action-items.md`
- Regenerate: `integrations/n8n-nodes-diariz/nodes/Diariz/generated/index.ts` and the OpenAPI snapshot

- [ ] **Step 1: Bump the version in all eight places**

`0.260.0` -> `0.261.0` in:

```
version.json
apps/web/package.json
apps/desktop/package.json
integrations/n8n-nodes-diariz/package.json
src/Diariz.Api/Diariz.Api.csproj              (<Version>)
apps/web/package-lock.json                    (two fields: top level and packages[""])
apps/desktop/package-lock.json                (two fields)
integrations/n8n-nodes-diariz/package-lock.json (two fields)
```

Edit the lock files **by hand**. Regenerating them churns dependency resolution for no reason.

- [ ] **Step 2: Verify the mirrors gate passes**

Run: `cd apps/web && npx vitest run src/lib/versionMirrors.test.ts src/lib/releaseNotes`

Expected: `versionMirrors` PASS; `releases.test.ts` FAILS because `RECENT[0].version` is still `0.260.0`. That is the next step.

- [ ] **Step 3: Add the release entry**

At the top of `RECENT` in `apps/web/src/lib/releaseNotes/current.ts`. Plain hyphens only - `noFancyDashes.test.ts` covers this file:

```typescript
  {
    version: "0.261.0",
    date: "2026-08-28",
    pr: 677,
    headline: "The Actions tab is now a list you choose, not one you inherit",
    summary:
      "Every action item Diariz found was going straight into the Actions tab, across every meeting you have ever recorded. That made it a list of hundreds, most of it minor and much of it somebody else's, and no amount of filtering fixed it - the problem was that things arrived there on their own.\n\nNow they arrive because you put them there. Every action is still extracted from every meeting and still sits on that meeting's page exactly as before, with nothing hidden and nothing lost. Each one has gained a pin, and pinning is what promotes it into the Actions tab and into a folder's Actions tab. Unpin it and it goes back to living on its meeting.\n\nThat means both of those views start empty, which is the point. They fill up with the handful of things you have actually decided to track.",
    added: [
      "A **pin** on every action item, on its meeting's page and in the Actions tab. Pinned actions appear in the cross-meeting Actions views; everything else stays on the meeting it came from.",
    ],
    changed: [
      "The **Actions** tab and a folder's **Actions** tab now list pinned actions only. Every action is still on its own meeting's page, unchanged.",
      "The API's action list is unchanged by default, so existing automations keep seeing everything. Pass `pinned=true` for just the pinned ones.",
    ],
  },
```

Confirm the real PR number before committing - the `pr` field has to be written before `gh pr create` exists to report it, and guessing "last + 1" is unreliable because issues share the sequence. [Issue #676](https://github.com/kenhayward/Diariz/issues/676) took 676, so 677 is the likely PR number, but verify.

- [ ] **Step 4: Update the four feature inventories in lockstep**

`apps/web/src/lib/appInfo.ts`, the **Action items** `CAPABILITIES` row:

```
| **Action items** | Auto-extracted with owner and deadline into an editable table on each meeting. Pin the ones you intend to track and they appear in a cross-meeting Actions list with completion, a person filter, and links back to the transcript; everything else stays on its own meeting. |
```

`README.md` line 35, the same row, same wording.

`docs/features.md`, the action-items bullet: extend the "Across all your meetings" prose to say the cross-meeting list is pinned-only and that pinning happens per action on the meeting page or in the tab.

`docs/Overall_Synopsis_of_Platform.md`, the "Action management (cross-meeting)" section: add `POST /api/actions/pin { ids, pinned }` beside the existing description of `POST /api/actions/complete`, and record that `GET /api/actions` and `GET /api/sections/{id}/actions` take an optional `pinned` filter whose default is deliberately unchanged for the published API.

- [ ] **Step 5: Update the schema doc**

`docs/Data_Schema.md`: add a migration-history row after `AddActionCompletion`:

```
| `AddActionPinned` | `RecordingActions.Pinned` (bool, default false) — an action reaches the cross-meeting Actions views only once pinned |
```

and add `Pinned` to the `RecordingActions` column table.

- [ ] **Step 6: Update the help article**

`apps/web/src/content/help/en/action-items.md`. Its "Across all your meetings" section currently promises that the tab "puts every action item from every meeting in one list", which becomes false. Rewrite it around pinning, and update the front-matter `summary` (that string is what the contextual `?` popover shows, so keep it to two or three sentences). **ASCII only** in this file, enforced by `content/help/helpContent.test.ts`.

- [ ] **Step 7: Regenerate the n8n node and the OpenAPI snapshot**

```bash
cd integrations/n8n-nodes-diariz && npm run generate
```

Then run the .NET suite twice - the OpenAPI snapshot test rewrites its own snapshot, so run 1 fails and run 2 passes with no code change. Commit the regenerated file.

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~OpenApi"
```

`generated/index.ts` does **not** self-heal; if `npm run generate` is skipped, the "n8n community node" check goes red and stays red.

- [ ] **Step 8: Run everything**

```bash
dotnet build Diariz.slnx
```
```bash
dotnet test
```
```bash
cd apps/web && npm run build && npx vitest run
```

Expected: all green, output pristine.

- [ ] **Step 9: Commit**

```bash
git add version.json apps/web/package.json apps/web/package-lock.json apps/desktop/package.json apps/desktop/package-lock.json integrations/n8n-nodes-diariz/package.json integrations/n8n-nodes-diariz/package-lock.json src/Diariz.Api/Diariz.Api.csproj apps/web/src/lib/releaseNotes/current.ts apps/web/src/lib/appInfo.ts README.md docs/features.md docs/Overall_Synopsis_of_Platform.md docs/Data_Schema.md apps/web/src/content/help/en/action-items.md integrations/n8n-nodes-diariz/nodes/Diariz/generated/index.ts tests/Diariz.Api.Tests
git commit -m "chore: release 0.261.0 - pinned action items"
```

---

### Task 12: Live verification and the pull request

**Files:** none - this task verifies and ships.

Layout and behaviour claims cannot be proved in jsdom, which computes no geometry. The pin column changes table widths on two tables, so it needs looking at.

- [ ] **Step 1: Bring up the stack and sign in**

Follow the local docker recipe: the web app uses axios/XHR, so stub-based interception will miss its calls. Rebuild the api container after the .NET changes rather than expecting a hot reload - a stale build will serve the old endpoints while looking fine.

- [ ] **Step 2: Verify the round trip**

- Open a meeting with extracted actions. Confirm **every** action is listed, pinned or not, and the Pin column renders without pushing the table into a horizontal scrollbar.
- Pin one. Open the Actions tab: it appears.
- Unpin it from the Actions tab: the row leaves.
- Open a folder containing that meeting and confirm its Actions tab agrees.
- On an account with nothing pinned, read both empty states and check they say something true and useful.
- Check the browser console is clean.

- [ ] **Step 3: Push and open the PR**

```bash
git push -u origin claude/pinned-action-items
```

PR body must state:

- What changed and why, in user terms.
- **Deployment surface: server redeploy only.** Nothing under `apps/desktop/src`, `apps/desktop/build`, or `electron-builder.config.js` is touched, so no desktop release and no `v*` tag.
- That `GET /api/actions` keeps its current default so no n8n workflow breaks, and that a test guards it.
- No closing keyword. This is an enhancement, not a fix - [issue #676](https://github.com/kenhayward/Diariz/issues/676) is a separate pre-existing bug and is **not** closed by this PR.

- [ ] **Step 4: Watch CI**

Merge only on green. If the batch has other PRs open, merge one at a time - the strict up-to-date policy on `main` means each merge invalidates the others.

---

## Self-review

**Spec coverage:**

| Spec section | Task |
|---|---|
| `RecordingActions.Pinned` column + `AddActionPinned` migration | 1 |
| No `CurrentFormat` bump | 11 (asserted by omission; called out in the PR body) |
| `ActionListItemDto.Pinned` required | 1 |
| `RecordingActionDto.Pinned` trailing defaulted | 4 |
| `GET /api/actions?pinned=` | 1 |
| `GET /api/sections/{id}/actions?pinned=` | 3 |
| `POST /api/actions/pin` | 2 |
| MCP `list_action_items` unchanged | no task - unchanged by construction, and nothing in this plan touches `ListActionItemsTool.cs` |
| Extraction leaves fresh actions unpinned | 4 |
| Merge carries `Pinned` | 5 |
| Web types + client | 7 |
| `ActionsTable` pin column | 8 |
| `ActionsTab` pinned-only + ownership gate | 9 |
| `FolderActionsTable` + `SectionDetail` | 10 |
| Empty states, four locales | 7 |
| Integration tests | 6 |
| Release checklist, docs, n8n | 11 |
| Live verification | 12 |

**Deliberately out of scope, per the spec:** bulk pin from the Actions toolbar, `PinnedAt`, per-user pins, auto-unpin on completion, backfilling existing actions as pinned, and fixing merge dropping `Completed`.

**Type consistency:** `Pinned` (C#) / `pinned` (TS) throughout; `onTogglePin(id, pinned)` in all three components; `pinActions(ids, pinned)` in the client; `PinActionsRequest(Ids, Pinned)` on the wire.
