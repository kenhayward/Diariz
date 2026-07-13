# Formulas Phase D - Shared Formulas (Implementation Plan)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this
> plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Let a user mark a Personal formula **Shared**; other users **discover** it (sharer avatar + name,
formula name/description, read-only prompt), **add** it to their collection (a live link, not a copy), run it
(with their own LLM config), and **remove** it. Deleting the formula (or the subscriber) cascade-removes the
link. A new "Shared Formulas" group appears in the run picker.

**Architecture:** New schema (`Formula.Shared` + a `FormulaSubscription` link table + one additive migration).
`FormulasController` gains discovery + subscribe/unsubscribe endpoints and extends its visibility query;
`FormulaRunner` extends Personal run-access to owner-OR-subscribed. Web adds a "Share this Formula" checkbox,
a "Shared Formulas" run-picker group, and a discovery browser.

**Tech Stack:** .NET 10 + EF Core/Postgres (Testcontainers integration); React 19/TS + RTL/vitest.

**Release:** Minor bump `0.131.0` -> `0.132.0`. **Deployment:** server redeploy (API + web). **One additive
migration** (new column + new table) - forward-restore-safe, so **no `MaintenanceController.CurrentFormat`
bump**. No desktop release.

---

## Design decisions (locked)
- A Personal formula is shared platform-wide when `Shared = true`; discovery exposes its prompt/description +
  owner name/avatar to any authed user (the confirmed intent).
- Owner edits **propagate live** to subscribers (it's a link). Un-sharing (`Shared=false`) hides it from
  discovery + the run list and blocks running; existing subscription rows are left inert (re-sharing restores
  them). Deleting the formula cascade-removes all subscription rows.
- Run access for a Personal formula: **owner OR (Shared AND the caller has a subscription)**. A shared but
  un-subscribed formula stays **404** on run (existing leak-avoidance convention); it's added via the
  discovery browser, not run directly.
- No new origin "kind" needed: a subscribed shared formula is `scope=Personal` owned by the sharer, so the
  Phase-C origin resolver already renders the **sharer's** avatar on its result rows. (The `"shared"` value in
  the TS union stays unused - harmless.)

## Files
**Server:** `src/Diariz.Domain/Entities/Formula.cs` (+`Shared`), `FormulaSubscription.cs` (new),
`DiarizDbContext.cs`, a new migration; `src/Diariz.Api/Contracts/ApiDtos.cs`,
`Controllers/FormulasController.cs`, `Services/FormulaRunner.cs`.
**Server tests:** `tests/Diariz.Api.Tests/FormulasControllerTests.cs`, `FormulaRunnerTests.cs`;
`tests/Diariz.Api.IntegrationTests/FormulasIntegrationTests.cs`.
**Web:** `lib/types.ts`, `lib/api.ts`, `auth.tsx`, `components/FormulaEditModal.tsx`, `FormulaRunModal.tsx`,
`SharedFormulasBrowser.tsx` (new), `pages/RecordingDetail.tsx`, `locales/{en,de,es,fr}/{account,workspace}.json`.
**Web tests:** `FormulaEditModal.test.tsx`, `FormulaRunModal.test.tsx`, `SharedFormulasBrowser.test.tsx`.
**Docs:** `Data_Schema.md`, `Overall_Synopsis_of_Platform.md`, `README.md`, `docs/features.md`, `releases.ts`
+ version files.

---

## Task D1: Schema - `Formula.Shared` + `FormulaSubscription` + migration

**Files:** `src/Diariz.Domain/Entities/Formula.cs`, `src/Diariz.Domain/Entities/FormulaSubscription.cs` (new),
`src/Diariz.Domain/DiarizDbContext.cs`, a generated migration.

- [ ] **Step 1: Add `Shared` to the Formula entity**

In `src/Diariz.Domain/Entities/Formula.cs`, after `Enabled` (L16) add:
```csharp
    /// <summary>Only meaningful for Personal scope: when true, other users can discover this formula and
    /// subscribe to it (a live link, not a copy). Deleting the formula cascade-removes their subscriptions.</summary>
    public bool Shared { get; set; }
```

- [ ] **Step 2: Create the link entity**

Create `src/Diariz.Domain/Entities/FormulaSubscription.cs`:
```csharp
namespace Diariz.Domain.Entities;

/// <summary>A user's link to another user's shared Personal formula (a live pointer, not a copy). Lets the
/// subscriber run it and see it under "Shared Formulas" in the run picker; the owner's edits propagate.
/// Deleting the formula OR the subscriber cascade-removes the link. Unique per (FormulaId, UserId).</summary>
public class FormulaSubscription
{
    public Guid Id { get; set; }
    public Guid FormulaId { get; set; }
    public Formula? Formula { get; set; }
    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
```

- [ ] **Step 3: DbSet + config (outside the Npgsql guard)**

In `src/Diariz.Domain/DiarizDbContext.cs`, add the DbSet next to the formula ones (L41-42):
```csharp
    public DbSet<FormulaSubscription> FormulaSubscriptions => Set<FormulaSubscription>();
```
And add config right after the `Formula` block (after L292), OUTSIDE the `IsNpgsql()` guard (a plain unique
index is fine there; only *filtered* indexes must be gated):
```csharp
        // A subscriber's live link to a shared Personal formula. Two cascade paths (formula + user) are safe
        // on Postgres (see the FormulaResult block). Unique per (FormulaId, UserId) so a user can't add the
        // same formula twice; the controller is also idempotent.
        builder.Entity<FormulaSubscription>(e =>
        {
            e.HasIndex(s => new { s.FormulaId, s.UserId }).IsUnique();
            e.HasOne(s => s.Formula).WithMany()
                .HasForeignKey(s => s.FormulaId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(s => s.User).WithMany()
                .HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });
```

- [ ] **Step 4: Generate the migration**

Run:
```bash
dotnet ef migrations add AddFormulaSharing --project src/Diariz.Domain --startup-project src/Diariz.Api
```
Then OPEN the generated `src/Diariz.Domain/Migrations/*_AddFormulaSharing.cs` and verify `Up()` contains:
- `AddColumn<bool>(name: "Shared", table: "Formulas", nullable: false, defaultValue: false)`.
- `CreateTable("FormulaSubscriptions", ...)` with `Id` (uuid, PK), `FormulaId` (uuid), `UserId` (uuid),
  `CreatedAt` (timestamptz), a FK to `Formulas` **onDelete: Cascade**, a FK to `AspNetUsers` **onDelete:
  Cascade**.
- A **unique** index `IX_FormulaSubscriptions_FormulaId_UserId` (`unique: true`).
- `Down()` drops the table then the column.
If the generated FKs are not both `Cascade`, fix the entity config (Step 3) and regenerate (delete the bad
migration first). Confirm `DiarizDbContextModelSnapshot.cs` was updated.

- [ ] **Step 5: Build**

Run: `dotnet build Diariz.slnx` -> succeeds. (The API auto-applies migrations on boot; no manual DB update.)

- [ ] **Step 6: Commit**
```bash
git add src/Diariz.Domain
git commit -m "feat(formulas): schema for shared formulas - Formula.Shared + FormulaSubscription link table"
```

---

## Task D2: API - DTOs + controller (visibility, share flag, discovery, subscribe) (TDD)

**Files:** `src/Diariz.Api/Contracts/ApiDtos.cs`, `src/Diariz.Api/Controllers/FormulasController.cs`,
`tests/Diariz.Api.Tests/FormulasControllerTests.cs`.

- [ ] **Step 1: DTOs**

In `src/Diariz.Api/Contracts/ApiDtos.cs`:
- Add `Shared` to `FormulaDto` (append, keep positional order):
```csharp
public record FormulaDto(
    Guid Id, string Scope, Guid? OwnerUserId, string Name, string? Description, string Prompt,
    int Context, bool Enabled, bool IsBuiltIn, bool Shared);
```
- Add `Shared` as a **trailing optional** on the request records (so existing positional test call-sites keep
  compiling):
```csharp
public record CreateFormulaRequest(string Scope, string Name, string? Description, string Prompt, int Context, bool Shared = false);
public record UpdateFormulaRequest(string? Name, string? Description, string? Prompt, int? Context, bool? Shared = null);
```
- Add the discovery DTO near `FormulaDto`:
```csharp
/// <summary>A formula shared by another user, for the discovery browser: the formula, the owner's display +
/// avatar (name falls back to email), and whether the caller has already added it.</summary>
public record SharedFormulaDto(FormulaDto Formula, string? OwnerName, string? OwnerPictureUrl, bool AlreadyAdded);
```

- [ ] **Step 2: Failing controller tests**

Add to `tests/Diariz.Api.Tests/FormulasControllerTests.cs` (mirror its existing `Http.Context(userId)` +
`TestDb` style; check the top of the file for the exact helpers). Cover:

```csharp
// (sketch - adapt to the file's existing helpers/naming)
[Fact] public async Task Create_personal_sets_shared_when_requested() { /* Create Personal with Shared=true -> dto.Shared true */ }
[Fact] public async Task Create_platform_ignores_shared() { /* Platform scope + Shared=true -> stored Shared=false */ }
[Fact] public async Task Update_toggles_shared_for_owner() { /* owner sets Shared true then false */ }
[Fact] public async Task List_includes_a_subscribed_shared_formula_and_excludes_unsubscribed() {
    // owner A creates a shared Personal formula; user B with a subscription sees it in List; without one, doesn't.
}
[Fact] public async Task Shared_lists_others_shared_formulas_with_owner_and_alreadyAdded() {
    // B's GET shared shows A's shared formula (not B's own, not non-shared), OwnerName = A's FullName, AlreadyAdded reflects a subscription.
}
[Fact] public async Task Subscribe_adds_link_is_idempotent_and_404s_for_own_or_nonshared() { }
[Fact] public async Task Unsubscribe_removes_link_and_is_idempotent() { }
```

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~FormulasController"` -> new tests FAIL
to compile/pass.

- [ ] **Step 3: Controller - visibility, share flag, endpoints**

In `src/Diariz.Api/Controllers/FormulasController.cs`:

- **`ToDto`** (L228-230) - add `f.Shared`:
```csharp
    private static FormulaDto ToDto(Formula f) => new(
        f.Id, f.Scope.ToString(), f.OwnerUserId, f.Name, f.Description, f.Prompt,
        (int)f.Context, f.Enabled, f.IsBuiltIn, f.Shared);
```

- **`List`** (L46-55) - extend the `Where` to include subscribed shared formulas:
```csharp
        var formulas = await _db.Formulas
            .Where(f => (f.Scope == FormulaScope.Personal && f.OwnerUserId == userId)
                     || (f.Scope != FormulaScope.Personal && f.Enabled)
                     || (f.Scope == FormulaScope.Personal && f.Shared
                         && _db.FormulaSubscriptions.Any(s => s.FormulaId == f.Id && s.UserId == userId)))
            .ToListAsync();
        return formulas.Select(ToDto).ToList();
```

- **`Create`** (L79-105) - set `Shared` (Personal only). Where it builds `new Formula { ... }`, add:
```csharp
                Shared = scope == FormulaScope.Personal && req.Shared,
```

- **`Update`** (L110-135) - after the field updates, before `SaveChanges`, add:
```csharp
        if (req.Shared is not null && formula.Scope == FormulaScope.Personal) formula.Shared = req.Shared.Value;
```

- **New endpoints** (add near the other actions; `UserId`/`_db`/`Forbidden` already exist):
```csharp
    /// <summary>Formulas shared by OTHER users, for the discovery browser. Any authed user; excludes the
    /// caller's own. Includes the owner's display (name falls back to email) + avatar and whether the caller
    /// has already added it.</summary>
    [HttpGet("shared")]
    public async Task<ActionResult<IReadOnlyList<SharedFormulaDto>>> Shared()
    {
        var userId = UserId;
        var shared = await _db.Formulas
            .Where(f => f.Scope == FormulaScope.Personal && f.Shared && f.OwnerUserId != userId)
            .OrderBy(f => f.Name)
            .ToListAsync();

        var ownerIds = shared.Where(f => f.OwnerUserId != null).Select(f => f.OwnerUserId!.Value).Distinct().ToList();
        var owners = (await _db.Users.Where(u => ownerIds.Contains(u.Id))
                .Select(u => new { u.Id, u.FullName, u.Email, u.PictureUrl }).ToListAsync())
            .ToDictionary(u => u.Id, u => (u.FullName, u.Email, u.PictureUrl));

        var sharedIds = shared.Select(f => f.Id).ToList();
        var mine = (await _db.FormulaSubscriptions
                .Where(s => s.UserId == userId && sharedIds.Contains(s.FormulaId))
                .Select(s => s.FormulaId).ToListAsync())
            .ToHashSet();

        return shared.Select(f =>
        {
            string? name = null, pic = null;
            if (f.OwnerUserId is Guid oid && owners.TryGetValue(oid, out var o))
            {
                name = string.IsNullOrWhiteSpace(o.FullName) ? o.Email : o.FullName;
                pic = o.PictureUrl;
            }
            return new SharedFormulaDto(ToDto(f), name, pic, mine.Contains(f.Id));
        }).ToList();
    }

    /// <summary>Add a shared Personal formula (owned by someone else) to the caller's collection. Idempotent.
    /// 404 for a missing / non-shared / non-Personal / own formula (leak-avoidance).</summary>
    [HttpPost("{id:guid}/subscribe")]
    public async Task<IActionResult> Subscribe(Guid id)
    {
        var userId = UserId;
        var f = await _db.Formulas.FirstOrDefaultAsync(x => x.Id == id);
        if (f is null || f.Scope != FormulaScope.Personal || !f.Shared || f.OwnerUserId == userId)
            return NotFound();

        if (!await _db.FormulaSubscriptions.AnyAsync(s => s.FormulaId == id && s.UserId == userId))
        {
            _db.FormulaSubscriptions.Add(new FormulaSubscription
            {
                Id = Guid.NewGuid(), FormulaId = id, UserId = userId, CreatedAt = DateTimeOffset.UtcNow,
            });
            await _db.SaveChangesAsync();
        }
        return NoContent();
    }

    /// <summary>Remove the caller's link to a shared formula. Idempotent.</summary>
    [HttpDelete("{id:guid}/subscribe")]
    public async Task<IActionResult> Unsubscribe(Guid id)
    {
        var userId = UserId;
        var sub = await _db.FormulaSubscriptions.FirstOrDefaultAsync(s => s.FormulaId == id && s.UserId == userId);
        if (sub is not null)
        {
            _db.FormulaSubscriptions.Remove(sub);
            await _db.SaveChangesAsync();
        }
        return NoContent();
    }
```
Add `using Diariz.Domain.Entities;` if `FormulaSubscription` isn't already in scope.

- [ ] **Step 4: Green + build**

Run: `dotnet build Diariz.slnx` -> succeeds.
Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~FormulasController"` -> PASS.

- [ ] **Step 5: Commit**
```bash
git add src/Diariz.Api/Contracts/ApiDtos.cs src/Diariz.Api/Controllers/FormulasController.cs tests/Diariz.Api.Tests/FormulasControllerTests.cs
git commit -m "feat(formulas): share flag, subscribed-visibility, discovery + subscribe/unsubscribe endpoints"
```

---

## Task D3: API - run access for shared formulas (TDD)

**Files:** `src/Diariz.Api/Services/FormulaRunner.cs`, `tests/Diariz.Api.Tests/FormulaRunnerTests.cs`.

- [ ] **Step 1: Failing runner tests**

Add to `tests/Diariz.Api.Tests/FormulaRunnerTests.cs` (mirror its existing setup - it fakes the LLM and uses
`TestDb`):
- a **shared + subscribed** Personal formula owned by A runs for B (produces a result);
- a **shared + un-subscribed** Personal formula owned by A throws `FormulaNotFoundException` for B;
- a **non-shared** non-owned Personal formula still throws `FormulaNotFoundException` (unchanged);
- the owner still runs their own.

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~FormulaRunner"` -> the new shared+subscribed
case FAILS (currently NotFound for any non-owner).

- [ ] **Step 2: Extend the access rule**

In `src/Diariz.Api/Services/FormulaRunner.cs` `RunAsync`, after the formula is loaded (~L49) and before/at the
`EnsureCanRun` call, compute the subscription and pass it in:
```csharp
        var subscribed = formula.Scope == FormulaScope.Personal
            && formula.OwnerUserId != userId
            && formula.Shared
            && await _db.FormulaSubscriptions.AnyAsync(s => s.FormulaId == formula.Id && s.UserId == userId, ct);
        EnsureCanRun(formula, userId, subscribed);
```
And update `EnsureCanRun` (L100-111):
```csharp
    private static void EnsureCanRun(Formula formula, Guid userId, bool subscribed)
    {
        if (formula.Scope == FormulaScope.Personal)
        {
            // Owner, or a subscriber to a shared formula. Otherwise hide its existence (404) - it's added via
            // the discovery browser, not run directly.
            if (formula.OwnerUserId != userId && !subscribed)
                throw new FormulaNotFoundException("Formula not found.");
        }
        else if (!formula.Enabled)
        {
            throw new FormulaAccessException("This formula is disabled.");
        }
    }
```

- [ ] **Step 3: Green + build**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~FormulaRunner"` -> PASS.
Run: `dotnet build Diariz.slnx` -> succeeds.

- [ ] **Step 4: Commit**
```bash
git add src/Diariz.Api/Services/FormulaRunner.cs tests/Diariz.Api.Tests/FormulaRunnerTests.cs
git commit -m "feat(formulas): a subscriber can run a shared formula (owner-or-subscribed run access)"
```

---

## Task D4: Integration tests - cascade + unique index (needs Docker)

**Files:** `tests/Diariz.Api.IntegrationTests/FormulasIntegrationTests.cs`.

Mirror the existing "User-cascade of a Personal formula" test (~L209-256) and the model's other cascade tests.

- [ ] **Step 1: Add tests (real Postgres)**
- Deleting a shared `Formula` **cascade-removes** its `FormulaSubscription` rows (the core requirement).
- Deleting a subscriber `ApplicationUser` **cascade-removes** their `FormulaSubscription` rows (keeps the
  formula + owner).
- The **unique index** rejects a duplicate `(FormulaId, UserId)` (second `SaveChanges` throws
  `DbUpdateException`) - mirrors `UserGroupsIntegrationTests.GroupName_IsUnique`.

- [ ] **Step 2: Run (if Docker is available)**

Run: `dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~Formulas"` -> PASS.
(If Docker isn't available in this environment, still ensure the project **compiles** via `dotnet build
Diariz.slnx`, and note the tests weren't executed for the coordinator to run.)

- [ ] **Step 3: Commit**
```bash
git add tests/Diariz.Api.IntegrationTests/FormulasIntegrationTests.cs
git commit -m "test(formulas): integration - FormulaSubscription cascades + unique (formula,user)"
```

---

## Task D5: Web - types, api, caller id, share checkbox (TDD)

**Files:** `apps/web/src/lib/types.ts`, `apps/web/src/lib/api.ts`, `apps/web/src/auth.tsx`,
`apps/web/src/components/FormulaEditModal.tsx`, `apps/web/src/components/FormulaEditModal.test.tsx`,
`apps/web/src/locales/{en,de,es,fr}/account.json`.

- [ ] **Step 1: Types**

In `apps/web/src/lib/types.ts`: add `shared: boolean;` to the `Formula` interface; add:
```ts
/// A formula shared by another user, for the discovery browser (mirrors SharedFormulaDto).
export interface SharedFormula {
  formula: Formula;
  ownerName: string | null;
  ownerPictureUrl: string | null;
  alreadyAdded: boolean;
}
```

- [ ] **Step 2: API wrappers**

In `apps/web/src/lib/api.ts`: add `shared?: boolean` to the `createFormula` and `updateFormula` body types
(pass it through), and add:
```ts
  async listSharedFormulas(): Promise<SharedFormula[]> {
    const { data } = await http.get<SharedFormula[]>("/api/formulas/shared");
    return data;
  },
  async subscribeFormula(id: string): Promise<void> {
    await http.post(`/api/formulas/${id}/subscribe`);
  },
  async unsubscribeFormula(id: string): Promise<void> {
    await http.delete(`/api/formulas/${id}/subscribe`);
  },
```
(Import `SharedFormula` in the api's type imports.)

- [ ] **Step 3: Expose the caller's id on auth**

In `apps/web/src/auth.tsx`: `userIdFromToken` already exists in `./lib/jwt`. Import it, add `id: string | null`
to `AuthState`, and populate it from the token alongside `email`/`pictureUrl` (`id: userIdFromToken(token)`).

- [ ] **Step 4: Share-checkbox test (red)**

Add to `apps/web/src/components/FormulaEditModal.test.tsx` (create if missing; use native assertions - NO
jest-dom): the checkbox renders for a Personal create, does NOT render for `scope="Platform"`, and toggling it
sends `shared: true` in the create payload (mock `api.createFormula`).

Run: `cd apps/web && npx vitest run src/components/FormulaEditModal.test.tsx` -> FAIL.

- [ ] **Step 5: Add the checkbox**

In `apps/web/src/components/FormulaEditModal.tsx`:
- `const [shared, setShared] = useState(formula?.shared ?? false);`
- `const isPersonal = formula ? formula.scope === "Personal" : scope === "Personal";`
- Include `shared` in both payloads: `api.updateFormula(id, { ..., shared })` and
  `api.createFormula({ ..., shared })`.
- Render, only when `isPersonal`, below the context block:
```tsx
        {isPersonal && (
          <label className="flex items-start gap-2 text-sm dark:text-gray-200">
            <input type="checkbox" checked={shared} onChange={(e) => setShared(e.target.checked)} className="mt-0.5" />
            <span>
              <span className="font-medium">{t("formulaShared")}</span>
              <span className="block text-xs text-gray-400 dark:text-gray-500">{t("formulaSharedHint")}</span>
            </span>
          </label>
        )}
```

- [ ] **Step 6: i18n (account.json, all 4 locales)** - add `formulaShared` + `formulaSharedHint`:
  - en: `Share this formula` / `Other people can find and add this formula. They can run it but not edit it, and your later changes apply to them too.`
  - de/es/fr: translate; keep "formula" consistent with the existing catalog; no em/en dashes.

- [ ] **Step 7: Green + build + commit**
```bash
cd apps/web && npx vitest run src/components/FormulaEditModal.test.tsx   # PASS
cd apps/web && npm run build                                             # tsc: flag any Formula fixture missing `shared` -> add it
git add apps/web/src/lib/types.ts apps/web/src/lib/api.ts apps/web/src/auth.tsx apps/web/src/components/FormulaEditModal.tsx apps/web/src/components/FormulaEditModal.test.tsx apps/web/src/locales
git commit -m "feat(formulas): Share this Formula checkbox + shared-formula api/types + caller id on auth"
```

---

## Task D6: Web - run-picker "Shared" group + discovery browser (TDD)

**Files:** `apps/web/src/components/FormulaRunModal.tsx`, `apps/web/src/components/SharedFormulasBrowser.tsx`
(new) + test, `apps/web/src/pages/RecordingDetail.tsx`, `apps/web/src/locales/{en,de,es,fr}/workspace.json`.

- [ ] **Step 1: i18n (workspace.json, all 4 locales)** - add:
  `findSharedFormulas` (`Find shared formulas`), `formulaScopeShared` (`Shared`), `sharedFormulasTitle`
  (`Shared formulas`), `sharedFormulasEmpty` (`No one has shared a formula yet.`), `noSharedFormulaMatches`
  (`No shared formulas match.`), `errLoadSharedFormulas` (`Couldn't load shared formulas.`),
  `addSharedFormula` (`Add`), `removeSharedFormula` (`Remove`), `sharedFormulaAdded` (`Added`),
  `sharedFormulaBy` (`Shared by {{name}}`), `viewFormula` (`View`), `hideFormula` (`Hide`),
  `errSubscribeFormula` (`Couldn't update your shared formulas.`). No em/en dashes.

- [ ] **Step 2: Run-picker "Shared" group + Find button**

In `apps/web/src/components/FormulaRunModal.tsx`:
- Add an `onFindShared?: () => void` prop.
- Import `useAuth` and read the caller id: `const { id: myId } = useAuth();`.
- Replace the scope-only grouping with a 4-group model. Add a "Shared" group for Personal formulas owned by
  someone else:
```tsx
  const groups = useMemo(() => {
    const g = (key: string, items: Formula[]) => ({ scope: key, items });
    return [
      g("Diariz", filtered.filter((f) => f.scope === "Diariz")),
      g("Platform", filtered.filter((f) => f.scope === "Platform")),
      g("Personal", filtered.filter((f) => f.scope === "Personal" && f.ownerUserId === myId)),
      g("Shared", filtered.filter((f) => f.scope === "Personal" && f.ownerUserId !== myId)),
    ].filter((x) => x.items.length > 0);
  }, [filtered, myId]);
```
- Extend `scopeLabel` with `Shared: t("workspace:formulaScopeShared")` (type the record as `Record<string,
  string>`).
- In the footer (next to the existing "Manage formulas" button), add:
```tsx
          <button type="button" onClick={() => onFindShared?.()} className="text-sm text-blue-600 hover:underline dark:text-blue-400">
            {t("workspace:findSharedFormulas")}
          </button>
```

- [ ] **Step 3: Discovery browser test (red)**

Create `apps/web/src/components/SharedFormulasBrowser.test.tsx` (RTL, native assertions, mock `api`): lists a
shared formula with its owner name; clicking Add calls `api.subscribeFormula(id)`; an already-added row shows
Remove and calls `api.unsubscribeFormula(id)`; empty state renders `sharedFormulasEmpty`.

Run: `cd apps/web && npx vitest run src/components/SharedFormulasBrowser.test.tsx` -> FAIL.

- [ ] **Step 4: Build the discovery browser**

Create `apps/web/src/components/SharedFormulasBrowser.tsx` - model the shell on `FormulaRunModal` (backdrop,
searchbox, scroll list, footer Close) with `Avatar` per row. It:
- `useQuery(["shared-formulas"], api.listSharedFormulas)`.
- filters by formula name/description/ownerName.
- each row: `<Avatar size="xs" initials={initialsFromName(f.ownerName)} pictureUrl={f.ownerPictureUrl} />`,
  the owner name (`t("sharedFormulaBy",{name})`), the formula name + description, a **View/Hide** toggle that
  reveals the read-only prompt (a `<pre className="whitespace-pre-wrap ...">{f.formula.prompt}</pre>`) and
  context labels, and an **Add**/**Remove** button driven by `alreadyAdded`.
- Add/Remove -> `api.subscribeFormula`/`unsubscribeFormula`, then invalidate `["shared-formulas"]` **and**
  `["formulas"]` (so the run picker's Shared group updates).
- Escape + backdrop close (it has no unsaved work).
- Uses the `FlaskIcon` on its title (`t("workspace:sharedFormulasTitle")`), consistent with the other formula
  modals.

- [ ] **Step 5: Wire RecordingDetail**

In `apps/web/src/pages/RecordingDetail.tsx`: add `sharedBrowserOpen` state; pass
`onFindShared={() => setSharedBrowserOpen(true)}` to `FormulaRunModal`; render `{sharedBrowserOpen &&
<SharedFormulasBrowser onClose={() => setSharedBrowserOpen(false)} />}`. (The browser is global - no recording
context.)

- [ ] **Step 6: Green + full web suite + commit**
```bash
cd apps/web && npx vitest run src/components/SharedFormulasBrowser.test.tsx src/components/FormulaRunModal.test.tsx   # PASS
cd apps/web && npm run build && npm test                                                                             # green, pristine
git add apps/web/src/components/FormulaRunModal.tsx apps/web/src/components/SharedFormulasBrowser.tsx apps/web/src/components/SharedFormulasBrowser.test.tsx apps/web/src/pages/RecordingDetail.tsx apps/web/src/locales apps/web/src/components/FormulaRunModal.test.tsx
git commit -m "feat(formulas): Shared Formulas run-picker group + discovery browser (add/remove)"
```

---

## Task D7: Docs + version bump

**Files:** `docs/Data_Schema.md`, `docs/Overall_Synopsis_of_Platform.md`, `README.md`, `docs/features.md`,
`apps/web/src/lib/releases.ts`, version files.

- [ ] **Step 1: Schema doc**

`docs/Data_Schema.md`: add the `FormulaSubscription` table (columns/PK/FKs/cascades + the unique
`(FormulaId, UserId)` index), add the `Shared` column to the `Formula`/`Formulas` table, and add a
migration-history row for `AddFormulaSharing`.

- [ ] **Step 2: Architecture doc + README + features**

- `docs/Overall_Synopsis_of_Platform.md` (Formulas section): document sharing - `Formula.Shared`, the
  `FormulaSubscription` link (cascade), the discovery + subscribe/unsubscribe endpoints, the owner-or-subscribed
  run rule, and that owner edits propagate live.
- `README.md` (Features table Formulas row) + `docs/features.md` (Formulas bullet): mention users can share a
  Personal formula, others discover and add it (a live link), and run it. Keep the README row one concise line.
- `apps/web/src/lib/releases.ts` `CAPABILITIES` Formulas row: add sharing to the description.
  All user-facing copy: no em/en dashes.

- [ ] **Step 3: Failing release guard -> bump**

Set `version.json` to `0.132.0`; `cd apps/web && npm test -- releases` -> FAIL. Then set the three mirrors
(`apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`) to `0.132.0` and
prepend `RELEASES[0]`:
```ts
  {
    version: "0.132.0",
    date: "2026-07-13",
    pr: 0, // set after opening the PR
    headline: "Share formulas with your team",
    summary:
      "You can now share a personal formula with everyone on the platform. Turn on \"Share this formula\" when editing it, and others can find it under \"Find shared formulas\" in the run picker, see who shared it, read what it does, and add it to their own collection - a live link, so your later edits reach them too. Added formulas appear in a new \"Shared Formulas\" group; anyone can remove one they added, and deleting the original removes it for everyone.",
    added: [
      "Share a personal formula platform-wide, and discover + add formulas others have shared (a live link, not a copy).",
      "A \"Shared Formulas\" group in the run picker and a \"Find shared formulas\" browser with the sharer's name and avatar.",
    ],
  },
```

- [ ] **Step 4: Green + builds + commit**
```bash
cd apps/web && npm test -- releases   # PASS
dotnet build Diariz.slnx              # succeeds
git add docs/Data_Schema.md docs/Overall_Synopsis_of_Platform.md README.md docs/features.md version.json apps/web/package.json apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj apps/web/src/lib/releases.ts
git commit -m "chore(release): 0.132.0 - shared formulas"
```

---

## Finish

- [ ] `dotnet build Diariz.slnx` clean; `dotnet test tests/Diariz.Api.Tests` green; `cd apps/web && npm test`
  green + pristine. Run `dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~Formulas"`
  if Docker is available (the cascade/unique tests need real Postgres).
- [ ] Live browser verification (coordinator): user A shares a Personal formula -> user B opens "Find shared
  formulas", sees A's avatar/name + the read-only prompt, adds it -> it shows under "Shared Formulas" in B's
  run picker -> B runs it (uses B's LLM config) -> A edits it and B sees the change -> B removes it -> A deletes
  it and it disappears for any remaining subscriber.
- [ ] Use **superpowers:finishing-a-development-branch**: push `feat/formulas-phase-d-shared`, open a PR.
  Deployment = **server redeploy (API + web)**; **one additive migration** (no CurrentFormat bump); no desktop
  release.
- [ ] Set `RELEASES[0].pr` once known.

## Self-review checklist
- Spec coverage: `Shared` column + `FormulaSubscription` cascade (D1) / share flag + discovery + subscribe +
  subscribed-visibility (D2) / owner-or-subscribed run access (D3) / cascade+unique integration (D4) / share
  checkbox + caller id (D5) / Shared run group + discovery browser + remove (D6) / schema+synopsis+README+
  features+CAPABILITIES + minor bump (D7). ✓
- No placeholders: full code for the entity, config, endpoints, runner rule, DTOs, checkbox, grouping, and the
  browser's behaviour; test intents enumerated with the exact cases. ✓
- Consistency: `FormulaSubscription` FK names/cascades match across entity+config+migration; `Shared` added to
  entity + `FormulaDto` + `ToDto` + create/update; `SharedFormulaDto`/`SharedFormula` field names align
  (server PascalCase -> web camelCase via the JSON serializer); requests use TRAILING optional `Shared` to
  keep positional tests compiling; `["formulas"]` invalidated after subscribe/unsubscribe. ✓
- Safety: additive migration, forward-restore-safe -> no `CurrentFormat` bump (stated). Leak-avoidance (404)
  preserved for non-subscribed shared run + subscribe on own/non-shared. ✓
