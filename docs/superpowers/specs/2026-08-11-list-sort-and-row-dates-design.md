# List sorting, row date/time, and the folder-icon affordance

Date: 2026-08-11

Three changes to how recordings are presented and how their folder is changed.

1. A **Sort by** control on the List tab's search line: Manual (today's behaviour, the default),
   Date/Time, Name and Duration, each with an Ascending/Descending modifier.
2. Recording rows show the **date and time** where they showed the duration, still right-aligned.
3. On the recording page, the **folder icon that opens the breadcrumb** becomes the Change folder
   control, replacing the separate text button.

## 1. Sort by

### Placement

`SearchBar` gains a `trailing?: ReactNode` slot rendered on its header line, to the right of the
field's rounded box. The field's container becomes `flex-1 min-w-0` so it yields width; the slot
content is `shrink-0`. `RecordingsPanel` passes the sort control into that slot.

The slot exists so the search line stays one component's responsibility. Lifting the whole line into
`RecordingsPanel` would split a row across two files and leave `SearchBar` rendering a results
overlay whose header lives somewhere else.

### The control

A new `components/nav/SortMenu.tsx`:

- a native `<select>` with four options - Manual, Date/Time, Name, Duration;
- a small icon button beside it toggling Ascending/Descending.

The direction toggle is **not rendered under Manual**: there is no direction for "the order you
arranged them in", and a live control that changes nothing is worse than an absent one.

Native `<select>` rather than a custom popover: it is the narrowest control that holds four labels in
a panel this width, and it is keyboard- and screen-reader-correct without any work of its own.

### State

New `lib/listSort.ts`:

```ts
export type SortKey = "manual" | "date" | "name" | "duration";
export type SortDir = "asc" | "desc";
export const LIST_SORT_KEY = "diariz.recordings.sort";
export function sortRecordings(items: RecordingSummary[], key: SortKey, dir: SortDir): RecordingSummary[];
export function useListSort(): { key; dir; setKey; setDir };
```

`useListSort` is `useState` seeded from `localStorage`, written back on every change, and tolerant of
storage being unavailable (private mode) exactly as `panelTab` is - the sort still moves for the
session, it just does not persist. Anything unparseable or unrecognised reads as
`{ key: "manual", dir: "asc" }`, which covers a first visit, a value from an older build, and a
hand-edited key.

One global setting, not one per folder or per room: a user who prefers newest-first wants that
everywhere, and per-folder state would make the same list sort differently as they drill.

`panelTab`'s `useSyncExternalStore` pattern is deliberately **not** copied. That exists because a
component outside the panel writes the tab; nothing outside the panel writes the sort.

### Semantics

- `manual` returns the input array unchanged - the server's `Position` order.
- `date` compares `createdAt`.
- `name` compares `name ?? title` with `localeCompare` (the same fallback the rows display).
- `duration` compares `durationMs`.
- `desc` is the ascending comparator reversed, so the two directions cannot disagree about ties.

`sortRecordings` returns a new array and never mutates its input.

### Scope

Recordings on the **List tab** only. Folder rows keep their manual order under every setting:
Duration and Date/Time have no meaning for a folder, and a folder block that reacted to Name alone
would be inconsistent across the three keys. Search results, the Calendar tab and the Tags tab are
untouched.

### The sort must not leak into the reorder writes

`computeReorder` turns whatever id list it is handed into the recordings' new server `Position`s. So
`levelIds` - passed to the level-background drop, and to select-all - stays derived from the
**unsorted** `level.items`. Only `rowList` receives the sorted array.

Without this, dragging a single recording in from another folder while sorted by Name would rewrite
the whole destination folder's manual order to alphabetical, destroying an arrangement the user never
touched. The sort is display-only; every id list that reaches a write stays in manual order.

### Drag-to-reorder while sorted

Reordering within a level is disabled while a non-manual sort is active. A drop would write a
`Position` the sorted view cannot show, so the row would spring back and read as a broken drag.

`RecordingRow.onDropBefore` becomes optional. When it is absent the row attaches no `onDragOver` /
`onDrop` handler at all, so a drop bubbles to the level background (which appends) rather than being
swallowed. Rows stay `draggable` throughout - dragging into a folder row or onto a breadcrumb still
works under any sort, because those paths append and never reorder the source level.

Reordering returns the moment the user switches back to Manual.

## 2. Date and time on the row

New pure helper in `lib/format.ts`:

```ts
formatListDateTime(iso: string, locale: string | undefined, todayLabel: string, now?: Date): string
```

| When | Output |
|---|---|
| Same calendar day as `now` | `Today 14:30` |
| Same year, earlier day | `11 Aug 14:30` |
| Any other year | `11 Aug 2025 14:30` |

The day and month come from `Intl` (`{ day: "numeric", month: "short" }`, plus `year: "numeric"`
outside the current year) so the order follows the locale. The time is `formatTimeHm` - fixed
24-hour, as specified.

`todayLabel` is injected already translated rather than importing i18n into `format.ts`, which is
pure and locale-string-driven throughout. `now` is injectable so the tests are deterministic,
matching `formatRelativeTime`'s existing signature.

In `RecordingRow` the right-aligned `formatDuration` span is replaced by this call, keeping its
`shrink-0 tabular-nums text-xs` so the column still lines up. The duration moves into the row's
existing hover `title`, beside the source, so nothing is lost.

This row is shared with the Calendar day list and the Tags lists, which change with it. That is
consistent rather than a side effect: a row should say the same thing wherever it appears.

## 3. The folder icon is the Change folder button

The separate `Change folder` text button added in PR #508 is removed. The `FolderIcon` that opens the
breadcrumb becomes the trigger.

`FolderChips` gains `showIcon = true`. `RecordingDetail` passes `showIcon={false}` and renders the
icon itself, immediately left of the `<nav>`, as a `<button>`:

- button chrome (rounded border, background, hover tint) for the affordance;
- the glyph still tinted with the deepest crumb's `sectionColor`, so today's colour cue survives;
- `aria-label` and `title` from the existing `workspace:changeFolder` key, already present in all
  four locales;
- `onClick` opens the same move modal (`setMoving(true)`) the removed button opened.

The button stays **outside** the navigation landmark. PR #508 reasoned that changing the path is an
action on the path rather than a step in it; moving the icon out preserves that decision instead of
reversing it one commit later.

## Testing

Test-first throughout.

| File | Covers |
|---|---|
| `lib/listSort.test.ts` | each comparator, both directions, Manual returns the input order, input not mutated, unknown/absent storage falls back to Manual |
| `lib/format.test.ts` | the three `formatListDateTime` branches with an injected `now`, plus the year boundary (31 Dec / 1 Jan) |
| `components/nav/SortMenu.test.tsx` | options render, direction toggle absent under Manual and present otherwise, changes reported upward |
| `components/RecordingsPanel.test.tsx` | a sort reorders the rendered rows; folder rows do not move; the ids handed to `reorderRecordings` stay in manual order when a drop lands while sorted; rows carry no reorder drop handler while sorted |
| `components/nav/RecordingRow.test.tsx` | the date/time is rendered, the duration appears in the `title` |
| `pages/RecordingDetail.test.tsx` | clicking the folder icon opens the move modal; the old `Change folder` text button is gone |

Component tests follow the established pattern: `vi.mock` of `../lib/api`, rendered inside
`MemoryRouter` + `QueryClientProvider` + `SelectionProvider`.

## Release chores

Per `CLAUDE.md`, this is a functional enhancement: **0.206.0 -> 0.207.0**.

1. `version.json` and its four mirrors (`apps/web/package.json`, `apps/desktop/package.json`,
   `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`).
2. One `RELEASES[0]` entry in `apps/web/src/lib/releases.ts`.
3. The About-box `CAPABILITIES` row for the recordings list.
4. The README Features row and the matching `docs/features.md` bullet, in lockstep.
5. New i18n keys in all four locales (`en`, `de`, `es`, `fr`): the sort labels, the direction toggle
   label, and `Today`. Plain hyphens only - no em or en dashes in user-facing text.

`docs/Data_Schema.md` and `docs/Overall_Synopsis_of_Platform.md` are unchanged: no schema, contract,
dependency or deployment change. The help article covering the recordings list is reviewed for the
row's duration-to-date change and the new sort, since both alter behaviour a user relies on.

**Deployment surface: server redeploy only.** No file under `apps/desktop/src`, `apps/desktop/build`,
`electron-builder.config.js` or the desktop dependencies is touched, so no desktop release is needed.

## Out of scope

- Sorting folder rows.
- Sorting search results, the Calendar tab or the Tags tab.
- Any server-side ordering change - `listRecordings` still returns `Position` order and the sort is
  applied in the client.
- A "Yesterday" label, or any relative wording beyond `Today`.
