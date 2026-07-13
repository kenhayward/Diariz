# Formulas Phase C - Two-panel Formulas tab (Implementation Plan)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this
> plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Turn the Formulas transcript tab into a resizable two-panel view - a left runs-list and a right panel
that renders the selected result's Markdown - and give each run an **origin icon** (the Diariz logo for
Diariz + Platform formulas, the running user's avatar for Personal ones). The origin is resolved server-side
and carried on `FormulaResultDto`.

**Architecture:** Web (`apps/web`) + a small API DTO addition. Server: a shared `FormulaResultOrigins`
resolver batches the linked formula's scope/owner + that person's display name/picture into a new
`FormulaResultOriginDto` on every `FormulaResultDto` (List / Update / Run). Web: a new `FormulasPanel`
composes the existing `FormulasManager` (left, resizable) with a `renderMarkdown` preview (right); the toolbar
is unchanged.

**Tech Stack:** ASP.NET Core (.NET 10) + EF Core; React 19 + TS + Vite + Tailwind; RTL/vitest (RTL **is**
wired here - write real component tests); xUnit + in-memory `TestDb`.

**Release:** Minor bump `0.130.2` -> `0.131.0` (functional enhancement). **Deployment:** server redeploy
(API + web). No migration. No desktop release.

---

## Current state (verified 2026-07-13)
- `FormulaResultDto` (`src/Diariz.Api/Contracts/ApiDtos.cs:523`): `(Guid Id, Guid RecordingId, string Name,
  Guid? CreatedByUserId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)`.
- Built in TWO places: `FormulaResultsController.ToDto` (L137, used by List/Update) and
  `FormulasController.ToResultDto` (L231, used by Run). Adding a required `Origin` forces both to update -
  `dotnet build Diariz.slnx` will flag any missed site.
- `FormulaRunner.RunAsync` returns the `FormulaResult` **entity**; `RunFormulaTool` uses it but builds its own
  text (no DTO), so it needs no change.
- `FormulaResult` (entity): `FormulaId` is nullable (`SET NULL` when the formula is deleted); `CreatedByUserId`
  is nullable (`SET NULL` when the author is deleted).
- `ApplicationUser`: `FullName` (nullable, UI falls back to Email) + `PictureUrl` (nullable, Google picture).
- Web: `FormulaResult` type (`apps/web/src/lib/types.ts:795`); `api.getFormulaResultText(recordingId, id)`
  returns the Markdown string; `api.listFormulaResults` returns `FormulaResult[]`.
- Tab wiring: `RecordingDetail.tsx:1463-1483` pushes `{ key:"formulas", toolbar:<FormulasToolbar/>,
  content:<FormulasManager/> }`; `selectedFormulaResultId` state at L216.
- `FormulasManager.tsx` renders a single-select `<ul>`; `DetailTabs` page-scrolls (sticky strip+toolbar, the
  content grows with the page).
- `useResizableWidth` computes width from **absolute** `ev.clientX` (correct only for a viewport-left-anchored
  panel). The Formulas tab is offset inside the detail column, so this task uses a small **delta-based** resize
  (width += cursorDelta) instead - position-independent.
- `renderMarkdown(text): string` (`apps/web/src/lib/markdown.ts`); `initialsFromName(name|null): string`
  (`apps/web/src/lib/initials.ts`); `Avatar` takes `{ initials, pictureUrl?, size?: "xs"|"sm"|"lg" }`; the
  Diariz logo is `apps/web/public/logo.png` (used as `<img src="/logo.png">`).

## Files
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs` (new `FormulaResultOriginDto`; add `Origin` to `FormulaResultDto`).
- Create: `src/Diariz.Api/Services/FormulaResultOrigins.cs` (shared batch resolver).
- Modify: `src/Diariz.Api/Controllers/FormulaResultsController.cs` (List/Update use the resolver).
- Modify: `src/Diariz.Api/Controllers/FormulasController.cs` (Run uses the resolver).
- Create: `tests/Diariz.Api.Tests/FormulaResultOriginsTests.cs`.
- Modify: `apps/web/src/lib/types.ts` (`FormulaResultOrigin` + `origin` on `FormulaResult`).
- Modify: `apps/web/src/components/FormulasManager.tsx` (origin icon per row).
- Create: `apps/web/src/components/FormulasPanel.tsx` (the two-panel split + right preview).
- Modify: `apps/web/src/pages/RecordingDetail.tsx` (use `FormulasPanel`; invalidate the preview query on edit).
- Modify: `apps/web/src/locales/{en,de,es,fr}/workspace.json` (`formulaSelectToView`).
- Modify/Create tests: `apps/web/src/components/FormulasManager.test.tsx`, `FormulasPanel.test.tsx`, and any
  fixtures constructing `FormulaResult` (tsc will flag them - add `origin`).
- Modify: `docs/Overall_Synopsis_of_Platform.md`, `docs/features.md`; version files + `releases.ts`.

---

## Task C1: Server - origin DTO + shared resolver + wire List/Update/Run (TDD)

**Files:**
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs`
- Create: `src/Diariz.Api/Services/FormulaResultOrigins.cs`
- Create: `tests/Diariz.Api.Tests/FormulaResultOriginsTests.cs`
- Modify: `src/Diariz.Api/Controllers/FormulaResultsController.cs`, `src/Diariz.Api/Controllers/FormulasController.cs`

- [ ] **Step 1: Write the failing resolver test**

Create `tests/Diariz.Api.Tests/FormulaResultOriginsTests.cs`:

```csharp
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

/// <summary>Origin resolution for a formula result's left-list icon: Diariz/Platform formulas are "official"
/// (no person); Personal formulas attribute to the owner; a deleted formula (FormulaId SET NULL) attributes
/// to the result's creator. The person's display name falls back to email when FullName is null.</summary>
public class FormulaResultOriginsTests
{
    private static async Task<ApplicationUser> AddUser(Diariz.Domain.DiarizDbContext db, string? fullName,
        string email, string? picture)
    {
        var u = new ApplicationUser
        {
            Id = Guid.NewGuid(), UserName = email, Email = email,
            FullName = fullName, PictureUrl = picture,
        };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        return u;
    }

    [Fact]
    public async Task Resolves_diariz_platform_personal_and_orphaned_results()
    {
        using var db = TestDb.Create();
        var owner = await AddUser(db, "Ada Lovelace", "ada@x.test", "https://pic/ada.png");
        var noName = await AddUser(db, null, "grace@x.test", null);

        var diariz = new Formula { Id = Guid.NewGuid(), Scope = FormulaScope.Diariz, Name = "Recap", Prompt = "p", IsBuiltIn = true };
        var platform = new Formula { Id = Guid.NewGuid(), Scope = FormulaScope.Platform, Name = "Policy", Prompt = "p" };
        var personal = new Formula { Id = Guid.NewGuid(), Scope = FormulaScope.Personal, OwnerUserId = owner.Id, Name = "Mine", Prompt = "p" };
        var personalNoName = new Formula { Id = Guid.NewGuid(), Scope = FormulaScope.Personal, OwnerUserId = noName.Id, Name = "Theirs", Prompt = "p" };
        db.Formulas.AddRange(diariz, platform, personal, personalNoName);
        await db.SaveChangesAsync();

        FormulaResult Res(Guid? formulaId, Guid? createdBy) => new()
        {
            Id = Guid.NewGuid(), RecordingId = Guid.NewGuid(), FormulaId = formulaId,
            CreatedByUserId = createdBy, Name = "r", Text = "t",
        };
        var rDiariz = Res(diariz.Id, owner.Id);
        var rPlatform = Res(platform.Id, owner.Id);
        var rPersonal = Res(personal.Id, owner.Id);
        var rNoName = Res(personalNoName.Id, noName.Id);
        var rOrphan = Res(null, owner.Id); // formula deleted -> attribute to creator

        var origins = await FormulaResultOrigins.ResolveAsync(
            db, new[] { rDiariz, rPlatform, rPersonal, rNoName, rOrphan });

        Assert.Equal("diariz", origins[rDiariz.Id].Kind);
        Assert.Null(origins[rDiariz.Id].PersonName);

        Assert.Equal("platform", origins[rPlatform.Id].Kind);
        Assert.Null(origins[rPlatform.Id].PersonName);

        Assert.Equal("personal", origins[rPersonal.Id].Kind);
        Assert.Equal("Ada Lovelace", origins[rPersonal.Id].PersonName);
        Assert.Equal("https://pic/ada.png", origins[rPersonal.Id].PersonPictureUrl);

        Assert.Equal("personal", origins[rNoName.Id].Kind);
        Assert.Equal("grace@x.test", origins[rNoName.Id].PersonName); // FullName null -> email
        Assert.Null(origins[rNoName.Id].PersonPictureUrl);

        Assert.Equal("personal", origins[rOrphan.Id].Kind);
        Assert.Equal("Ada Lovelace", origins[rOrphan.Id].PersonName); // creator
    }
}
```

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~FormulaResultOrigins"`
Expected: FAIL to compile (`FormulaResultOrigins` / `FormulaResultOriginDto` don't exist).

- [ ] **Step 2: Add the DTOs**

In `src/Diariz.Api/Contracts/ApiDtos.cs`, add near `FormulaResultDto` (L523):

```csharp
/// <summary>Where a formula result came from, for the runs-list icon. Kind: "diariz" | "platform" |
/// "personal" (Phase D adds "shared"). Diariz/Platform are "official" (no person - the UI shows the Diariz
/// logo); personal/shared carry the person's display + picture for an avatar.</summary>
public record FormulaResultOriginDto(string Kind, string? PersonName, string? PersonPictureUrl);
```

and change `FormulaResultDto` to append `Origin`:

```csharp
public record FormulaResultDto(
    Guid Id, Guid RecordingId, string Name, Guid? CreatedByUserId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, FormulaResultOriginDto Origin);
```

- [ ] **Step 3: Implement the shared resolver**

Create `src/Diariz.Api/Services/FormulaResultOrigins.cs`:

```csharp
using Diariz.Api.Contracts;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>Resolves the display "origin" of formula results for the runs-list icon, batched (no N+1):
/// Diariz/Platform formulas are "official" (no person); Personal formulas attribute to the owner; a result
/// whose formula was deleted (FormulaId SET NULL) attributes to its creator. Shared by the results and run
/// endpoints so the icon is consistent everywhere.</summary>
public static class FormulaResultOrigins
{
    public static async Task<IReadOnlyDictionary<Guid, FormulaResultOriginDto>> ResolveAsync(
        DiarizDbContext db, IReadOnlyList<FormulaResult> results, CancellationToken ct = default)
    {
        var formulaIds = results.Where(r => r.FormulaId != null).Select(r => r.FormulaId!.Value).Distinct().ToList();
        var formulas = (await db.Formulas.Where(f => formulaIds.Contains(f.Id))
                .Select(f => new { f.Id, f.Scope, f.OwnerUserId }).ToListAsync(ct))
            .ToDictionary(f => f.Id, f => (f.Scope, f.OwnerUserId));

        var personIds = new HashSet<Guid>();
        foreach (var r in results)
        {
            if (r.FormulaId is Guid fid && formulas.TryGetValue(fid, out var f))
            {
                if (f.Scope == FormulaScope.Personal && f.OwnerUserId is Guid oid) personIds.Add(oid);
            }
            else if (r.CreatedByUserId is Guid cid) personIds.Add(cid);
        }

        var people = (await db.Users.Where(u => personIds.Contains(u.Id))
                .Select(u => new { u.Id, u.FullName, u.Email }).ToListAsync(ct))
            .ToDictionary(u => u.Id, u => (u.FullName, u.Email));
        var pictures = (await db.Users.Where(u => personIds.Contains(u.Id))
                .Select(u => new { u.Id, u.PictureUrl }).ToListAsync(ct))
            .ToDictionary(u => u.Id, u => u.PictureUrl);

        var origins = new Dictionary<Guid, FormulaResultOriginDto>(results.Count);
        foreach (var r in results)
        {
            FormulaScope? scope = null;
            Guid? personId = null;
            if (r.FormulaId is Guid fid && formulas.TryGetValue(fid, out var f))
            {
                scope = f.Scope;
                personId = f.Scope == FormulaScope.Personal ? f.OwnerUserId : null;
            }
            else
            {
                personId = r.CreatedByUserId; // formula deleted/missing -> attribute to the creator
            }
            origins[r.Id] = Build(scope, personId, people, pictures);
        }
        return origins;
    }

    private static FormulaResultOriginDto Build(
        FormulaScope? scope, Guid? personId,
        IReadOnlyDictionary<Guid, (string? FullName, string? Email)> people,
        IReadOnlyDictionary<Guid, string?> pictures)
    {
        var kind = scope switch
        {
            FormulaScope.Diariz => "diariz",
            FormulaScope.Platform => "platform",
            _ => "personal", // Personal, or a deleted formula (scope null)
        };
        if (scope is FormulaScope.Diariz or FormulaScope.Platform)
            return new(kind, null, null);
        if (personId is Guid id && people.TryGetValue(id, out var p))
            return new(kind, string.IsNullOrWhiteSpace(p.FullName) ? p.Email : p.FullName,
                pictures.TryGetValue(id, out var pic) ? pic : null);
        return new(kind, null, null);
    }
}
```

> Note: the person's display + picture are read in two small queries (kept separate so the value tuples
> translate cleanly through `ToList` + client `ToDictionary`; the in-memory test provider handles both).

- [ ] **Step 4: Green resolver test**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~FormulaResultOrigins"`
Expected: PASS.

- [ ] **Step 5: Wire the controllers**

In `src/Diariz.Api/Controllers/FormulaResultsController.cs`:
- `List` - replace the `return Ok(results.Select(ToDto).ToList());` tail with:

```csharp
        var origins = await FormulaResultOrigins.ResolveAsync(_db, results);
        return Ok(results.Select(r => ToDto(r, origins[r.Id])).ToList());
```

- `Update` - after `await _db.SaveChangesAsync();` replace `return Ok(ToDto(result));` with:

```csharp
        var origins = await FormulaResultOrigins.ResolveAsync(_db, new[] { result });
        return Ok(ToDto(result, origins[result.Id]));
```

- Replace the `ToDto` helper (L137-138):

```csharp
    private static FormulaResultDto ToDto(FormulaResult r, FormulaResultOriginDto origin) => new(
        r.Id, r.RecordingId, r.Name, r.CreatedByUserId, r.CreatedAt, r.UpdatedAt, origin);
```

Add `using Diariz.Api.Services;` if not present.

In `src/Diariz.Api/Controllers/FormulasController.cs` `Run` (L191-223): after `var result = await
_runner.RunAsync(...)`, resolve the origin and pass it to `ToResultDto`:

```csharp
            var result = await _runner.RunAsync(UserId, recordingId, formulaId, ct);
            var origins = await FormulaResultOrigins.ResolveAsync(_db, new[] { result }, ct);
            return Ok(ToResultDto(result, origins[result.Id]));
```

and update `ToResultDto` (L231):

```csharp
    private static FormulaResultDto ToResultDto(FormulaResult r, FormulaResultOriginDto origin) => new(
        r.Id, r.RecordingId, r.Name, r.CreatedByUserId, r.CreatedAt, r.UpdatedAt, origin);
```

(`FormulasController` already has `_db`; add `using Diariz.Api.Services;` if needed.)

- [ ] **Step 6: Build the whole solution (catches any missed DTO construction site) + backend tests**

Run: `dotnet build Diariz.slnx` -> succeeds (if it flags another `new FormulaResultDto(...)`, add the origin
there too).
Run: `dotnet test tests/Diariz.Api.Tests` -> green.

- [ ] **Step 7: Commit**

```bash
git add src/Diariz.Api/Contracts/ApiDtos.cs src/Diariz.Api/Services/FormulaResultOrigins.cs src/Diariz.Api/Controllers/FormulaResultsController.cs src/Diariz.Api/Controllers/FormulasController.cs tests/Diariz.Api.Tests/FormulaResultOriginsTests.cs
git commit -m "feat(formulas): resolve a result's origin (scope + person) onto FormulaResultDto"
```

---

## Task C2: Web - origin type + list icon (TDD)

**Files:**
- Modify: `apps/web/src/lib/types.ts`
- Modify: `apps/web/src/components/FormulasManager.tsx`
- Modify/Create: `apps/web/src/components/FormulasManager.test.tsx`

- [ ] **Step 1: Add the type**

In `apps/web/src/lib/types.ts`, above `FormulaResult` (L795), add and extend:

```ts
/// Where a formula result came from, for the runs-list icon. Mirrors FormulaResultOriginDto.
export interface FormulaResultOrigin {
  kind: "diariz" | "platform" | "personal"; // Phase D adds "shared"
  personName: string | null;
  personPictureUrl: string | null;
}
```
and add `origin: FormulaResultOrigin;` as the last field of `FormulaResult`.

- [ ] **Step 2: Update/extend the FormulasManager test (red)**

Ensure `apps/web/src/components/FormulasManager.test.tsx` exists; add an origin field to its fixtures and a
test asserting the icon. If the file doesn't exist, create it:

```tsx
import { render, screen } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import FormulasManager from "./FormulasManager";
import type { FormulaResult } from "../lib/types";

vi.mock("react-i18next", () => ({
  useTranslation: () => ({ t: (k: string) => k, i18n: { language: "en" } }),
}));

const base = {
  recordingId: "r1", createdByUserId: "u1",
  createdAt: "2026-07-13T00:00:00Z", updatedAt: "2026-07-13T00:00:00Z",
};
const results: FormulaResult[] = [
  { ...base, id: "a", name: "Recap", origin: { kind: "diariz", personName: null, personPictureUrl: null } },
  { ...base, id: "b", name: "Mine", origin: { kind: "personal", personName: "Ada Lovelace", personPictureUrl: null } },
];

describe("FormulasManager origin icons", () => {
  it("shows the Diariz logo for a built-in result and initials for a personal one", () => {
    render(<FormulasManager results={results} selectedId={null} onSelect={() => {}} />);
    // Diariz -> the logo image
    expect(screen.getByRole("img")).toHaveAttribute("src", "/logo.png");
    // Personal (no picture) -> initials bubble "AL"
    expect(screen.getByText("AL")).toBeInTheDocument();
  });
});
```

Run: `cd apps/web && npx vitest run src/components/FormulasManager.test.tsx`
Expected: FAIL (no icon rendered yet).

- [ ] **Step 3: Render the origin icon**

In `apps/web/src/components/FormulasManager.tsx`:
- Add imports: `import Avatar from "./Avatar";`, `import { initialsFromName } from "../lib/initials";`,
  `import type { FormulaResult, FormulaResultOrigin } from "../lib/types";` (replace the existing type import).
- Make the row button a flex row with the icon first:

Replace the `<button ...>` block's className and inner markup so it becomes:
```tsx
          <button
            type="button"
            onClick={() => onSelect(selectedId === r.id ? null : r.id)}
            aria-pressed={selectedId === r.id}
            className={`flex w-full items-center gap-2 rounded px-2 py-2 text-left ${
              selectedId === r.id ? "bg-blue-50 dark:bg-blue-900/30" : "hover:bg-gray-50 dark:hover:bg-gray-800"
            }`}
          >
            <OriginIcon origin={r.origin} />
            <span className="min-w-0 flex-1">
              <span className="block truncate text-sm font-medium text-gray-800 dark:text-gray-100">{r.name}</span>
              <span className="block truncate text-xs text-gray-400 dark:text-gray-500">
                {t("formulaGeneratedMeta", {
                  time: formatRelativeTime(r.createdAt, i18n.language),
                  name: r.name,
                })}
              </span>
            </span>
          </button>
```
- Add the icon component at the bottom of the file:
```tsx
/// Diariz + Platform formulas are "official" -> the Diariz logo; personal/shared -> the person's avatar.
function OriginIcon({ origin }: { origin: FormulaResultOrigin }) {
  if (origin.kind === "diariz" || origin.kind === "platform") {
    return <img src="/logo.png" alt="" className="h-6 w-6 shrink-0 rounded-full object-cover" />;
  }
  return <Avatar size="xs" initials={initialsFromName(origin.personName)} pictureUrl={origin.personPictureUrl} />;
}
```

- [ ] **Step 4: Green + typecheck**

Run: `cd apps/web && npx vitest run src/components/FormulasManager.test.tsx` -> PASS.
Run: `cd apps/web && npm run build` -> PASS (tsc will flag any OTHER fixture that builds a `FormulaResult`
without `origin` - add `origin` to each; search test files + any mock data).

- [ ] **Step 5: Commit**

```bash
git add apps/web/src/lib/types.ts apps/web/src/components/FormulasManager.tsx apps/web/src/components/FormulasManager.test.tsx
git commit -m "feat(formulas): show an origin icon (logo or avatar) on each run in the list"
```

---

## Task C3: Web - the two-panel split (TDD)

**Files:**
- Create: `apps/web/src/components/FormulasPanel.tsx`
- Create: `apps/web/src/components/FormulasPanel.test.tsx`
- Modify: `apps/web/src/pages/RecordingDetail.tsx`
- Modify: `apps/web/src/locales/{en,de,es,fr}/workspace.json`

- [ ] **Step 1: Add the i18n key**

Add `formulaSelectToView` to each locale's `workspace.json`:
- en: `Select a result to view it.`
- de: `Wählen Sie ein Ergebnis zur Anzeige aus.`
- es: `Selecciona un resultado para verlo.`
- fr: `Sélectionnez un résultat pour l'afficher.`

- [ ] **Step 2: Write the FormulasPanel test (red)**

Create `apps/web/src/components/FormulasPanel.test.tsx`:

```tsx
import { render, screen, waitFor, fireEvent } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi } from "vitest";
import FormulasPanel from "./FormulasPanel";
import type { FormulaResult } from "../lib/types";

vi.mock("react-i18next", () => ({
  useTranslation: () => ({ t: (k: string) => k, i18n: { language: "en" } }),
}));
vi.mock("../lib/api", () => ({
  api: { getFormulaResultText: vi.fn().mockResolvedValue("# Hello\n\nBody text.") },
  apiErrorMessage: (e: unknown, f: string) => f,
}));

const base = {
  recordingId: "r1", createdByUserId: "u1",
  createdAt: "2026-07-13T00:00:00Z", updatedAt: "2026-07-13T00:00:00Z",
  origin: { kind: "personal" as const, personName: "Ada", personPictureUrl: null },
};
const results: FormulaResult[] = [{ ...base, id: "a", name: "Recap" }];

function wrap(ui: React.ReactElement) {
  const qc = new QueryClient();
  return render(<QueryClientProvider client={qc}>{ui}</QueryClientProvider>);
}

describe("FormulasPanel", () => {
  it("renders the empty state (no split) when there are no runs", () => {
    wrap(<FormulasPanel recordingId="r1" results={[]} selectedId={null} onSelect={() => {}} />);
    expect(screen.getByText("formulasEmpty")).toBeInTheDocument();
    expect(screen.queryByRole("separator")).not.toBeInTheDocument();
  });

  it("prompts to select when a split is shown but nothing is selected", () => {
    wrap(<FormulasPanel recordingId="r1" results={results} selectedId={null} onSelect={() => {}} />);
    expect(screen.getByText("formulaSelectToView")).toBeInTheDocument();
    expect(screen.getByRole("separator")).toBeInTheDocument();
  });

  it("renders the selected result's markdown in the right panel", async () => {
    wrap(<FormulasPanel recordingId="r1" results={results} selectedId="a" onSelect={() => {}} />);
    await waitFor(() => expect(screen.getByText("Hello")).toBeInTheDocument());
    expect(screen.getByText("Body text.")).toBeInTheDocument();
  });
});
```

Run: `cd apps/web && npx vitest run src/components/FormulasPanel.test.tsx`
Expected: FAIL (no `FormulasPanel`).

- [ ] **Step 3: Implement FormulasPanel**

Create `apps/web/src/components/FormulasPanel.tsx`:

```tsx
import { useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { useQuery } from "@tanstack/react-query";
import { api, apiErrorMessage } from "../lib/api";
import { renderMarkdown } from "../lib/markdown";
import type { FormulaResult } from "../lib/types";
import FormulasManager from "./FormulasManager";

const WIDTH_KEY = "diariz.formulas.listWidth";
const MIN = 200, MAX = 460, INITIAL = 280;

/// The Formulas tab body: a resizable left runs-list (FormulasManager) beside a right panel that renders the
/// selected result's Markdown read-only. The empty state (no runs) spans the whole width. Resize is
/// delta-based (width += cursor delta) rather than useResizableWidth's absolute clientX, because this panel
/// is offset inside the detail column, not anchored to the viewport's left edge.
export default function FormulasPanel({
  recordingId,
  results,
  selectedId,
  onSelect,
}: {
  recordingId: string;
  results: FormulaResult[];
  selectedId: string | null;
  onSelect: (id: string | null) => void;
}) {
  const [width, setWidth] = useState<number>(() => {
    const s = Number(localStorage.getItem(WIDTH_KEY));
    return s >= MIN && s <= MAX ? s : INITIAL;
  });
  const widthRef = useRef(width);
  widthRef.current = width;

  function startResize(e: React.MouseEvent) {
    e.preventDefault();
    const startX = e.clientX;
    const startW = widthRef.current;
    const onMove = (ev: MouseEvent) => setWidth(Math.min(MAX, Math.max(MIN, startW + ev.clientX - startX)));
    const onUp = () => {
      document.removeEventListener("mousemove", onMove);
      document.removeEventListener("mouseup", onUp);
      document.body.style.userSelect = "";
      localStorage.setItem(WIDTH_KEY, String(widthRef.current));
    };
    document.body.style.userSelect = "none";
    document.addEventListener("mousemove", onMove);
    document.addEventListener("mouseup", onUp);
  }

  // No runs yet: show the manager's empty state across the whole body (no split).
  if (results.length === 0) {
    return <FormulasManager results={results} selectedId={selectedId} onSelect={onSelect} />;
  }

  return (
    <div className="flex items-stretch">
      <div style={{ width }} className="min-w-0 shrink-0">
        <FormulasManager results={results} selectedId={selectedId} onSelect={onSelect} />
      </div>
      <div
        role="separator"
        aria-orientation="vertical"
        onMouseDown={startResize}
        className="w-1 shrink-0 cursor-col-resize bg-gray-200 hover:bg-blue-400 dark:bg-gray-700"
      />
      <div className="min-w-0 flex-1 pl-3">
        <ResultView recordingId={recordingId} selectedId={selectedId} />
      </div>
    </div>
  );
}

function ResultView({ recordingId, selectedId }: { recordingId: string; selectedId: string | null }) {
  const { t } = useTranslation(["workspace", "common"]);
  const { data, isLoading, error } = useQuery({
    queryKey: ["formula-result-text", recordingId, selectedId],
    queryFn: () => api.getFormulaResultText(recordingId, selectedId!),
    enabled: selectedId != null,
  });

  if (selectedId == null)
    return <p className="px-1 py-6 text-sm text-gray-400 dark:text-gray-500">{t("workspace:formulaSelectToView")}</p>;
  if (isLoading) return <p className="px-1 py-6 text-sm text-gray-500 dark:text-gray-400">{t("common:loading")}</p>;
  if (error)
    return (
      <p className="px-1 py-6 text-sm text-red-600 dark:text-red-400">
        {apiErrorMessage(error, t("workspace:formulaResultLoadFailed"))}
      </p>
    );
  return (
    <div
      className="chat-md text-sm dark:text-gray-100"
      dangerouslySetInnerHTML={{ __html: renderMarkdown(data ?? "") }}
    />
  );
}
```

- [ ] **Step 4: Green FormulasPanel test**

Run: `cd apps/web && npx vitest run src/components/FormulasPanel.test.tsx` -> PASS.

- [ ] **Step 5: Wire RecordingDetail to use the panel + refresh the preview on edit**

In `apps/web/src/pages/RecordingDetail.tsx`:
- Replace the `FormulasManager` import (L34) with `import FormulasPanel from "../components/FormulasPanel";`
  (FormulasManager is now used only inside FormulasPanel; remove the now-unused import to satisfy tsc).
- In the formulas tab (L1476-1482), replace the `content` with:

```tsx
      content: (
        <FormulasPanel
          recordingId={id}
          results={formulaResults}
          selectedId={selectedFormulaResultId}
          onSelect={setSelectedFormulaResultId}
        />
      ),
```
- Ensure the right-panel preview refreshes after an edit: wherever the formula-result edit save refreshes
  (the `refreshFormulas` helper around L549, and/or the FormulaResultEditModal `onSaved`), also invalidate the
  preview query so the rendered panel isn't stale:

```ts
    void queryClient.invalidateQueries({ queryKey: ["formula-result-text", id] });
```
(Use the existing `queryClient`/`qc` instance in scope; the `["formula-results", id]` invalidation already
there stays. Match the surrounding invalidation style.)

- [ ] **Step 6: Typecheck + full web suite**

Run: `cd apps/web && npm run build` -> PASS.
Run: `cd apps/web && npm test` -> green, pristine (was 848+ at baseline; new tests added).

- [ ] **Step 7: Commit**

```bash
git add apps/web/src/components/FormulasPanel.tsx apps/web/src/components/FormulasPanel.test.tsx apps/web/src/pages/RecordingDetail.tsx apps/web/src/locales
git commit -m "feat(formulas): two-panel Formulas tab - resizable runs list beside a rendered result"
```

---

## Task C4: Docs + version bump

**Files:**
- Modify: `docs/Overall_Synopsis_of_Platform.md`, `docs/features.md`
- Modify: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`
- Modify: `apps/web/src/lib/releases.ts`

- [ ] **Step 1: Docs**

- `docs/Overall_Synopsis_of_Platform.md` (Formulas section, ~L360-402): note the Formulas tab is now a
  two-panel view (runs list + rendered result) and that `FormulaResultDto` carries an `origin` (scope +
  person) for the list icon, resolved by `FormulaResultOrigins`. One concise addition; no em/en dashes.
- `docs/features.md` (Formulas bullet): add that the tab shows generated results in a resizable list beside a
  rendered preview, with an origin icon (Diariz logo for built-ins, the author's avatar otherwise).

- [ ] **Step 2: Failing release-invariant guard**

Set `version.json` to `0.131.0`, then: `cd apps/web && npm test -- releases` -> FAIL.

- [ ] **Step 3: Bump mirrors + release entry**

Set `<Version>` (csproj) + `"version"` (web + desktop package.json) to `0.131.0`. Prepend to `RELEASES`:

```ts
  {
    version: "0.131.0",
    date: "2026-07-13",
    pr: 0, // set to the real PR number after opening the PR
    headline: "Formulas tab: side-by-side runs and results",
    summary:
      "The Formulas tab is now a two-panel view: a resizable list of the formulas you have run on the left, and the selected result's rendered document on the right - click a run to read it in place, with the toolbar acting on your selection as before. Each run in the list shows an origin icon: the Diariz logo for built-in formulas, or the author's avatar for your own.",
    added: [
      "Two-panel Formulas tab: a resizable runs list beside a live rendered preview of the selected result.",
      "Origin icon on each run (Diariz logo for built-in/platform formulas, the author's avatar otherwise).",
    ],
  },
```

- [ ] **Step 4: Green + builds**

Run: `cd apps/web && npm test -- releases` -> PASS.
Run: `dotnet build Diariz.slnx` -> succeeds.

- [ ] **Step 5: Commit**

```bash
git add docs/Overall_Synopsis_of_Platform.md docs/features.md version.json apps/web/package.json apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj apps/web/src/lib/releases.ts
git commit -m "chore(release): 0.131.0 - two-panel Formulas tab"
```

---

## Finish

- [ ] `dotnet build Diariz.slnx` clean; `dotnet test tests/Diariz.Api.Tests` green; `cd apps/web && npm test`
  green + pristine. (Integration tests need Docker; run if available - the FormulaResultDto shape changed but
  the resolver is unit-tested.)
- [ ] Live browser verification (coordinator does this): open a recording's Formulas tab, run a formula,
  confirm the two panels, the origin icons (logo for a built-in run, avatar for a personal one), that
  selecting a run renders it on the right, and that the divider drags/persists.
- [ ] Use **superpowers:finishing-a-development-branch**: push `feat/formulas-phase-c-two-panel-tab`, open a
  PR. Deployment surface = **server redeploy (API + web)**; no migration; no desktop release.
- [ ] Set `RELEASES[0].pr` once known.

## Self-review checklist
- Spec coverage: two-panel resizable split (C3) / right rendered result (C3) / origin icon logo-vs-avatar (C2)
  / `origin` on the DTO resolved server-side with graceful null-formula fallback (C1) / minor bump + docs (C4). ✓
- No placeholders: full code for the resolver, DTOs, controller edits, `FormulasPanel`, `OriginIcon`,
  `ResultView`, and every test. ✓
- Consistency: `FormulaResultOriginDto`/`FormulaResultOrigin` field names (`Kind`/`kind`,
  `PersonName`/`personName`, `PersonPictureUrl`/`personPictureUrl`) match across server + web; `origin` added
  to both the DTO and the TS type; the delta-resize rationale is documented (not `useResizableWidth`). ✓
- Forward to Phase D: `OriginIcon`'s else-branch already renders an avatar for any non-diariz/platform kind, so
  "shared" needs only server population - no web change. ✓
