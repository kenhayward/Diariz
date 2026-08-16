# LLM Usage Logging - PR 3 (admin viewer) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the Platform Administrator a viewer over the LLM usage log - filtered, sorted, with per-column totals over the whole filter, a group-by roll-up, and filtered deletion.

**Architecture:** One admin-only controller exposing three read shapes over one shared filter (operations, calls, summary) plus delete and filter-options endpoints, and one lazy-loaded full-page React route behind a platform-admin gate.

**Tech Stack:** ASP.NET Core (.NET 10), EF Core + Postgres, React 19 + TS + Tailwind v4, vitest + React Testing Library.

**Spec:** `docs/superpowers/specs/2026-08-16-llm-usage-logging-design.md` (the "API" and "UI" sections)
**Predecessors:** PR 1 (0.216.0/0.216.1) captures every call; PR 2 (0.217.0) added streamed tokens, true durations and time-to-first-token. Both merged.

## This is the first user-visible PR of the three

PRs 1 and 2 shipped nothing a non-administrator could see. This one does, so unlike its predecessors it **must** update the README Features table, `docs/features.md`, and the About-box `CAPABILITIES` table - see Task 10.

## Verified facts - do not re-derive these

Checked against the codebase before this plan was written.

1. **There is NO pagination precedent in this API.** `FeedbackController.List` - the closest analogue, an admin-only list behind `ManagePlatform` - returns every row with no paging. This PR introduces the first paged endpoint, so it defines the pattern rather than following one. Do not go looking for a house style; there isn't one.
2. **`/developers/api` is the precedent for a standalone full-page route**: declared in `apps/web/src/App.tsx` (around line 73) inside `RequireAuth` + `Suspense`, lazy-loaded, and linked from `SettingsModal.tsx` (around line 366) with a plain `href`. Follow that shape.
3. **`RequireAuth` checks authentication only, not permission.** The platform-admin gate is `const { isPlatformAdmin } = useAuth()` (`apps/web/src/auth.tsx:57`, surfaced at `:142`), used this way in `ManageMeetingTypesModal.tsx:49`.
4. **`LlmCall`'s per-operation fields are constant by construction** - `Kind`, `UserId`, `UserEmail`, `RecordingId`, `RecordingTitle`, `SectionId`, `SectionName` are all set once when the scope is pushed, and every call in the operation copies them. `Model` and `Endpoint` come from the request but do not vary within one operation. This is what makes the operations-mode group-by safe.
5. **`LlmCalls` has five indexes**: `(StartedAt DESC)`, `(UserId, StartedAt DESC)`, `(OperationId)`, plus EF's FK indexes on `RecordingId` and `SectionId`. Every query this PR adds must be servable from those; adding a sixth index needs a specific justification.

## Global Constraints

- **TDD is mandatory.** Failing test first, run it, watch it fail with the expected message, then implement.
- **Every task includes a mutation check** on its most load-bearing new test: break the production code, observe the named test fail, paste the output, restore. If a test cannot be made to fail, say so plainly. Across PRs 1 and 2 this caught six tests that could not fail, including one guard that had been dead for an entire release.
- **Aggregate behaviour is tested against real Postgres**, never the EF in-memory provider, which does not translate `GROUP BY`/`SUM` faithfully. A unit test of totals would pass without proving anything.
- **Nulls are not zeros.** A model that reports no usage must not drag a total down as though it emitted 0 tokens. Sums ignore nulls, and every token total is accompanied by a count of how many calls actually reported one.
- **Tokens per second is `SUM(CompletionTokens) / SUM(DurationMs)`**, never an average of per-row rates - averaging lets one 3-token 40 ms call outweigh a 4,000-token run.
- **Totals are computed over the whole filter, not the returned page.** That is a separate aggregate query, not a sum of what is on screen.
- **No user-supplied string may reach SQL.** Sorting goes through a whitelist of column names mapped to expressions.
- **`from` defaults to 30 days ago** so no query is ever unbounded. This is the largest table in the database.
- **Never expose prompt or completion content** - the table holds none, and nothing here may join to a source that does.
- **No em/en dashes in user-facing text.** Plain hyphen `-` only. Binds every UI string, every locale catalog, and the release notes.
- **No jest-dom matchers.** Zero of the 230+ files in `apps/web` use them. Plain assertions only; do not install a dependency or edit `src/test-setup.ts`.
- **Recording and folder links must be built through `useRoomBasePath`** - a link built without it silently drops the user into their Personal Room.
- **No mocking library** in .NET. Fakes go in `tests/Diariz.Api.TestSupport`.
- **Never `git add -A`.** Stage explicit paths.
- **Build `Diariz.slnx`** before pushing, not just the unit project.
- **`dotnet test --filter "Name=X"` does not work here.** Use `--filter "FullyQualifiedName~X"`.
- Target version: **`0.218.0`** (functional enhancement: Minor +1, Build reset).
- Deployment surface: **server redeploy only** - nothing under `apps/desktop` is touched.

---

## File Structure

**Create (API):**
- `src/Diariz.Api/Contracts/LlmUsageContracts.cs` - filter, row, totals and summary DTOs
- `src/Diariz.Api/Services/LlmUsageQuery.cs` - pure filter predicate + sort whitelist
- `src/Diariz.Api/Controllers/LlmUsageController.cs`

**Create (web):**
- `apps/web/src/pages/LlmUsage.tsx` - the page
- `apps/web/src/components/llmusage/UsageFilterBar.tsx`
- `apps/web/src/components/llmusage/UsageTable.tsx`
- `apps/web/src/components/llmusage/UsageSummary.tsx`
- plus a `.test.tsx` beside each

**Create (tests):**
- `tests/Diariz.Api.Tests/LlmUsageQueryTests.cs`
- `tests/Diariz.Api.IntegrationTests/LlmUsageViewerIntegrationTests.cs`

**Modify:**
- `src/Diariz.Api/Program.cs` (nothing new expected - the controller is discovered; confirm)
- `apps/web/src/App.tsx`, `apps/web/src/lib/api.ts`, `apps/web/src/lib/types.ts`, `apps/web/src/components/SettingsModal.tsx`, `apps/web/src/locales/*/`

Four small components rather than one page file: this page has a filter bar, three table shapes and a destructive action, and a single file holding all of it would be the largest component in the app.

---

### Task 1: Filter and sort primitives (pure)

**Files:**
- Create: `src/Diariz.Api/Contracts/LlmUsageContracts.cs`, `src/Diariz.Api/Services/LlmUsageQuery.cs`
- Test: `tests/Diariz.Api.Tests/LlmUsageQueryTests.cs`

**Interfaces:**
- Consumes: `LlmCall`, `LlmCallKind`.
- Produces: `record LlmUsageFilter(DateTimeOffset? From, DateTimeOffset? To, Guid[]? UserIds, int[]? Kinds, string[]? Models, string? Outcome, Guid? RecordingId, Guid? SectionId)`; `static class LlmUsageQuery` with `IQueryable<LlmCall> Apply(IQueryable<LlmCall> source, LlmUsageFilter filter, DateTimeOffset nowUtc)` and `bool TryResolveSort(string? sort, out string column)`.

- [ ] **Step 1: Write the failing tests**

These are pure query-shape tests and belong in the unit project. The in-memory provider is adequate here because they exercise *filtering*, not aggregation.

```csharp
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

public class LlmUsageQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    private static LlmCall Row(
        DateTimeOffset startedAt, LlmCallKind kind = LlmCallKind.Summarize, Guid? userId = null,
        string model = "m", bool success = true) => new()
    {
        Id = Guid.NewGuid(), OperationId = Guid.NewGuid(), Sequence = 1, Kind = kind,
        UserId = userId, UserEmail = "u@e.com", Model = model, Endpoint = "http://x/v1",
        StartedAt = startedAt, CompletedAt = startedAt, DurationMs = 1, Success = success,
    };

    private static async Task<List<LlmCall>> QueryAsync(LlmUsageFilter filter, params LlmCall[] rows)
    {
        await using var db = TestDb.Create();
        db.LlmCalls.AddRange(rows);
        await db.SaveChangesAsync();
        return await LlmUsageQuery.Apply(db.LlmCalls, filter, Now).ToListAsync();
    }

    [Fact]
    public async Task DefaultsToTheLastThirtyDays_WhenNoFromIsGiven()
    {
        // The largest table in the database. An unbounded default would make the first page load a
        // full scan, and it would get worse every day the platform runs.
        var recent = Row(Now.AddDays(-3));
        var ancient = Row(Now.AddDays(-31));

        var found = await QueryAsync(new LlmUsageFilter(null, null, null, null, null, null, null, null), recent, ancient);

        Assert.Equal(recent.Id, Assert.Single(found).Id);
    }

    [Fact]
    public async Task AnExplicitFrom_OverridesTheDefaultWindow()
    {
        var ancient = Row(Now.AddDays(-90));

        var found = await QueryAsync(
            new LlmUsageFilter(Now.AddDays(-365), null, null, null, null, null, null, null), ancient);

        Assert.Single(found);
    }

    [Fact]
    public async Task FiltersByUser_Kind_AndModel()
    {
        var userId = Guid.NewGuid();
        var wanted = Row(Now.AddDays(-1), LlmCallKind.ChatMessage, userId, "qwen");
        var wrongUser = Row(Now.AddDays(-1), LlmCallKind.ChatMessage, Guid.NewGuid(), "qwen");
        var wrongKind = Row(Now.AddDays(-1), LlmCallKind.Tags, userId, "qwen");
        var wrongModel = Row(Now.AddDays(-1), LlmCallKind.ChatMessage, userId, "llama");

        var found = await QueryAsync(
            new LlmUsageFilter(null, null, [userId], [(int)LlmCallKind.ChatMessage], ["qwen"], null, null, null),
            wanted, wrongUser, wrongKind, wrongModel);

        Assert.Equal(wanted.Id, Assert.Single(found).Id);
    }

    [Theory]
    [InlineData("failed", false)]
    [InlineData("ok", true)]
    public async Task FiltersByOutcome(string outcome, bool expectedSuccess)
    {
        var ok = Row(Now.AddDays(-1), success: true);
        var failed = Row(Now.AddDays(-1), success: false);

        var found = await QueryAsync(
            new LlmUsageFilter(null, null, null, null, null, outcome, null, null), ok, failed);

        Assert.Equal(expectedSuccess, Assert.Single(found).Success);
    }

    [Fact]
    public async Task OutcomeAll_ReturnsBoth()
    {
        var found = await QueryAsync(
            new LlmUsageFilter(null, null, null, null, null, "all", null, null),
            Row(Now.AddDays(-1), success: true), Row(Now.AddDays(-1), success: false));

        Assert.Equal(2, found.Count);
    }

    [Fact]
    public async Task EmptyFilterArrays_AreIgnoredRatherThanMatchingNothing()
    {
        // A UI that clears its multi-select sends an empty array. Treating that as "match nothing"
        // would show an empty table and look like a bug.
        var found = await QueryAsync(
            new LlmUsageFilter(null, null, [], [], [], null, null, null), Row(Now.AddDays(-1)));

        Assert.Single(found);
    }

    [Theory]
    [InlineData("startedAt")]
    [InlineData("durationMs")]
    [InlineData("totalTokens")]
    public void TryResolveSort_AcceptsWhitelistedColumns(string sort)
    {
        Assert.True(LlmUsageQuery.TryResolveSort(sort, out _));
    }

    [Theory]
    [InlineData("UserEmail; DROP TABLE \"LlmCalls\"")]
    [InlineData("nonsense")]
    [InlineData("")]
    [InlineData(null)]
    public void TryResolveSort_RejectsAnythingElse(string? sort)
    {
        // The only defence that matters here: a sort key is a whitelist lookup, never string
        // interpolation into SQL.
        Assert.False(LlmUsageQuery.TryResolveSort(sort, out _));
    }
}
```

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~LlmUsageQueryTests"
```

Expected: compile failure, `LlmUsageFilter` / `LlmUsageQuery` do not exist.

- [ ] **Step 3: Implement**

`LlmUsageFilter` goes in `src/Diariz.Api/Contracts/LlmUsageContracts.cs`. `LlmUsageQuery.Apply` composes the predicate; treat null AND empty arrays as "no filter". `TryResolveSort` maps a small set of camelCase keys to canonical column names and returns false for everything else, including null and empty.

Whitelist exactly these: `startedAt`, `durationMs`, `promptTokens`, `completionTokens`, `totalTokens`, `kind`, `model`, `userEmail`. Nothing else - and note in a comment that `tokensPerSecond` is deliberately absent because it is derived, not a column.

- [ ] **Step 4: Run to verify they pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~LlmUsageQueryTests"
```

- [ ] **Step 5: Mutation check**

Change `Apply` so an empty array matches nothing (e.g. drop the `.Length > 0` guard). Re-run: `EmptyFilterArrays_AreIgnoredRatherThanMatchingNothing` must FAIL. Paste the output. Restore.

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Contracts/LlmUsageContracts.cs src/Diariz.Api/Services/LlmUsageQuery.cs tests/Diariz.Api.Tests/LlmUsageQueryTests.cs
git commit -m "feat(llm-usage): add the usage-log filter and sort whitelist"
```

---

### Task 2: Totals, against real Postgres

**Files:**
- Modify: `src/Diariz.Api/Services/LlmUsageQuery.cs`, `src/Diariz.Api/Contracts/LlmUsageContracts.cs`
- Test: `tests/Diariz.Api.IntegrationTests/LlmUsageViewerIntegrationTests.cs`

**Interfaces:**
- Produces: `record LlmUsageTotals(int Calls, int Operations, long DurationMs, long? PromptTokens, long? CompletionTokens, long? ReasoningTokens, long? TotalTokens, int TokenMeasuredCalls, int FailedCalls, double? TokensPerSecond)`; `static Task<LlmUsageTotals> TotalsAsync(IQueryable<LlmCall> filtered, CancellationToken ct)`.

- [ ] **Step 1: Write the failing integration tests**

This MUST be an integration test. Aggregation is exactly what the in-memory provider does not translate faithfully.

Create `tests/Diariz.Api.IntegrationTests/LlmUsageViewerIntegrationTests.cs`. Match the `ContainersFixture` injection style and the `IntegrationCollection.Name` convention used by `LlmUsageIntegrationTests.cs`, and **isolate with a unique marker** - all integration tests share one database, and a totals assertion that assumes an otherwise-empty table will go flaky the moment anyone else seeds an `LlmCall`.

```csharp
    [Fact]
    public async Task Totals_SumIgnoresNulls_AndReportsHowManyCallsWereMeasured()
    {
        // A model that reports no usage must not be counted as having emitted 0 tokens - that would
        // silently deflate every average an administrator reads.
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        db.LlmCalls.AddRange(
            Row(marker, durationMs: 1000, completionTokens: 100, totalTokens: 150),
            Row(marker, durationMs: 1000, completionTokens: 300, totalTokens: 450),
            Row(marker, durationMs: 1000, completionTokens: null, totalTokens: null));
        await db.SaveChangesAsync();

        var totals = await LlmUsageQuery.TotalsAsync(db.LlmCalls.Where(c => c.OperationId == marker), default);

        Assert.Equal(3, totals.Calls);
        Assert.Equal(400, totals.CompletionTokens);      // not 400/3 averaged, and the null is not a 0
        Assert.Equal(2, totals.TokenMeasuredCalls);      // "measured on 2 of 3"
    }

    [Fact]
    public async Task Totals_TokensPerSecond_IsSumOverSum_NotAnAverageOfRates()
    {
        // One tiny fast call and one large slow one. Averaging the per-row rates gives ~37.5/s;
        // the correct aggregate is 4003 tokens over 10.04s = ~399/s. This is the assertion that
        // stops the cheap implementation.
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();
        db.LlmCalls.AddRange(
            Row(marker, durationMs: 40, completionTokens: 3),
            Row(marker, durationMs: 10_000, completionTokens: 4000));
        await db.SaveChangesAsync();

        var totals = await LlmUsageQuery.TotalsAsync(db.LlmCalls.Where(c => c.OperationId == marker), default);

        Assert.NotNull(totals.TokensPerSecond);
        Assert.InRange(totals.TokensPerSecond!.Value, 395, 403);
    }

    [Fact]
    public async Task Totals_CountsOperationsDistinctly_AndFailures()
    {
        // Operations is COUNT(DISTINCT OperationId), not COUNT(*) - a three-round-trip chat turn is
        // one operation, and reporting it as three would overstate usage threefold.
        await using var db = fx.CreateDbContext();
        var marker = Guid.NewGuid();          // this test's isolation key, carried on Model
        var turn = Guid.NewGuid();
        db.LlmCalls.AddRange(
            Row(marker, operationId: turn, sequence: 1),
            Row(marker, operationId: turn, sequence: 2),
            Row(marker, operationId: Guid.NewGuid(), success: false));
        await db.SaveChangesAsync();

        var totals = await LlmUsageQuery.TotalsAsync(
            db.LlmCalls.Where(c => c.Model == marker.ToString()), default);

        Assert.Equal(3, totals.Calls);
        Assert.Equal(2, totals.Operations);
        Assert.Equal(1, totals.FailedCalls);
    }

    [Fact]
    public async Task Totals_OfAnEmptySet_AreZeroAndNull_NotAnError()
    {
        await using var db = fx.CreateDbContext();

        var totals = await LlmUsageQuery.TotalsAsync(
            db.LlmCalls.Where(c => c.OperationId == Guid.NewGuid()), default);

        Assert.Equal(0, totals.Calls);
        Assert.Null(totals.TokensPerSecond); // not NaN, not a divide-by-zero
    }
```

Write a `Row(...)` helper local to this file with the marker as `OperationId` unless overridden. Adjust the third test's query so it selects only that test's own rows - the version above is deliberately awkward to show the marker discipline matters; write it cleanly.

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~LlmUsageViewerIntegrationTests"
```

Expected: `TotalsAsync` does not exist. (Docker must be running.)

- [ ] **Step 3: Implement**

`TotalsAsync` runs ONE grouped aggregate query over the filtered set. Guard the division: `TokensPerSecond` is null when `DurationMs` sums to zero or no completion tokens were measured - never `NaN`, never `Infinity`, because those serialize to invalid JSON and would break the page.

- [ ] **Step 4: Run to verify they pass**

- [ ] **Step 5: Mutation check**

Change `TokensPerSecond` to an average of per-row rates. Re-run: `Totals_TokensPerSecond_IsSumOverSum_NotAnAverageOfRates` must FAIL. Paste it. Restore.

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Services/LlmUsageQuery.cs src/Diariz.Api/Contracts/LlmUsageContracts.cs tests/Diariz.Api.IntegrationTests/LlmUsageViewerIntegrationTests.cs
git commit -m "feat(llm-usage): aggregate usage totals over the whole filter"
```

---

### Task 3: The controller - calls and operations modes

**Files:**
- Create: `src/Diariz.Api/Controllers/LlmUsageController.cs`
- Test: `tests/Diariz.Api.IntegrationTests/LlmUsageViewerIntegrationTests.cs`, `tests/Diariz.Api.Tests/LlmUsageControllerTests.cs`

**Interfaces:**
- Produces: `GET /api/admin/llm-usage` with `mode` (`operations` default, `calls`), the filter as query parameters, `sort`, `desc`, `page` (1-based, default 1), `pageSize` (default 50, max 200). Returns `{ rows, page, pageSize, total, totals }`.

- [ ] **Step 1: Write the failing tests**

Authorization first, as a unit test:

```csharp
    [Fact]
    public void Endpoints_RequireManagePlatform_NotMerelyAnAdmin()
    {
        // ReadAdminSettings is held by Administrators too. This log carries every user's activity,
        // so it is Platform Administrator only.
        var attr = typeof(LlmUsageController).GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>().Single();
        Assert.Equal("ManagePlatform", attr.Policy);
    }
```

Then the behaviour, as integration tests: one operation with three calls returns ONE row in `operations` mode with `turns == 3` and summed tokens, and THREE rows in `calls` mode; `pageSize` caps at 200; an unknown `sort` is rejected rather than ignored; `total` counts the whole filter while `rows` holds only the page.

Include this one explicitly:

```csharp
    [Fact]
    public async Task Totals_CoverTheWholeFilter_NotJustTheReturnedPage()
    {
        // The requirement most likely to be implemented wrongly: summing the rows on screen is easy
        // and looks right until the second page.
        // ... seed 5 marked calls, request pageSize: 2 ...
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(5, result.Total);
        Assert.Equal(5, result.Totals.Calls);
    }
```

- [ ] **Step 2: Run to verify they fail**

- [ ] **Step 3: Implement**

`[Authorize(Policy = "ManagePlatform")]` on the class. Bind the filter from query. Apply `LlmUsageQuery.Apply`, then sort via the whitelist (reject an unrecognised `sort` with 400 - silently ignoring it would show the user data ordered differently from what they asked for), then page.

Operations mode groups by the composite key of the fields that are constant per operation - `OperationId`, `Kind`, `UserId`, `UserEmail`, `RecordingId`, `RecordingTitle`, `SectionId`, `SectionName`, `Model` - and aggregates `COUNT(*)` as turns, `MIN(StartedAt)`, `MAX(CompletedAt)`, summed tokens, and `bool_and(Success)` as the outcome. Add a comment recording WHY that composite key is safe: those fields are stamped once when the scope is pushed and copied onto every call in the operation.

`total` is a `COUNT` over the filtered set before paging; `totals` comes from `LlmUsageQuery.TotalsAsync` over the same filtered set. Neither may be derived from the page.

- [ ] **Step 4: Run to verify they pass**, then `dotnet build Diariz.slnx`

- [ ] **Step 5: Mutation check**

Change `totals` to aggregate the paged rows instead of the filtered set. Re-run: `Totals_CoverTheWholeFilter_NotJustTheReturnedPage` must FAIL. Paste it. Restore.

- [ ] **Step 6: Commit**

---

### Task 4: The summary endpoint

**Files:** modify the controller and the integration tests.

**Interfaces:**
- Produces: `GET /api/admin/llm-usage/summary?groupBy=user,model,kind` returning grouped rows plus the same `totals` object.

- [ ] **Step 1: Write the failing tests**

Group by one dimension and by two combined; assert each group's `calls`, `operations`, summed tokens and its own tokens/second; assert an unknown `groupBy` value is rejected (same whitelist discipline as sorting); assert turns are reported as an average and a maximum per operation, never a sum - summing turns across operations is meaningless.

- [ ] **Step 2: Run to verify they fail**
- [ ] **Step 3: Implement.** Reuse `LlmUsageQuery.Apply` for the filter so summary and detail can never disagree about what is in scope.
- [ ] **Step 4: Run to verify they pass**
- [ ] **Step 5: Mutation check:** make the summary build its own filter predicate instead of reusing `Apply`, drop one clause from it, and confirm a test catches the divergence. Paste it. Restore.
- [ ] **Step 6: Commit**

---

### Task 5: Delete and filter options

**Files:** modify the controller and the integration tests.

**Interfaces:**
- Produces: `DELETE /api/admin/llm-usage` (same filter, returns `{ deleted }`); `GET /api/admin/llm-usage/filters` returning distinct users, models and kinds present in the log.

- [ ] **Step 1: Write the failing tests**

Delete removes exactly the rows matching the filter and leaves others; it returns the count; it uses `ExecuteDeleteAsync` (assert by behaviour on a marked set, not by inspecting SQL). Also: **a delete with no filter at all must still be bounded by the default 30-day window** - the same `Apply` the read path uses, so "delete matching these filters" can never mean "delete everything" by accident.

`GET /filters` returns only values actually present, so the dropdowns cannot offer a combination that yields nothing.

- [ ] **Step 2: Run to verify they fail**
- [ ] **Step 3: Implement**
- [ ] **Step 4: Run to verify they pass**
- [ ] **Step 5: Mutation check:** make delete ignore the filter and delete everything; confirm the "leaves others" test FAILS. Paste it. Restore. This is the most destructive endpoint in the feature and the test must be able to catch it.
- [ ] **Step 6: Commit**

---

### Task 6: Web API client and types

**Files:**
- Modify: `apps/web/src/lib/api.ts`, `apps/web/src/lib/types.ts`
- Test: `apps/web/src/lib/api.test.ts` if one exists for this shape; otherwise coverage comes via the component tests in Tasks 7-8.

- [ ] **Step 1: Add the types** mirroring the API DTOs exactly - rows, totals, summary rows, filter options. Nullable token fields must be `number | null`, not `number | undefined`, so "not measured" survives the round trip and cannot be confused with zero.
- [ ] **Step 2: Add the client methods** to the `api` object following the file's existing style: `getLlmUsage(params)`, `getLlmUsageSummary(params)`, `deleteLlmUsage(params)`, `getLlmUsageFilters()`.
- [ ] **Step 3: Typecheck.** `cd apps/web && npm run build`
- [ ] **Step 4: Commit**

---

### Task 7: The page shell, filter bar and table

**Files:**
- Create: `apps/web/src/pages/LlmUsage.tsx`, `apps/web/src/components/llmusage/UsageFilterBar.tsx`, `apps/web/src/components/llmusage/UsageTable.tsx` + tests
- Modify: `apps/web/src/App.tsx`, `apps/web/src/components/SettingsModal.tsx`, `apps/web/src/locales/*/`

- [ ] **Step 1: Write the failing component tests**

Follow the established pattern: `vi.mock` of `../lib/api`, render inside `MemoryRouter` + `QueryClientProvider`. **Plain assertions - no jest-dom.**

Cover: the page requests with the expected params; changing a filter refetches with the new params; clicking a sortable header toggles direction and refetches; the totals row renders the API's totals rather than a sum of the visible rows (seed a page whose visible rows sum to something *different* from the totals, and assert the rendered figure matches the totals - a test where they coincide proves nothing); a non-admin sees a refusal rather than the table.

- [ ] **Step 2: Run to verify they fail**

```bash
cd apps/web && npx vitest run src/pages/LlmUsage.test.tsx
```

- [ ] **Step 3: Implement**

Route at `/admin/llm-usage` in `App.tsx`, lazy-loaded inside `RequireAuth` + `Suspense`, exactly like `/developers/api`. `RequireAuth` checks authentication only, so the page itself gates on `const { isPlatformAdmin } = useAuth()` and renders a short refusal when false. Link it from the Model Settings (AI) tab of `SettingsModal.tsx` the way `/developers/api` is linked.

Filter bar: date presets (24 h / 7 d / 30 d / custom), user / type / model multi-selects populated from `GET /filters`, outcome toggle. Table: sortable headers, a sticky totals row, pagination. Every token total displays its "measured on N of M calls" qualifier - a total that silently covers only part of the set is worse than no total.

Recording and folder links go through `useRoomBasePath`. Render them unconditionally: a Platform Administrator has no automatic access to another user's recording, so some will 403, and pre-checking access per row costs more than it saves.

All strings via i18n, in every locale file. Plain hyphens only.

- [ ] **Step 4: Run to verify they pass**, plus `npm run build`
- [ ] **Step 5: Mutation check:** change the totals row to sum the visible rows. Confirm the totals test FAILS. Paste it. Restore.
- [ ] **Step 6: Commit**

---

### Task 8: Summary mode and filtered delete

**Files:**
- Create: `apps/web/src/components/llmusage/UsageSummary.tsx` + test
- Modify: `apps/web/src/pages/LlmUsage.tsx` and its test

- [ ] **Step 1: Write the failing tests**

Mode tabs switch between Operations, Calls and Summary and call the right endpoint; group-by chips change the `groupBy` parameter; the delete button opens a confirm that **states the exact row count** before proceeding; confirming calls `deleteLlmUsage` with the CURRENT filter (not an empty one - seed a non-default filter and assert it is passed through); cancelling calls nothing.

That last pair matters more than it looks: a delete that quietly drops the filter would destroy far more than the administrator asked for.

- [ ] **Step 2: Run to verify they fail**
- [ ] **Step 3: Implement.** The confirm dialog must show the count from the current query's `total`, and the destructive action must be visually distinct.
- [ ] **Step 4: Run to verify they pass**, plus `npm run build`
- [ ] **Step 5: Mutation check:** make the delete call send an empty filter. Confirm the pass-through test FAILS. Paste it. Restore.
- [ ] **Step 6: Commit**

---

### Task 9: Live verification

**Files:** none changed unless a defect is found.

The stack is the user's live local environment. Rebuild **only** the `api` service. Do not touch postgres, redis, minio, worker, web, or any `glitchtip*` / `hawser` / `portainer*` container. Do not run `docker compose down` or remove volumes. **Do not delete any usage rows that you did not create** - the delete endpoint is under test, so create your own marked rows to delete and leave the real history alone.

- [ ] **Step 1:** `dotnet build Diariz.slnx && dotnet test`, then `cd apps/web && npx vitest run && npm run build`. All green, no warnings.
- [ ] **Step 2:** `cd deploy && docker compose up -d --build api`
- [ ] **Step 3:** Open `/admin/llm-usage` as the platform admin. Confirm: real rows appear; operations mode shows one row per turn with a sensible turn count; switching to calls mode expands them; the totals row matches a hand-checked `psql` aggregate over the same window; sorting works on every whitelisted column.
- [ ] **Step 4:** Confirm a **non-admin** account gets refused - both the page and a direct API call.
- [ ] **Step 5:** Insert a handful of rows with a marker you control, delete them through the UI, confirm the confirm dialog stated the right count and that only your rows went.
- [ ] **Step 6:** Check the page loads in reasonable time against the real table size, and note the row count and load time in your report. If it is slow, say so with numbers rather than adding an index reflexively.

---

### Task 10: Docs, version bump and release notes

**Unlike PRs 1 and 2, this PR IS user-visible.** All of the following apply.

- [ ] **Step 1:** Bump to **0.218.0** in `version.json` and all four mirrors (`apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`).
- [ ] **Step 2:** Add the `RELEASES[0]` entry (version, date, real PR number, headline, prose summary, `added` list). Plain hyphens only.
- [ ] **Step 3:** Update the About-box **`CAPABILITIES`** table row for admin controls to mention the usage viewer.
- [ ] **Step 4:** Update the **README Features** table row and the matching **`docs/features.md`** bullet - in lockstep, never one without the other.
- [ ] **Step 5:** Update **`docs/Overall_Synopsis_of_Platform.md`** with the new admin surface and its endpoints.
- [ ] **Step 6:** `docs/Data_Schema.md` needs no change - no schema change in this PR. Say so explicitly in the PR body so a reviewer knows it was considered.
- [ ] **Step 7:** Consider whether a **help article** belongs under `apps/web/src/content/help/` - this is a new admin-facing screen, and the help content is task-oriented "how do I" prose for users inside the app. Judge it and state your reasoning either way in the report; if you add one, it is ASCII only with the required `title`/`summary`/`group`/`order` front matter, and `helpContent.test.ts` enforces that.
- [ ] **Step 8:** Verify `cd apps/web && npx vitest run src/lib/versionMirrors.test.ts src/lib/releases.test.ts` and `npm run build`.
- [ ] **Step 9:** Push, `gh pr create`, read the REAL PR number, amend the `releases.ts` entry, push again. Do not guess the number - Dependabot PRs and issues share the sequence.
- [ ] **Step 10:** The PR body states: **server redeploy only**; **no schema change or migration**; that this is the first user-visible PR of the three; and the row count and page load time measured in Task 9.

---

## Self-Review

**Spec coverage:** the spec's API section calls for one shared filter with a 30-day default (Task 1), totals over the whole filter with null-aware sums and `SUM/SUM` tokens-per-second (Task 2), operations/calls modes with paging and a sort whitelist (Task 3), a multi-dimension summary (Task 4), filtered delete and filter options (Task 5), and a dedicated admin page with filter bar, three modes, sticky totals and a counted delete confirm (Tasks 6-8). Live verification is Task 9; the full release checklist, which this PR triggers in full for the first time, is Task 10.

**Deliberate non-goals:** no schema change; no new index unless Task 9 produces numbers justifying one; no per-user (non-admin) view of usage; no cost/currency modelling.

**Type consistency:** `LlmUsageFilter`, `LlmUsageQuery.Apply`/`.TryResolveSort`/`.TotalsAsync`, and `LlmUsageTotals` are each defined once and reused by every endpoint - which is also what stops the summary and detail views disagreeing about what is in scope.

**Known adaptation points:** the `ContainersFixture` injection style and marker-isolation convention in the existing `LlmUsageIntegrationTests.cs` (Task 2); the existing multi-select and table idioms in `apps/web/src/components` (Task 7); whether an `api.test.ts` exists for the client shape (Task 6).
