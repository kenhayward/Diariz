# Calendar "Go to today" toolbar button

**Date:** 2026-08-19
**Status:** approved, ready to plan
**Deployment surface:** server redeploy, web only (`apps/web`). No API change, no desktop release.
**Version:** 0.229.0 -> 0.230.0 (functional enhancement: Minor +1, Build reset)

## Problem

The Calendar tab opens on today, but there is no way back. Once you page the month grid forward to look
at next month's meetings, returning to today means paging back by hand, one month at a time. Every
calendar application has a "today" control; this one does not.

## Goals

1. A toolbar button that puts the user back on today in one click - both the selected day *and* the
   visible month.
2. Visible only while the Calendar tab is open.
3. No change to any existing behaviour.

## Non-goals

- A keyboard shortcut.
- Changing how the month grid pages, or how a day is selected.
- Any change to the two calendar sync buttons.

## The non-obvious problem this design exists to solve

"Move the selected day to today" sounds like one line: `setSelectedDay(dayKey(new Date()))`. It is not,
because **`selectedDay` already defaults to today**
(`RecordingsPanel.tsx:109`).

Consider the case the button is actually for: the user opens Calendar, pages the grid forward to
December, and `selectedDay` is still today. Setting it to today again is a no-op - React bails out, no
effect fires, and the month grid stays on December. The button does nothing in precisely the situation
that motivates it.

The visible month is separate state (`month`, local to `CalendarTab`), and the button lives in
`ListToolbar`, which is `CalendarTab`'s **sibling**. So the button must command two components.

### Why the month stays where it is

Three ways to reach the month were considered:

1. **Lift `month` into `RecordingsPanel`,** beside `selectedDay`. Rejected: `CalendarTab`'s own header
   comment documents keeping it local as a deliberate trade ("the alternative is keeping the month alive
   in the panel, which is cold state in the hottest file in the app"), and the resulting behaviour - the
   month resetting to today when you leave and reopen the tab - is pinned by a test in
   `CalendarTab.test.tsx` that explicitly says "it IS a behaviour change, so it is pinned here".
   Lifting the state would silently reverse that decision and break its test.
2. **Derive the month from `selectedDay`,** deleting the local state. Rejected: it would mean paging
   prev/next also moves the selected day, so you could no longer look ahead through months while keeping
   today's detail open below. A larger behaviour change than the feature warrants.
3. **Pass a counter signal.** Chosen. It changes nothing that exists.

A **counter, not a boolean**, because the button must be able to fire more than once: a boolean latched
at `true` would work exactly once.

## Design

### 1. Placement and gating

A `ToolbarButton` in `ListToolbar`, rendered immediately before the existing calendar sync block:

```
[ (+) New folder ] [ Select ] ... [ (X) Go to today ] | [ Sync selected day ] [ Sync calendar ]
```

`ListToolbar` is the right home, and not by default: its own header comment records that the calendar
syncs were put there "because that is where every other action in this panel lives".

**Gating differs from its neighbours, deliberately.** The two sync buttons are
`calendarMode && isPersonalRoom`; Go to today is **`calendarMode` only**. The syncs are personal-only
because the *calendar event overlay* is - a shared room has no events to sync - but the day grid still
draws that room's recordings, so navigating to today is meaningful there.

That difference dictates where the separator goes: it is rendered as the **first child inside** the
`calendarMode && isPersonalRoom` block, not as a sibling between the two blocks. Rendered between them it
would dangle at the end of the toolbar in a shared room, where the sync pair is hidden.

The separator reuses the existing toolbar idiom from `ConversationFlowPlayer.tsx:138`:

```tsx
<span className="h-5 w-px shrink-0 bg-gray-200 dark:bg-gray-600" aria-hidden />
```

### 2. What the click does

- `ListToolbar` gains an `onGoToToday: () => void` prop and calls it.
- `RecordingsPanel` implements it as **both** state moves:
  `setSelectedDay(dayKey(new Date()))` and `setGoToTodaySignal((n) => n + 1)`.
- `CalendarTab` gains `goToTodaySignal: number` and an effect keyed on it that sets `month` to today's
  year and month. The effect must **skip its first run**, so mounting the tab does not re-assert the
  month that `useState` has just initialised - harmless today, but it would fight any future change to
  the initial month.

`setSelectedDay` is what makes the day heading and day grid show today; the signal is what moves the
month grid. Both are needed, and the signal is the half that works when `selectedDay` is unchanged.

### 3. Icon

A crosshair - a circle with four ticks - declared file-local in `ListToolbar.tsx` next to the existing
`SyncTodayIcon` and `SyncCalendarIcon`, which is how that file already organises its glyphs.

Not a calendar variant: `SyncTodayIcon` is already a calendar with a filled square in its body, so the
calendar-with-a-marker motif is taken, and a third calendar glyph in a row of three buttons would not be
tellable apart at 16px.

### 4. Copy

`calGoToToday` in the `workspace` namespace, beside `calSyncSelectedDay`, in **all four** catalogs
(`en`, `de`, `es`, `fr`) - `locales.test.ts` enforces key parity. English: "Go to today". Plain hyphens
only, per project convention.

## Error handling

There is nothing to fail: no network call, no persistence. Two edge cases, both benign:

| Case | Behaviour |
|---|---|
| Today is already selected and visible | The click is a no-op the user cannot perceive. Not disabled - see below. |
| `selectedDay` is null | Set to today, which is the normal path. |

The button is **never disabled**. Disabling it correctly would mean "today is selected *and* the grid is
showing today's month", and `ListToolbar` cannot know the second half - the month is in its sibling.
A button disabled on only half its condition would be wrong in the paged-to-December case, which is the
case that matters most.

## Testing

**`ListToolbar.test.tsx`**

- Shows on the Calendar tab.
- Absent on the List tab.
- **Present in a shared room, where the two sync buttons are not.** This is the assertion that pins the
  deliberate gating difference; without it, a later "tidy-up" would fold the button into the
  `isPersonalRoom` block and silently remove it from shared rooms.
- Clicking calls `onGoToToday`.

**`CalendarTab.test.tsx`** - the harness already has a `monthHeading(offset)` helper and a `Harness`
component holding `selectedDay`. Add `goToTodaySignal` state and a button to bump it, then:

- Page to next month, bump the signal, assert the grid is back on today's month.
  **`selectedDay` is left untouched at today**, so this reproduces exactly the no-op case that a naive
  `setSelectedDay` implementation would fail. If this test can pass without the signal, it is not testing
  the feature.
- The existing "starts on the current month again after being left and reopened" test must still pass
  unedited - it is the guard that the effect has not disturbed the documented mount behaviour.

No `jest-dom` matchers: none of the 230+ existing web test files use them.

## Release checklist

1. `version.json` -> `0.230.0`, plus the four mirrors (`apps/web/package.json`,
   `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`,
   `integrations/n8n-nodes-diariz/package.json`).
2. `RELEASES[0]` in `apps/web/src/lib/releases.ts`, with the real PR number read from `gh pr create`
   (it cannot be guessed as last + 1).
3. The Calendar rows in `CAPABILITIES` (`releases.ts`), the **README** Features table, and
   `docs/features.md` each enumerate the calendar's controls, so each gets a clause. These three move in
   lockstep.
4. No `docs/Data_Schema.md` change (no schema) and no `docs/Overall_Synopsis_of_Platform.md` change (no
   architecture, contract, endpoint or dependency change).
5. No OpenAPI snapshot change - the API is untouched.
