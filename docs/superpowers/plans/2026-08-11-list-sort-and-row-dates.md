# List Sorting, Row Date/Time, and the Folder-Icon Affordance - Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a persisted Sort by control to the recordings List tab, show each recording's date and time where the row showed its duration, and make the breadcrumb's folder icon the Change folder button.

**Architecture:** All three changes are client-only and live in `apps/web`. Sorting is a pure function over the already-fetched recordings plus a small localStorage-backed hook - the server keeps returning `Position` order and no endpoint changes. The sort is display-only: the id lists that feed reorder writes stay in manual order, and drop-to-reorder is switched off while a non-manual sort is active.

**Tech Stack:** React 19 + TypeScript + Vite + Tailwind v4, i18next, TanStack Query, vitest + @testing-library/react.

**Spec:** `docs/superpowers/specs/2026-08-11-list-sort-and-row-dates-design.md`

## Global Constraints

- **TDD is required.** Write the failing test, run it and watch it fail, then write the minimal code to pass. No production code without a failing test that preceded it.
- **No em or en dashes in user-facing text.** Use a plain hyphen `-` in all UI strings, i18n catalogs and release notes. (Code comments are unaffected.)
- **Every new i18n key must be added to all four locales** - `apps/web/src/locales/{en,de,es,fr}/workspace.json`. `src/locales.test.ts` fails the build if the key sets drift.
- **Keep test output pristine** - a passing run has no errors or warnings.
- **Never `git add -A`** in this repo; stage explicit paths only.
- Run web tests from `apps/web`: `npm test -- --run <path>`.
- Version bump for this PR: **0.206.0 -> 0.207.0** (functional enhancement). Handled once, in Task 8.
- Deployment surface: **server redeploy only**, no desktop release.

---

### Task 1: The sort model (`lib/listSort.ts`)

Pure comparators plus the persisted setting. No UI.

**Files:**
- Create: `apps/web/src/lib/listSort.ts`
- Test: `apps/web/src/lib/listSort.test.ts`

**Interfaces:**
- Consumes: `RecordingSummary` from `apps/web/src/lib/types`.
- Produces:
  - `type SortKey = "manual" | "date" | "name" | "duration"`
  - `type SortDir = "asc" | "desc"`
  - `interface ListSort { key: SortKey; dir: SortDir }`
  - `const LIST_SORT_KEY = "diariz.recordings.sort"`
  - `const DEFAULT_SORT: ListSort`
  - `function parseSort(raw: string | null): ListSort`
  - `function sortRecordings(items: RecordingSummary[], sort: ListSort): RecordingSummary[]`
  - `function useListSort(): [ListSort, (next: ListSort) => void]`

- [ ] **Step 1: Write the failing test**

Create `apps/web/src/lib/listSort.test.ts`:

```ts
import { describe, it, expect } from "vitest";
import { parseSort, sortRecordings, DEFAULT_SORT, type ListSort } from "./listSort";
import type { RecordingSummary } from "./types";

const base: RecordingSummary = {
  id: "a",
  title: "Mic 6/26/2026",
  name: "Alpha",
  source: "Microphone",
  durationMs: 1000,
  status: "Transcribed",
  createdAt: new Date("2026-06-26T12:00:00Z").toISOString(),
  sectionId: null,
  sectionName: null,
  hasActions: false,
  hasAudio: true,
  calendarEventId: null,
};

// Manual order is deliberately neither alphabetical nor chronological, so every key below moves the list.
const items: RecordingSummary[] = [
  { ...base, id: "a", name: "Beta", durationMs: 3000, createdAt: new Date("2026-01-02T09:00:00Z").toISOString() },
  { ...base, id: "b", name: "Alpha", durationMs: 1000, createdAt: new Date("2026-03-04T09:00:00Z").toISOString() },
  { ...base, id: "c", name: "Gamma", durationMs: 2000, createdAt: new Date("2026-02-03T09:00:00Z").toISOString() },
];

const ids = (sort: ListSort) => sortRecordings(items, sort).map((r) => r.id);

describe("sortRecordings", () => {
  it("leaves the manual order untouched in both directions", () => {
    expect(ids({ key: "manual", dir: "asc" })).toEqual(["a", "b", "c"]);
    expect(ids({ key: "manual", dir: "desc" })).toEqual(["a", "b", "c"]);
  });

  it("sorts by name", () => {
    expect(ids({ key: "name", dir: "asc" })).toEqual(["b", "a", "c"]);
    expect(ids({ key: "name", dir: "desc" })).toEqual(["c", "a", "b"]);
  });

  it("sorts by date", () => {
    expect(ids({ key: "date", dir: "asc" })).toEqual(["a", "c", "b"]);
    expect(ids({ key: "date", dir: "desc" })).toEqual(["b", "c", "a"]);
  });

  it("sorts by duration", () => {
    expect(ids({ key: "duration", dir: "asc" })).toEqual(["b", "c", "a"]);
    expect(ids({ key: "duration", dir: "desc" })).toEqual(["a", "c", "b"]);
  });

  // The rows display `name ?? title`, so the sort must agree with what the reader can see.
  it("falls back to the title when a recording has no name", () => {
    const unnamed = [
      { ...base, id: "x", name: null, title: "Zulu" },
      { ...base, id: "y", name: null, title: "Alpha" },
    ];
    expect(sortRecordings(unnamed, { key: "name", dir: "asc" }).map((r) => r.id)).toEqual(["y", "x"]);
  });

  // The panel keeps a separate unsorted list for its reorder writes; a mutating sort would corrupt it.
  it("does not mutate its input", () => {
    const input = [...items];
    sortRecordings(input, { key: "name", dir: "asc" });
    expect(input.map((r) => r.id)).toEqual(["a", "b", "c"]);
  });
});

describe("parseSort", () => {
  it("reads a stored setting back", () => {
    expect(parseSort(JSON.stringify({ key: "date", dir: "desc" }))).toEqual({ key: "date", dir: "desc" });
  });

  // A first visit, a value from an older build, and a hand-edited key all have to land somewhere safe.
  it.each([null, "", "not json", '{"key":"colour","dir":"asc"}', '{"key":"name","dir":"sideways"}', '"name"'])(
    "falls back to the default for %s",
    (raw) => {
      expect(parseSort(raw as string | null)).toEqual(DEFAULT_SORT);
    },
  );
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd apps/web && npm test -- --run src/lib/listSort.test.ts`
Expected: FAIL - `Failed to resolve import "./listSort"`.

- [ ] **Step 3: Write minimal implementation**

Create `apps/web/src/lib/listSort.ts`:

```ts
/// How the List tab's recordings are ordered, and where that choice is kept.
///
/// The sort is **display only**. `computeReorder` (lib/reorder.ts) turns whatever id list it is handed into
/// the recordings' new server `Position`s, so the panel keeps feeding its reorder writes the unsorted level
/// while rendering the sorted one. Sorting a view must never rewrite an order the user arranged by hand.

import { useState } from "react";
import type { RecordingSummary } from "./types";

export type SortKey = "manual" | "date" | "name" | "duration";
export type SortDir = "asc" | "desc";

export interface ListSort {
  key: SortKey;
  dir: SortDir;
}

/// Namespaced like `PANEL_TAB_KEY`, so the app's localStorage keys stay recognisably ours.
export const LIST_SORT_KEY = "diariz.recordings.sort";

/// The order the list has always shown: whatever `Position` the server returns.
export const DEFAULT_SORT: ListSort = { key: "manual", dir: "asc" };

const KEYS: readonly SortKey[] = ["manual", "date", "name", "duration"];
const DIRS: readonly SortDir[] = ["asc", "desc"];

/// Anything unparseable or unrecognised reads as the default - a first visit, a value from an older build,
/// and a hand-edited key all have to leave the list with an order it can render.
export function parseSort(raw: string | null): ListSort {
  if (!raw) return DEFAULT_SORT;
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return DEFAULT_SORT;
  }
  if (typeof parsed !== "object" || parsed === null) return DEFAULT_SORT;
  const { key, dir } = parsed as { key?: unknown; dir?: unknown };
  if (!KEYS.includes(key as SortKey) || !DIRS.includes(dir as SortDir)) return DEFAULT_SORT;
  return { key: key as SortKey, dir: dir as SortDir };
}

const displayName = (r: RecordingSummary) => r.name ?? r.title;

/// Ascending comparator per key. `desc` is this reversed rather than a second set of comparators, so the
/// two directions cannot disagree about ties.
function compare(a: RecordingSummary, b: RecordingSummary, key: SortKey): number {
  switch (key) {
    case "date":
      return new Date(a.createdAt).getTime() - new Date(b.createdAt).getTime();
    case "name":
      return displayName(a).localeCompare(displayName(b));
    case "duration":
      return a.durationMs - b.durationMs;
    default:
      return 0;
  }
}

/// A **new** array in the chosen order. Manual returns a copy in the input's order: the caller renders this
/// and keeps the original for its reorder writes, so mutating in place would corrupt the very list the
/// display-only rule exists to protect.
export function sortRecordings(items: RecordingSummary[], sort: ListSort): RecordingSummary[] {
  if (sort.key === "manual") return [...items];
  const sign = sort.dir === "desc" ? -1 : 1;
  return [...items].sort((a, b) => sign * compare(a, b, sort.key));
}

/// The setting, persisted globally. One preference for the whole app: a user who wants newest-first wants it
/// in every folder and every room, and per-folder state would make the same list sort differently as they
/// drill. Storage being unavailable (private mode) is not an error - the sort still moves for this session,
/// it just will not survive a reload.
export function useListSort(): [ListSort, (next: ListSort) => void] {
  const [sort, setSort] = useState<ListSort>(() => {
    try {
      return parseSort(localStorage.getItem(LIST_SORT_KEY));
    } catch {
      return DEFAULT_SORT;
    }
  });

  function update(next: ListSort) {
    setSort(next);
    try {
      localStorage.setItem(LIST_SORT_KEY, JSON.stringify(next));
    } catch {
      /* storage disabled: the sort still applies this session */
    }
  }

  return [sort, update];
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd apps/web && npm test -- --run src/lib/listSort.test.ts`
Expected: PASS, all tests green.

- [ ] **Step 5: Commit**

```bash
git add apps/web/src/lib/listSort.ts apps/web/src/lib/listSort.test.ts
git commit -m "feat(web): add the recordings list sort model"
```

---

### Task 2: The row's date/time format (`formatListDateTime`)

**Files:**
- Modify: `apps/web/src/lib/format.ts` (append after `formatTimeHm`, around line 50)
- Test: `apps/web/src/lib/format.test.ts` (exists - append a `describe` block)

**Interfaces:**
- Consumes: `formatTimeHm` from the same module.
- Produces: `function formatListDateTime(iso: string, locale: string | undefined, todayLabel: string, now?: Date): string`

- [ ] **Step 1: Write the failing test**

Append to `apps/web/src/lib/format.test.ts` (import `formatListDateTime` alongside the existing imports at the top of the file):

```ts
describe("formatListDateTime", () => {
  // Built from local components, not an ISO literal: the output's time comes from the local clock, so a
  // UTC literal would make these assertions depend on the machine's timezone.
  const at = (y: number, m: number, d: number, h: number, min: number) =>
    new Date(y, m, d, h, min).toISOString();
  const now = new Date(2026, 7, 11, 9, 0); // 11 Aug 2026, 09:00 local

  it("labels today with the time only", () => {
    expect(formatListDateTime(at(2026, 7, 11, 14, 30), "en", "Today", now)).toBe("Today 14:30");
  });

  it("shows day and month for an earlier day this year", () => {
    expect(formatListDateTime(at(2026, 7, 3, 14, 30), "en", "Today", now)).toBe("3 Aug 14:30");
  });

  it("adds the year outside the current year", () => {
    expect(formatListDateTime(at(2025, 7, 11, 14, 30), "en", "Today", now)).toBe("11 Aug 2025 14:30");
  });

  // Same month and day, different year: without a year comparison this would read as "Today".
  it("does not call the same date in another year today", () => {
    expect(formatListDateTime(at(2025, 7, 11, 8, 5), "en", "Today", now)).toBe("11 Aug 2025 08:05");
  });

  it("treats yesterday across a year boundary as another year", () => {
    const newYear = new Date(2026, 0, 1, 9, 0);
    expect(formatListDateTime(at(2025, 11, 31, 23, 15), "en", "Today", newYear)).toBe("31 Dec 2025 23:15");
  });

  // Day-first is fixed by hand rather than left to Intl, which renders "Aug 11" for en-US. Only the month
  // name is localised.
  it("localises the month name but keeps the day first", () => {
    expect(formatListDateTime(at(2026, 7, 3, 14, 30), "fr", "Aujourd'hui", now)).toMatch(/^3 ao/);
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd apps/web && npm test -- --run src/lib/format.test.ts`
Expected: FAIL - `formatListDateTime is not a function` (or an unresolved import).

- [ ] **Step 3: Write minimal implementation**

Add to `apps/web/src/lib/format.ts`, directly below `formatTimeHm`:

```ts
/// A recording's timestamp as the list rows show it: "Today 14:30", "11 Aug 14:30", or "11 Aug 2025 14:30"
/// once the year stops being obvious. The time is always fixed 24-hour.
///
/// The day is placed **before** the localised month by hand rather than through `Intl`'s own ordering, which
/// renders "Aug 11" for en-US - `formatLongDate` above composes English the same way and for the same reason.
/// `todayLabel` arrives already translated so this module stays free of i18n, and `now` is injectable so the
/// tests are deterministic (as `formatRelativeTime` does).
export function formatListDateTime(
  iso: string,
  locale: string | undefined,
  todayLabel: string,
  now: Date = new Date(),
): string {
  const d = new Date(iso);
  const time = formatTimeHm(iso);
  const sameYear = d.getFullYear() === now.getFullYear();
  if (sameYear && d.getMonth() === now.getMonth() && d.getDate() === now.getDate()) {
    return `${todayLabel} ${time}`;
  }
  const month = d.toLocaleDateString(locale || undefined, { month: "short" });
  const year = sameYear ? "" : ` ${d.getFullYear()}`;
  return `${d.getDate()} ${month}${year} ${time}`;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd apps/web && npm test -- --run src/lib/format.test.ts`
Expected: PASS, including the pre-existing formatting tests.

- [ ] **Step 5: Commit**

```bash
git add apps/web/src/lib/format.ts apps/web/src/lib/format.test.ts
git commit -m "feat(web): add the list row date/time format"
```

---

### Task 3: The `SortMenu` control

The dropdown plus its direction toggle, and the i18n keys they need. Not yet mounted anywhere.

**Files:**
- Create: `apps/web/src/components/nav/SortMenu.tsx`
- Create: `apps/web/src/components/nav/SortMenu.test.tsx`
- Modify: `apps/web/src/locales/en/workspace.json`, `.../de/workspace.json`, `.../es/workspace.json`, `.../fr/workspace.json`

**Interfaces:**
- Consumes: `ListSort`, `SortKey`, `SortDir` from `lib/listSort` (Task 1); `ChevronDownIcon` from `components/icons`.
- Produces: default export `SortMenu({ sort, onChange }: { sort: ListSort; onChange: (next: ListSort) => void })`

- [ ] **Step 1: Add the i18n keys**

Add to `apps/web/src/locales/en/workspace.json` (keep the file's existing alphabetical-ish grouping; placing them next to the other list keys is fine):

```json
  "sortBy": "Sort by",
  "sortManual": "Manual",
  "sortDate": "Date/Time",
  "sortName": "Name",
  "sortDuration": "Duration",
  "sortAscending": "Ascending",
  "sortDescending": "Descending",
  "today": "Today",
```

`de/workspace.json`:

```json
  "sortBy": "Sortieren nach",
  "sortManual": "Manuell",
  "sortDate": "Datum/Zeit",
  "sortName": "Name",
  "sortDuration": "Dauer",
  "sortAscending": "Aufsteigend",
  "sortDescending": "Absteigend",
  "today": "Heute",
```

`es/workspace.json`:

```json
  "sortBy": "Ordenar por",
  "sortManual": "Manual",
  "sortDate": "Fecha/Hora",
  "sortName": "Nombre",
  "sortDuration": "Duración",
  "sortAscending": "Ascendente",
  "sortDescending": "Descendente",
  "today": "Hoy",
```

`fr/workspace.json`:

```json
  "sortBy": "Trier par",
  "sortManual": "Manuel",
  "sortDate": "Date/Heure",
  "sortName": "Nom",
  "sortDuration": "Durée",
  "sortAscending": "Croissant",
  "sortDescending": "Décroissant",
  "today": "Aujourd'hui",
```

The catalogs are UTF-8 and already accented throughout (250 accented values in `es`, 325 in `fr`), so write the accents as shown. Save all four files as UTF-8 without a BOM.

Note: `today` is consumed in Task 5, not here. It is added now so the four catalogs are edited once.

- [ ] **Step 2: Write the failing test**

Create `apps/web/src/components/nav/SortMenu.test.tsx`:

```tsx
import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi } from "vitest";
import SortMenu from "./SortMenu";
import type { ListSort } from "../../lib/listSort";

function renderMenu(sort: ListSort) {
  const onChange = vi.fn();
  render(<SortMenu sort={sort} onChange={onChange} />);
  return onChange;
}

describe("SortMenu", () => {
  it("offers manual, date, name and duration", () => {
    renderMenu({ key: "manual", dir: "asc" });
    const select = screen.getByRole("combobox", { name: /sort by/i }) as HTMLSelectElement;
    expect([...select.options].map((o) => o.value)).toEqual(["manual", "date", "name", "duration"]);
  });

  it("shows the current key as the selection", () => {
    renderMenu({ key: "duration", dir: "asc" });
    expect((screen.getByRole("combobox", { name: /sort by/i }) as HTMLSelectElement).value).toBe("duration");
  });

  it("reports a key change, keeping the direction", () => {
    const onChange = renderMenu({ key: "manual", dir: "desc" });
    fireEvent.change(screen.getByRole("combobox", { name: /sort by/i }), { target: { value: "name" } });
    expect(onChange).toHaveBeenCalledWith({ key: "name", dir: "desc" });
  });

  /// There is no direction for "the order you arranged them in", and a live control that changes nothing
  /// is worse than an absent one.
  it("hides the direction toggle under manual", () => {
    renderMenu({ key: "manual", dir: "asc" });
    expect(screen.queryByRole("button", { name: /ascending|descending/i })).toBeNull();
  });

  it("shows the direction toggle once a key is chosen", () => {
    renderMenu({ key: "name", dir: "asc" });
    expect(screen.getByRole("button", { name: /ascending/i })).toBeTruthy();
  });

  it("flips the direction, keeping the key", () => {
    const onChange = renderMenu({ key: "name", dir: "asc" });
    fireEvent.click(screen.getByRole("button", { name: /ascending/i }));
    expect(onChange).toHaveBeenCalledWith({ key: "name", dir: "desc" });
  });

  it("flips back from descending", () => {
    const onChange = renderMenu({ key: "date", dir: "desc" });
    fireEvent.click(screen.getByRole("button", { name: /descending/i }));
    expect(onChange).toHaveBeenCalledWith({ key: "date", dir: "asc" });
  });
});
```

- [ ] **Step 3: Run test to verify it fails**

Run: `cd apps/web && npm test -- --run src/components/nav/SortMenu.test.tsx`
Expected: FAIL - `Failed to resolve import "./SortMenu"`.

- [ ] **Step 4: Write minimal implementation**

Create `apps/web/src/components/nav/SortMenu.tsx`:

```tsx
import { useTranslation } from "react-i18next";
import { ChevronDownIcon } from "../icons";
import type { ListSort, SortKey } from "../../lib/listSort";

const KEYS: SortKey[] = ["manual", "date", "name", "duration"];

/// How the List tab's recordings are ordered: the key, plus a direction toggle beside it.
///
/// A native `<select>` rather than a custom popover - it is the narrowest control that holds four labels in
/// a panel this width, and it is keyboard- and screen-reader-correct without any work of its own.
///
/// The direction toggle is absent under Manual: there is no direction for the order you arranged by hand.
export default function SortMenu({
  sort,
  onChange,
}: {
  sort: ListSort;
  onChange: (next: ListSort) => void;
}) {
  const { t } = useTranslation("workspace");
  const label: Record<SortKey, string> = {
    manual: t("sortManual"),
    date: t("sortDate"),
    name: t("sortName"),
    duration: t("sortDuration"),
  };
  const ascending = sort.dir === "asc";

  return (
    <div className="flex shrink-0 items-center gap-1">
      <select
        aria-label={t("sortBy")}
        title={t("sortBy")}
        value={sort.key}
        onChange={(e) => onChange({ key: e.target.value as SortKey, dir: sort.dir })}
        className="rounded border bg-gray-50 px-1 py-0.5 text-[11px] text-gray-600 outline-none focus:ring-2 focus:ring-blue-500 dark:border-gray-700 dark:bg-gray-800 dark:text-gray-300"
      >
        {KEYS.map((key) => (
          <option key={key} value={key}>
            {label[key]}
          </option>
        ))}
      </select>
      {sort.key !== "manual" && (
        <button
          type="button"
          aria-label={ascending ? t("sortAscending") : t("sortDescending")}
          title={ascending ? t("sortAscending") : t("sortDescending")}
          onClick={() => onChange({ key: sort.key, dir: ascending ? "desc" : "asc" })}
          className="rounded border px-1 py-0.5 text-gray-500 hover:bg-gray-100 dark:border-gray-700 dark:text-gray-400 dark:hover:bg-gray-800"
        >
          {/* One glyph rotated, rather than two icons: the pair can never drift apart, and the app has no
              up-chevron of its own. The span carries the rotation because the icons take no className. */}
          <span className={`block ${ascending ? "rotate-180" : ""}`}>
            <ChevronDownIcon size={12} />
          </span>
        </button>
      )}
    </div>
  );
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `cd apps/web && npm test -- --run src/components/nav/SortMenu.test.tsx src/locales.test.ts`
Expected: PASS both - the locale gate confirms the four catalogs agree.

- [ ] **Step 6: Commit**

```bash
git add apps/web/src/components/nav/SortMenu.tsx apps/web/src/components/nav/SortMenu.test.tsx apps/web/src/locales/en/workspace.json apps/web/src/locales/de/workspace.json apps/web/src/locales/es/workspace.json apps/web/src/locales/fr/workspace.json
git commit -m "feat(web): add the list Sort by control"
```

---

### Task 4: A trailing slot on the search line

`SearchBar` makes room on its header line for a control it does not own.

**Files:**
- Modify: `apps/web/src/components/nav/SearchBar.tsx:24-39` (props), `:96-144` (header row)
- Test: `apps/web/src/components/nav/SearchBar.test.tsx` (exists - append)

**Interfaces:**
- Produces: `SearchBar` gains an optional prop `trailing?: React.ReactNode`.

- [ ] **Step 1: Write the failing test**

Append to `apps/web/src/components/nav/SearchBar.test.tsx`, inside the existing `describe("SearchBar", ...)` block:

```tsx
  /// The sort control shares the search line. SearchBar keeps ownership of the row so the field and the
  /// control cannot drift apart in two files.
  it("renders a trailing control on the search line", () => {
    renderBar({ trailing: <button type="button">sort-here</button> });

    const control = screen.getByRole("button", { name: "sort-here" });
    const field = screen.getByRole("searchbox");
    // Same row: the field's rounded box and the control are siblings under one flex line.
    expect(control.parentElement).toBe(field.closest("div")!.parentElement);
  });
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd apps/web && npm test -- --run src/components/nav/SearchBar.test.tsx`
Expected: FAIL - the `sort-here` button is not found (the prop is ignored).

Note: TypeScript will also reject `trailing` in the test's props spread. That is the compile-time half of the same failure; both go away in Step 3.

- [ ] **Step 3: Write minimal implementation**

In `apps/web/src/components/nav/SearchBar.tsx`, add the prop to the signature (after `scopeName`):

```tsx
  /// Rendered on the search line, right of the field - the panel's Sort by control. The field gives up the
  /// width for it. Kept as a slot rather than a hard dependency: this component knows about searching, not
  /// about how the list below it is ordered.
  trailing,
```

and to the type:

```tsx
  trailing?: React.ReactNode;
```

Then wrap the header row's contents (the `<div className="shrink-0 border-b px-2 py-1.5 ...">` at line 96) so the field box and the slot sit side by side:

```tsx
      <div className="flex shrink-0 items-center gap-1.5 border-b px-2 py-1.5 dark:border-gray-800">
        <div className="flex min-w-0 flex-1 items-center gap-1.5 rounded-lg border bg-gray-50 px-2 py-1 focus-within:ring-2 focus-within:ring-blue-500 dark:border-gray-700 dark:bg-gray-800">
          ...unchanged field contents...
        </div>
        {trailing}
      </div>
```

The only changes to the existing markup are `flex items-center gap-1.5` moving onto the outer div and `min-w-0 flex-1` being added to the field's box - so the field shrinks and the slot keeps its natural width.

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd apps/web && npm test -- --run src/components/nav/SearchBar.test.tsx`
Expected: PASS, including every pre-existing SearchBar test.

- [ ] **Step 5: Commit**

```bash
git add apps/web/src/components/nav/SearchBar.tsx apps/web/src/components/nav/SearchBar.test.tsx
git commit -m "feat(web): give the search line a trailing slot"
```

---

### Task 5: The row shows date and time, and can decline to reorder

**Files:**
- Modify: `apps/web/src/components/nav/RecordingRow.tsx` (props at `:46-72`, drop handlers at `:97-103`, the duration span at `:151-154`)
- Create: `apps/web/src/components/nav/RecordingRow.test.tsx`

**Interfaces:**
- Consumes: `formatListDateTime` from `lib/format` (Task 2); the `workspace:today` key (Task 3).
- Produces: `RecordingRow`'s `onDropBefore` becomes optional - `onDropBefore?: (draggedId: string) => void`. When it is absent the row attaches no `onDragOver` / `onDrop` handler, so a drop bubbles to whatever is behind it.

- [ ] **Step 1: Write the failing test**

Create `apps/web/src/components/nav/RecordingRow.test.tsx`:

```tsx
import { render, screen, fireEvent } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter } from "react-router-dom";
import { describe, it, expect, vi } from "vitest";
import type { RecordingSummary } from "../../lib/types";

// The row's kebab menu pulls in useRecordingActions, which opens a MoveToSectionModal that lists sections.
// An absent method fails as an opaque crash rather than a clear assertion, so stub what it can reach.
vi.mock("../../lib/api", () => ({
  api: {
    renameRecording: vi.fn(),
    deleteRecording: vi.fn(),
    audioUrl: vi.fn(),
    listSections: vi.fn().mockResolvedValue([]),
    moveRecording: vi.fn(),
  },
  apiErrorMessage: (e: unknown) => String(e),
}));

// useRecordingActions reads useSharedRoomId as well as useRoomBasePath - a partial mock crashes on mount.
vi.mock("../../lib/rooms", () => ({
  useRoom: () => ({ currentRoom: { id: "p1", isPersonal: true }, can: () => true }),
  useRoomBasePath: () => "",
  useSharedRoomId: () => undefined,
}));

import { RecordingRow } from "./RecordingRow";

const rec: RecordingSummary = {
  id: "rec-1",
  title: "Mic 6/26/2026",
  name: "Weekly Standup",
  source: "System",
  durationMs: 9000, // 0:09
  status: "Transcribed",
  createdAt: new Date(2025, 7, 11, 14, 30).toISOString(),
  sectionId: null,
  sectionName: null,
  hasActions: false,
  hasAudio: true,
  calendarEventId: null,
};

function renderRow(props: Partial<React.ComponentProps<typeof RecordingRow>> = {}) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <MemoryRouter>
        <ul>
          <RecordingRow
            r={rec}
            indentClass="pl-3"
            selectMode={false}
            selected={false}
            onToggleSelect={() => {}}
            {...props}
          />
        </ul>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe("RecordingRow", () => {
  /// The row's right-hand column answers "when was this?", which is what people scan a list for. The
  /// duration is still one hover away.
  it("shows the recording's date and time", () => {
    renderRow();
    expect(screen.getByText("11 Aug 2025 14:30")).toBeTruthy();
  });

  it("no longer shows the duration in the row", () => {
    renderRow();
    expect(screen.queryByText("0:09")).toBeNull();
  });

  it("keeps the duration in the hover title", () => {
    renderRow();
    expect(screen.getByRole("link").getAttribute("title")).toContain("0:09");
  });

  it("reorders when a drop handler is supplied", () => {
    const onDropBefore = vi.fn();
    renderRow({ onDropBefore });

    fireEvent.drop(screen.getByRole("link").closest("li")!, { dataTransfer: { getData: () => "other" } });

    expect(onDropBefore).toHaveBeenCalledWith("other");
  });

  /// Reordering is switched off while the list is sorted. The row must then be *transparent* to the drop,
  /// not swallow it - the level behind it appends the recording instead.
  it("lets a drop through when no drop handler is supplied", () => {
    const onBackgroundDrop = vi.fn();
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={qc}>
        <MemoryRouter>
          <div onDrop={onBackgroundDrop}>
            <ul>
              <RecordingRow
                r={rec}
                indentClass="pl-3"
                selectMode={false}
                selected={false}
                onToggleSelect={() => {}}
              />
            </ul>
          </div>
        </MemoryRouter>
      </QueryClientProvider>,
    );

    fireEvent.drop(screen.getByRole("link").closest("li")!, { dataTransfer: { getData: () => "other" } });

    expect(onBackgroundDrop).toHaveBeenCalled();
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd apps/web && npm test -- --run src/components/nav/RecordingRow.test.tsx`
Expected: FAIL - "11 Aug 2025 14:30" is not found (the row renders `0:09`), and TypeScript rejects the omitted `onDropBefore`.

- [ ] **Step 3: Write minimal implementation**

In `apps/web/src/components/nav/RecordingRow.tsx`:

1. Swap the import: `formatDuration` stays (the title still uses it), and add `formatListDateTime`:

```tsx
import { formatDuration, formatListDateTime } from "../../lib/format";
```

2. Make `onDropBefore` optional in both the destructure and the type:

```tsx
  /// Insert the dragged recording before this one. **Optional**: the List tab omits it while a non-manual
  /// sort is active, because a reorder would write a `Position` the sorted view cannot show - the row would
  /// spring back and read as a broken drag. With no handler the row attaches no drop listener at all, so the
  /// drop bubbles to the level behind it (which appends) instead of being swallowed.
  onDropBefore?: (draggedId: string) => void;
```

3. Make the `<li>`'s drag listeners conditional - replace the `onDragOver` / `onDrop` props with a spread:

```tsx
      {...(onDropBefore
        ? {
            onDragOver: (e: React.DragEvent) => e.preventDefault(),
            onDrop: (e: React.DragEvent) => {
              if (e.dataTransfer.files?.length) return; // a file upload - let it bubble to the panel drop zone
              e.preventDefault();
              e.stopPropagation(); // don't also trigger the group's append-drop
              onDropBefore(e.dataTransfer.getData("text/plain"));
            },
          }
        : {})}
```

4. Add the duration to the hover title and replace the right-hand span:

```tsx
            title={`${r.name ?? r.title} - ${sourceLabel(r.source, t)} · ${new Date(r.createdAt).toLocaleDateString(i18n.language)} · ${formatDuration(r.durationMs)}`}
```

```tsx
            {/* When, right-aligned (tabular-nums) so the column lines up down the list. The duration moved
                into the hover title above: "when was this?" is what a list is scanned for. */}
            <span className="shrink-0 tabular-nums text-xs text-gray-500 dark:text-gray-400">
              {formatListDateTime(r.createdAt, i18n.language, t("workspace:today"))}
            </span>
```

Note the title's separator changes from an em dash to a plain hyphen, per the global constraint.

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd apps/web && npm test -- --run src/components/nav/RecordingRow.test.tsx`
Expected: PASS.

- [ ] **Step 5: Run the suites that render this row**

Run: `cd apps/web && npm test -- --run src/components/nav/CalendarTab.test.tsx src/components/nav/TagsTab.test.tsx src/components/RecordingsPanel.test.tsx`
Expected: PASS. If a test asserted on a row's duration text, update it to the date/time - the row deliberately says the same thing everywhere.

- [ ] **Step 6: Commit**

```bash
git add apps/web/src/components/nav/RecordingRow.tsx apps/web/src/components/nav/RecordingRow.test.tsx
git commit -m "feat(web): show the date and time on recording rows"
```

(If Step 5 required edits to CalendarTab/TagsTab/RecordingsPanel tests, stage those files in this commit too.)

---

### Task 6: Wire the sort into the panel

**Files:**
- Modify: `apps/web/src/components/RecordingsPanel.tsx` (imports at `:19-22`, `rowList` at `:211-229`, the level derivation at `:234-238`, the `<SearchBar>` mount at `:286-292`)
- Test: `apps/web/src/components/RecordingsPanel.test.tsx` (exists - append a `describe` block)

**Interfaces:**
- Consumes: `useListSort`, `sortRecordings` (Task 1); `SortMenu` (Task 3); `SearchBar`'s `trailing` prop (Task 4); `RecordingRow`'s optional `onDropBefore` (Task 5).

- [ ] **Step 1: Write the failing test**

Append to `apps/web/src/components/RecordingsPanel.test.tsx`:

```tsx
/// Sorting is a **view**. The order the user arranged by hand is what every reorder write is measured
/// against, so it has to survive being looked at in a different order.
describe("RecordingsPanel sorting", () => {
  // The names are chosen so that alphabetical order is the REVERSE of the manual order. That is what makes
  // the drop assertion below able to fail: with the ids appended, a manual [a, b, c] yields ["a","b","c"]
  // while a leaked sorted [b, c, a] yields ["b","a","c"]. Fixtures where the two coincide would let a real
  // bug through.
  const three = [
    { ...rec, id: "a", name: "Gamma", durationMs: 3000, createdAt: new Date("2026-01-02T09:00:00Z").toISOString() },
    { ...rec, id: "b", name: "Alpha", durationMs: 1000, createdAt: new Date("2026-03-04T09:00:00Z").toISOString() },
    { ...rec, id: "c", name: "Beta", durationMs: 2000, createdAt: new Date("2026-02-03T09:00:00Z").toISOString() },
  ];

  // Read the rendered order off the row links' hrefs rather than their text: the row now shows a name AND a
  // date, and splitting that string back apart is exactly the kind of assertion that quietly stops testing
  // anything when the format changes.
  const orderedIds = () =>
    screen
      .getAllByRole("link")
      .map((el) => el.getAttribute("href") ?? "")
      .filter((href) => href.includes("/recordings/"))
      .map((href) => href.split("/recordings/")[1].split("?")[0]);

  const sortBy = (value: string) =>
    fireEvent.change(screen.getByRole("combobox", { name: /sort by/i }), { target: { value } });

  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    roomStub.currentRoom = { id: "p1", isPersonal: true };
    roomStub.canManageContents = true;
    (api.listSections as ReturnType<typeof vi.fn>).mockResolvedValue([]);
    (api.listRecordings as ReturnType<typeof vi.fn>).mockResolvedValue(three);
    (api.reorderRecordings as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
  });

  it("starts on manual, in the order the server returned", async () => {
    renderList();
    await screen.findByText("Gamma");

    expect((screen.getByRole("combobox", { name: /sort by/i }) as HTMLSelectElement).value).toBe("manual");
    expect(orderedIds()).toEqual(["a", "b", "c"]);
  });

  it("reorders the rows when a sort is chosen", async () => {
    renderList();
    await screen.findByText("Gamma");

    sortBy("duration"); // b=1000, c=2000, a=3000

    expect(orderedIds()).toEqual(["b", "c", "a"]);
  });

  it("reverses on the direction toggle", async () => {
    renderList();
    await screen.findByText("Gamma");

    sortBy("duration");
    fireEvent.click(screen.getByRole("button", { name: /ascending/i }));

    expect(orderedIds()).toEqual(["a", "c", "b"]);
  });

  it("sorts by date", async () => {
    renderList();
    await screen.findByText("Gamma");

    sortBy("date"); // a=2 Jan, c=3 Feb, b=4 Mar

    expect(orderedIds()).toEqual(["a", "c", "b"]);
  });

  it("persists the choice", async () => {
    renderList();
    await screen.findByText("Gamma");

    sortBy("name");

    expect(JSON.parse(localStorage.getItem("diariz.recordings.sort")!)).toEqual({ key: "name", dir: "asc" });
  });

  it("restores a persisted sort on mount", async () => {
    localStorage.setItem("diariz.recordings.sort", JSON.stringify({ key: "name", dir: "desc" }));
    renderList();
    await screen.findByText("Gamma");

    expect(orderedIds()).toEqual(["a", "c", "b"]); // Gamma, Beta, Alpha
  });

  /// Folders have no duration and no date of their own, so a folder block that reacted to Name alone would
  /// be inconsistent across the three keys. They keep their manual order under every setting.
  it("leaves the folder rows in their manual order", async () => {
    (api.listSections as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "s1", name: "Zulu", parentId: null, position: 0 },
      { id: "s2", name: "Alpha Folder", parentId: null, position: 1 },
    ]);
    renderList();
    await screen.findByText("Zulu");

    sortBy("name");

    const folders = screen.getAllByRole("button", { name: /open folder/i }).map((el) => el.textContent);
    expect(folders[0]).toContain("Zulu");
    expect(folders[1]).toContain("Alpha Folder");
  });

  /// A drop would write a Position the sorted view cannot show, so the row springs back and the drag reads
  /// as broken. Off while sorted; back the moment Manual returns.
  it("does not reorder within the level while sorted", async () => {
    renderList();
    await screen.findByText("Gamma");
    sortBy("name"); // display becomes b (Alpha), c (Beta), a (Gamma)

    // Drop "c" onto the "Alpha" row (b). Sorted, this must NOT insert c before b.
    // getData must answer per type: the level-background handler asks for the section MIME first, and a
    // dataTransfer that returns "c" for every type would be read as a folder drag instead of a recording.
    fireEvent.drop(screen.getByText("Alpha").closest("li")!, {
      dataTransfer: { getData: (type: string) => (type === "text/plain" ? "c" : "") },
    });

    await waitFor(() => expect(api.reorderRecordings).toHaveBeenCalled());
    // It bubbled past the row to the level behind it, which appends - and the list it appended into is the
    // MANUAL order [a, b, c], giving ["a","b","c"]. Had the sorted order leaked into the write it would read
    // ["b","a","c"], and one drag would have silently rewritten every Position in this folder.
    expect(api.reorderRecordings).toHaveBeenCalledWith(null, ["a", "b", "c"], undefined);
  });

  it("reorders again once manual is restored", async () => {
    renderList();
    await screen.findByText("Gamma");
    sortBy("name");
    sortBy("manual");

    // Manual display is a (Gamma), b (Alpha), c (Beta); dropping c onto b inserts it before b.
    fireEvent.drop(screen.getByText("Alpha").closest("li")!, {
      dataTransfer: { getData: (type: string) => (type === "text/plain" ? "c" : "") },
    });

    await waitFor(() => expect(api.reorderRecordings).toHaveBeenCalledWith(null, ["a", "c", "b"], undefined));
  });
});
```

The two drop assertions are the ones that matter, and they are written so they *can* fail: Step 5 below mutates the implementation to prove it.

If `getAllByRole("button", { name: /open folder/i })` does not match, check `SectionRow`'s actual aria-label in `apps/web/src/components/nav/SectionRow.tsx` and use that wording - do not weaken the query to something that would pass regardless of order.

- [ ] **Step 2: Run test to verify it fails**

Run: `cd apps/web && npm test -- --run src/components/RecordingsPanel.test.tsx -t "sorting"`
Expected: FAIL - no combobox named "Sort by" exists.

- [ ] **Step 3: Write minimal implementation**

In `apps/web/src/components/RecordingsPanel.tsx`:

1. Add imports:

```tsx
import SortMenu from "./nav/SortMenu";
import { useListSort, sortRecordings } from "../lib/listSort";
```

2. Add the hook beside the other panel state (near `searchQuery`, around line 103):

```tsx
  // How the level below is ordered. Persisted globally - see lib/listSort.
  const [sort, setSort] = useListSort();
  const sorted = sort.key !== "manual";
```

3. In `rowList`, render the sorted items but keep passing the **unsorted** ids, and drop the reorder handler while sorted:

```tsx
  /// `items` arrives in manual order. It is sorted for display only: `ids` - which becomes the recordings'
  /// new server Positions via computeReorder - stays exactly as the server returned it. See lib/listSort.
  const rowList = (sectionId: string | null, items: RecordingSummary[]) => {
    const ids = items.map((i) => i.id);
    return (
      <ul className="divide-y dark:divide-gray-800">
        {sortRecordings(items, sort).map((r) => (
          <RecordingRow
            key={r.id}
            r={r}
            indentClass="pl-3"
            selectMode={selection.selectMode}
            selected={selection.selectedIds.includes(r.id)}
            onToggleSelect={() => selection.toggle(r.id)}
            // Omitted while sorted: a reorder would write a Position this view cannot show. The row then
            // passes the drop through to the level behind it, which appends.
            onDropBefore={sorted ? undefined : (draggedId) => drop(sectionId, ids, draggedId, r.id)}
            cut={cutRecordingIds.includes(r.id)}
          />
        ))}
      </ul>
    );
  };
```

4. Leave `level`, `levelIds` and the level-background `onDrop` **exactly as they are** (lines 234-238 and 328-338). They are the manual order, which is the point.

5. Mount the control in the search line:

```tsx
            <SearchBar
              roomId={roomId}
              sectionId={drill.sectionId}
              scopeName={currentLevelName}
              onQueryChange={setSearchQuery}
              onDrill={(id) => drill.drillTo(id)}
              trailing={<SortMenu sort={sort} onChange={setSort} />}
            />
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd apps/web && npm test -- --run src/components/RecordingsPanel.test.tsx`
Expected: PASS, including all pre-existing panel tests.

- [ ] **Step 5: Mutation-verify the invariant**

Temporarily change `levelIds` to `sortRecordings(level.items, sort).map((i) => i.id)` and re-run.
Expected: the "does not reorder within the level while sorted" test FAILS. Revert the change and confirm it passes again. A test that cannot fail is not a test.

- [ ] **Step 6: Commit**

```bash
git add apps/web/src/components/RecordingsPanel.tsx apps/web/src/components/RecordingsPanel.test.tsx
git commit -m "feat(web): sort the recordings list by date, name or duration"
```

---

### Task 7: The folder icon becomes the Change folder button

**Files:**
- Modify: `apps/web/src/components/detail/FolderChips.tsx:23-56`
- Modify: `apps/web/src/pages/RecordingDetail.tsx:1658-1680`
- Test: `apps/web/src/pages/RecordingDetail.test.tsx` (exists - the `describe("RecordingDetail folder chips")` block)
- Test: `apps/web/src/components/detail/FolderChips.test.tsx` (exists - append)

**Interfaces:**
- Produces: `FolderChips` gains `showIcon?: boolean` (default `true`).

- [ ] **Step 1: Write the failing tests**

Append to `apps/web/src/components/detail/FolderChips.test.tsx` (match the file's existing render helper):

```tsx
  /// The detail page renders the icon itself, as the Change folder button. Two adjacent folder glyphs would
  /// blur which control is which.
  it("can omit its leading folder icon", () => {
    renderChips({ showIcon: false });
    expect(screen.queryByTestId("folder-chips-icon")).toBeNull();
  });
```

For that to be assertable, the icon's wrapper span in `FolderChips` gains `data-testid="folder-chips-icon"`.

Then, in `apps/web/src/pages/RecordingDetail.test.tsx`, inside `describe("RecordingDetail folder chips", ...)`, replace nothing and append:

```tsx
  /// The affordance moved onto the glyph that opens the path: one control, where the path begins.
  it("uses the folder icon as the Change folder button", async () => {
    renderInRoom(inRoom("acme"));

    const button = await screen.findByRole("button", { name: /change folder/i });
    expect(button.querySelector("svg")).toBeTruthy();
    expect(button.textContent?.trim()).toBe("");
  });
```

The three `Change folder` tests already in this block - that the button exists, that it opens the picker, and that it stays outside the navigation landmark - are the regression net for this change and must keep passing unedited.

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd apps/web && npm test -- --run src/components/detail/FolderChips.test.tsx src/pages/RecordingDetail.test.tsx`
Expected: FAIL - `showIcon` is not a prop, and the Change folder button contains the text "Change folder" rather than an svg.

- [ ] **Step 3: Write minimal implementation**

In `apps/web/src/components/detail/FolderChips.tsx`, add the prop and gate the icon:

```tsx
  /// Whether to render the leading folder glyph. The recording detail page passes `false` and renders it
  /// itself, as the Change folder button - changing the path is an action *on* the path, so it stays outside
  /// this navigation landmark, and two adjacent folder glyphs would blur which control is which.
  showIcon = true,
```

```tsx
  showIcon?: boolean;
```

```tsx
      {showIcon && (
        <span
          data-testid="folder-chips-icon"
          style={color ? ({ "--sc-light": color.light, "--sc-dark": color.dark } as React.CSSProperties) : undefined}
          className={`shrink-0 ${color ? "text-[var(--sc-light)] dark:text-[var(--sc-dark)]" : "text-gray-400 dark:text-gray-500"}`}
          aria-hidden
        >
          <FolderIcon size={14} />
        </span>
      )}
```

In `apps/web/src/pages/RecordingDetail.tsx`, add the `sectionColor` import if it is not already there:

```tsx
import { sectionColor } from "../lib/sectionColors";
```

and replace the folder row (lines 1658-1680) with:

```tsx
      {folderPlacement && (
        // -mt-1 counteracts the hero's space-y-2.5 so the row sits tight under the name as part of the
        // title block.
        <div className="-mt-1 flex flex-wrap items-center gap-2">
          {/* The glyph the path opens with IS the way to change that path - one control where the reader is
              already looking, instead of a second button competing with it. Button chrome (border,
              background, hover tint) is what says it is clickable at all; the glyph keeps the deepest
              folder's colour, so the row still names its folder at a glance. It sits outside FolderChips'
              navigation landmark because changing the path is an action on the path, not a step in it. */}
          <button
            type="button"
            aria-label={t("workspace:changeFolder")}
            title={t("workspace:changeFolder")}
            onClick={() => setMoving(true)}
            style={folderIconColor ? ({ "--sc-light": folderIconColor.light, "--sc-dark": folderIconColor.dark } as React.CSSProperties) : undefined}
            className={`shrink-0 rounded-md border border-gray-200 bg-white p-1 hover:border-blue-300 hover:bg-blue-50 dark:border-gray-600 dark:bg-gray-800 dark:hover:border-blue-500 dark:hover:bg-blue-900/30 ${
              folderIconColor ? "text-[var(--sc-light)] dark:text-[var(--sc-dark)]" : "text-gray-400 dark:text-gray-500"
            }`}
          >
            <FolderIcon size={14} />
          </button>
          <FolderChips
            roomName={currentRoom?.name ?? ""}
            crumbs={folderCrumbs}
            onSelect={openFolderInList}
            showIcon={false}
          />
        </div>
      )}
```

Add `FolderIcon` to the page's `components/icons` import, and derive the colour beside `folderCrumbs` (near line 1067):

```tsx
  // The glyph takes the colour of the folder the recording is actually in - the same colour the panel gives
  // that folder's row - so the button still says *which* folder without any text. Neutral at the room's top
  // level, where there is no folder to take a colour from.
  const folderIconColor = folderCrumbs.length > 0 ? sectionColor(folderCrumbs[folderCrumbs.length - 1].id) : null;
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd apps/web && npm test -- --run src/components/detail/FolderChips.test.tsx src/pages/RecordingDetail.test.tsx`
Expected: PASS, including the four pre-existing Change folder tests.

- [ ] **Step 5: Commit**

```bash
git add apps/web/src/components/detail/FolderChips.tsx apps/web/src/components/detail/FolderChips.test.tsx apps/web/src/pages/RecordingDetail.tsx apps/web/src/pages/RecordingDetail.test.tsx
git commit -m "feat(web): make the breadcrumb's folder icon the Change folder button"
```

---

### Task 8: Release chores and documentation

**Files:**
- Modify: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`
- Modify: `apps/web/src/lib/releases.ts`
- Modify: `README.md`, `docs/features.md`
- Review: `apps/web/src/content/help/**` (the article covering the recordings list)

- [ ] **Step 1: Bump the version and its four mirrors to 0.207.0**

`version.json` becomes `{ "version": "0.207.0" }`. Set the same value in `apps/web/package.json`, `apps/desktop/package.json`, `integrations/n8n-nodes-diariz/package.json` (`"version"`) and `src/Diariz.Api/Diariz.Api.csproj` (`<Version>`).

- [ ] **Step 2: Find the PR number**

Run: `gh pr list --state all --limit 1 --json number` and use the next number. Do not assume "last + 1" from memory - Dependabot PRs and issues share the sequence. If the created PR ends up with a different number, correct `releases.ts` in a follow-up commit (the repo has precedent: `chore(release): record the PR number for ...`).

- [ ] **Step 3: Add the release entry**

Prepend to `RELEASES` in `apps/web/src/lib/releases.ts`:

```ts
  {
    version: "0.207.0",
    date: "2026-08-11",
    pr: <number from step 2>,
    headline: "Sort your recordings, and see when each one was made",
    summary:
      "The recordings list gains a Sort by control on the search line: keep your manual arrangement, or order by date and time, name or duration, ascending or descending. Your choice is remembered. Each row now shows when the recording was made instead of how long it runs - today's recordings read as \"Today 14:30\", older ones as \"11 Aug 14:30\" - with the duration still on hover. On a recording's page, the folder icon at the start of the breadcrumbs is now the Change folder button, replacing the separate one beside it.",
    added: [
      "A Sort by control on the recordings list: Manual, Date/Time, Name or Duration, each ascending or descending, remembered between visits.",
    ],
    changed: [
      "Recording rows show the date and time instead of the duration; the duration moved to the hover tooltip.",
      "The folder icon that starts a recording's breadcrumbs is now the Change folder button.",
      "Dragging to reorder is paused while a sort other than Manual is active, and returns with Manual.",
    ],
  },
```

- [ ] **Step 4: Update the About-box CAPABILITIES row**

In the same file, amend the recordings-list row of the `CAPABILITIES` table so it mentions sorting. Keep it to one concise line in the existing `| Feature | Description |` shape - do not add prose.

- [ ] **Step 5: Update the README and features.md in lockstep**

Amend the matching Features table row in `README.md` and the matching prose bullet in `docs/features.md`. Both, never one without the other.

- [ ] **Step 6: Review the help article**

Check `apps/web/src/content/help/**` for the article describing the recordings list. Two behaviours a user relies on changed: rows now show date/time rather than duration, and reordering by drag pauses while sorted. Update the article's prose if it states either; content is ASCII only and keeps its `title` / `summary` / `group` / `order` front matter.

`docs/Data_Schema.md` and `docs/Overall_Synopsis_of_Platform.md` need no edit - no schema, contract, dependency or deployment change.

- [ ] **Step 7: Run the full web suite**

Run: `cd apps/web && npm test -- --run && npm run build`
Expected: PASS. `releases.test.ts` confirms `RELEASES[0].version` equals `version.json`, `versionMirrors.test.ts` confirms all four mirrors, `helpContent.test.ts` confirms the help front matter, and `npm run build` typechecks.

- [ ] **Step 8: Check for stray em and en dashes**

Run from the repo root:

```bash
git diff main --stat && git diff main > /tmp/diff.txt && python -c "print([l for l in open('/tmp/diff.txt',encoding='utf-8') if ('—' in l or '–' in l) and l.startswith('+')])"
```

Expected: an empty list for user-facing strings. Decode UTF-8 explicitly as shown - piping `git diff` straight into python decodes as cp1252 on Windows and reports a false zero.

- [ ] **Step 9: Commit**

```bash
git add version.json apps/web/package.json apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj integrations/n8n-nodes-diariz/package.json apps/web/src/lib/releases.ts README.md docs/features.md
git commit -m "chore(release): 0.207.0"
```

(Stage any help-article file edited in Step 6 with this commit.)

- [ ] **Step 10: Push and open the PR**

```bash
git push -u origin feat/list-sort-and-row-dates
```

Then `gh pr create`, with a body covering what changed, the display-only sort invariant, and this line: **Deployment surface: server redeploy only - no desktop release needed** (no file under `apps/desktop/src`, `apps/desktop/build`, `electron-builder.config.js` or the desktop dependencies is touched; the `apps/desktop/package.json` version bump is lockstep only).

Never merge locally - `main` is branch-protected and the PR must pass CI.

---

## Verification

Before opening the PR:

```bash
cd apps/web && npm test -- --run && npm run build
```

Then confirm in the running app (the change is web-only, so the dev server is enough):

1. The List tab's search line shows the Sort by control to its right, and the search field is narrower.
2. Choosing Duration reorders the recordings; the arrow toggle reverses them; the folder rows above do not move.
3. Reloading the page keeps the sort.
4. With a sort active, dragging one row onto another does not reorder them; switching back to Manual restores it.
5. Rows read "Today 14:30" for today's recordings and "11 Aug 14:30" (with the year when older) otherwise; hovering shows the duration.
6. On a recording's page, the folder icon before the breadcrumbs looks like a button and opens the folder picker; there is no separate Change folder button.
