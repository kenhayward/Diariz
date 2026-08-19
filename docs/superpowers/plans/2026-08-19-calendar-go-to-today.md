# Calendar "Go to today" Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a toolbar button, visible only on the Calendar tab, that returns both the selected day and the visible month to today.

**Architecture:** The button lives in `ListToolbar` and calls an `onGoToToday` prop. `RecordingsPanel` implements it by setting `selectedDay` to today **and** bumping a `goToTodaySignal` counter. `CalendarTab` watches that counter and resets its own local `month`. The counter exists because `selectedDay` already defaults to today, so setting it is often a no-op that would leave the month grid stranded.

**Tech Stack:** React 19 + TypeScript + Tailwind v4, vitest + @testing-library/react, i18next.

**Spec:** `docs/superpowers/specs/2026-08-19-calendar-go-to-today-design.md`

## Global Constraints

- **TDD is mandatory.** Write the failing test, run it, watch it fail with a real message, then write the minimal code to pass.
- **Test output must be pristine.** A passing run has no errors or warnings.
- **No em/en dashes in user-facing text.** Plain hyphen `-` only, never `—` or `–`. Applies to UI strings, all four i18n catalogs, and release notes.
- **Never commit to `main`.** Work lands on branch `feat/calendar-go-to-today` and merges via a PR.
- **Never `git add -A` in this repository.** Stage explicit paths only - a sweep pulls in hundreds of untracked scratch files.
- **Do not add a `jest-dom` dependency.** None of the 230+ existing web test files use its matchers; use plain assertions.
- This is a **web-only** change. Do not touch `src/`, `tests/`, or `apps/desktop/`.
- Version target: `0.229.0` -> **`0.230.0`**.

---

### Task 1: The toolbar button

**Files:**
- Modify: `apps/web/src/components/nav/ListToolbar.tsx`
- Modify: `apps/web/src/locales/{en,de,es,fr}/workspace.json`
- Test: `apps/web/src/components/nav/ListToolbar.test.tsx`

**Interfaces:**
- Consumes: nothing.
- Produces: `ListToolbar` gains a required prop `onGoToToday: () => void`. Task 3 supplies it from `RecordingsPanel`.

- [ ] **Step 1: Write the failing tests**

In `apps/web/src/components/nav/ListToolbar.test.tsx`, first add `onGoToToday={() => {}}` to the props in the `renderToolbar` helper's `<ListToolbar ...>` element, immediately before the `{...over}` spread, so every existing test still supplies the new required prop.

Then append a new block inside the top-level `describe`:

```tsx
  describe("go to today", () => {
    it("is offered on the Calendar tab", () => {
      renderToolbar({ listMode: false, calendarMode: true });
      expect(screen.getByRole("button", { name: "Go to today" })).toBeTruthy();
    });

    it("is not offered on the list", () => {
      renderToolbar();
      expect(screen.queryByRole("button", { name: "Go to today" })).toBeNull();
    });

    // The gating deliberately differs from the two syncs beside it: those are personal-only because the
    // calendar EVENT OVERLAY is, but a shared room's day grid still draws that room's recordings, so
    // navigating to today is meaningful there. Without this assertion, a later tidy-up that folded the
    // button into the isPersonalRoom block would silently drop it from shared rooms.
    it("is offered in a shared room, where the two syncs are not", () => {
      renderToolbar({ listMode: false, calendarMode: true, isPersonalRoom: false });
      expect(screen.getByRole("button", { name: "Go to today" })).toBeTruthy();
      expect(screen.queryByRole("button", { name: "Sync selected day" })).toBeNull();
      expect(screen.queryByRole("button", { name: "Sync calendar" })).toBeNull();
    });

    it("calls back when clicked", () => {
      const onGoToToday = vi.fn();
      renderToolbar({ listMode: false, calendarMode: true, onGoToToday });
      fireEvent.click(screen.getByRole("button", { name: "Go to today" }));
      expect(onGoToToday).toHaveBeenCalledTimes(1);
    });
  });
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
cd apps/web && npx vitest run src/components/nav/ListToolbar.test.tsx
```

Expected: the three "is offered"/"calls back" tests FAIL with `Unable to find an accessible element with the role "button" and name "Go to today"`. `"is not offered on the list"` PASSES already - it pins absence, which is currently trivially true. Do not "fix" that one.

- [ ] **Step 3: Add the icon**

At the bottom of `apps/web/src/components/nav/ListToolbar.tsx`, beside the existing `SyncTodayIcon` and `SyncCalendarIcon` declarations:

```tsx
// Feather `crosshair` with a filled centre - "centre on today". Deliberately not a calendar glyph:
// SyncTodayIcon next to it is already a calendar with a marker in its body, and three calendars in a row
// of three buttons are not tellable apart at 18px.
const TodayIcon = () => (
  <svg {...iconProps}>
    <circle cx="12" cy="12" r="10" />
    <line x1="22" y1="12" x2="18" y2="12" />
    <line x1="6" y1="12" x2="2" y2="12" />
    <line x1="12" y1="6" x2="12" y2="2" />
    <line x1="12" y1="22" x2="12" y2="18" />
    <circle cx="12" cy="12" r="2.5" fill="currentColor" stroke="none" />
  </svg>
);
```

- [ ] **Step 4: Add the prop**

In the `ListToolbar` destructured parameter list add `onGoToToday,` (put it next to `onError`), and in the type literal add:

```tsx
  /// Return the Calendar to today. The panel owns both halves of that - the selected day and, via a
  /// counter the Calendar tab watches, the visible month - so this is a callback rather than state here.
  onGoToToday: () => void;
```

- [ ] **Step 5: Render the button and move the separator inside the sync block**

Replace the existing calendar block. It currently reads:

```tsx
          {calendarMode && isPersonalRoom && (
            <>
              <ToolbarButton
                label={t("calSyncSelectedDay")}
```

Change it to:

```tsx
          {/* Calendar-only. Unlike the two syncs below it is NOT personal-room-only: those need the event
              overlay, which is personal, but a shared room's day grid still draws that room's recordings. */}
          {calendarMode && (
            <ToolbarButton label={t("calGoToToday")} onClick={onGoToToday} icon={<TodayIcon />} />
          )}
          {/* Calendar-only, and personal-room-only, because that is exactly where the event overlay exists.
              One control per scope rather than a menu: "the meeting I just accepted is missing" is a common
              enough moment to deserve a button, and the quick sync is seconds where the full one is tens.
              The separator is INSIDE this block, not between it and the button above: rendered outside it
              would dangle at the end of the toolbar in a shared room, where this pair is hidden. */}
          {calendarMode && isPersonalRoom && (
            <>
              <span className="h-5 w-px shrink-0 bg-gray-200 dark:bg-gray-600" aria-hidden />
              <ToolbarButton
                label={t("calSyncSelectedDay")}
```

Leave the rest of that block (the two sync buttons and its closing `</>`/`)}`) exactly as it is.

- [ ] **Step 6: Add the copy to all four catalogs**

Add `calGoToToday` beside `calSyncSelectedDay` in each `workspace.json`:

- `en`: `"calGoToToday": "Go to today"`
- `de`: `"calGoToToday": "Zu heute springen"`
- `es`: `"calGoToToday": "Ir a hoy"`
- `fr`: `"calGoToToday": "Aller a aujourd'hui"` - write the French with its real accent: `Aller à aujourd'hui`. The unaccented spelling here is only so this plan file stays ASCII.

- [ ] **Step 7: Run the tests to verify they pass**

```bash
cd apps/web && npx vitest run src/components/nav/ListToolbar.test.tsx src/locales.test.ts
```

Expected: PASS, including the locale key-parity gate.

- [ ] **Step 8: Commit**

```bash
git add apps/web/src/components/nav/ListToolbar.tsx apps/web/src/components/nav/ListToolbar.test.tsx apps/web/src/locales/en/workspace.json apps/web/src/locales/de/workspace.json apps/web/src/locales/es/workspace.json apps/web/src/locales/fr/workspace.json
git commit -m "feat: add a Go to today button to the calendar toolbar

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 2: The month signal in CalendarTab

**Files:**
- Modify: `apps/web/src/components/nav/CalendarTab.tsx`
- Test: `apps/web/src/components/nav/CalendarTab.test.tsx`

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces: `CalendarTab` gains a required prop `goToTodaySignal: number`. Task 3 supplies it.

- [ ] **Step 1: Write the failing test**

In `apps/web/src/components/nav/CalendarTab.test.tsx`, extend the `Harness` component inside `renderTab`. It currently holds only `selectedDay`; add the signal and a button to bump it:

```tsx
    const [selectedDay, setSelectedDay] = useState<string | null>(() => dayKey(new Date()));
    // The panel bumps this when its toolbar's Go to today is pressed. A counter, not a boolean, because
    // the button has to be able to fire more than once.
    const [goToTodaySignal, setGoToTodaySignal] = useState(0);
    return (
      <>
        <button onClick={() => setOpen((o) => !o)}>toggle-tab</button>
        <button onClick={() => setGoToTodaySignal((n) => n + 1)}>go-to-today</button>
        {open && (
          <CalendarTab
            recordings={recordings}
            isPersonalRoom
            selectedDay={selectedDay}
            onSelectDay={setSelectedDay}
            goToTodaySignal={goToTodaySignal}
          />
        )}
      </>
    );
```

Then append this test to the `describe("CalendarTab", ...)` block:

```tsx
  // The point of the signal. `selectedDay` is initialised to today and is never touched here, so a
  // "go to today" built only on setSelectedDay(today) would be a no-op and leave the grid on next month.
  // If this test can pass without the signal, it is not testing the feature.
  it("returns the grid to today's month when the signal fires, though the selected day never changed", async () => {
    renderTab();
    await screen.findByText("Today call");

    fireEvent.click(screen.getByRole("button", { name: /next month/i }));
    await waitFor(() => expect(screen.getByText(monthHeading(1))).toBeTruthy());

    fireEvent.click(screen.getByText("go-to-today"));

    expect(await screen.findByText(monthHeading(0))).toBeTruthy();
  });
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
cd apps/web && npx vitest run src/components/nav/CalendarTab.test.tsx
```

Expected: a TypeScript/runtime failure on the unknown `goToTodaySignal` prop, then the new test failing because the grid is still on next month. Confirm you see the new test fail before continuing.

- [ ] **Step 3: Add the prop and the effect**

In `apps/web/src/components/nav/CalendarTab.tsx`, add to the destructured params `goToTodaySignal,` and to the type literal:

```tsx
  /// Bumped by the panel when the toolbar's Go to today is pressed, to bring the month grid back. A
  /// counter rather than a boolean or a day value: `selectedDay` already defaults to today, so pressing
  /// the button often changes nothing else, and only a value that always changes can drive this.
  goToTodaySignal: number;
```

Then, immediately after the existing `const [month, setMonth] = useState(...)` block:

```tsx
  // Skip the first run: mounting must not re-assert the month `useState` has just initialised. Only a
  // real bump from the toolbar moves the grid, which keeps the "reopening the tab starts on the current
  // month" behaviour below owned solely by the mount.
  const seenSignal = useRef(false);
  useEffect(() => {
    if (!seenSignal.current) {
      seenSignal.current = true;
      return;
    }
    const now = new Date();
    setMonth({ year: now.getFullYear(), month: now.getMonth() });
  }, [goToTodaySignal]);
```

`useEffect` and `useRef` are already imported on line 1 of that file; do not add an import.

- [ ] **Step 4: Run the tests to verify they pass**

```bash
cd apps/web && npx vitest run src/components/nav/CalendarTab.test.tsx
```

Expected: PASS, **including the pre-existing** `"starts on the current month again after being left and reopened"` test, unedited. That test is the guard that the new effect has not disturbed mount behaviour.

- [ ] **Step 5: Commit**

```bash
git add apps/web/src/components/nav/CalendarTab.tsx apps/web/src/components/nav/CalendarTab.test.tsx
git commit -m "feat: let the calendar month grid be returned to today by a signal

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 3: Wire the panel, and prove it end to end

**Files:**
- Modify: `apps/web/src/components/RecordingsPanel.tsx`
- Test: `apps/web/src/components/RecordingsPanel.test.tsx`

**Interfaces:**
- Consumes: `ListToolbar`'s `onGoToToday` (Task 1) and `CalendarTab`'s `goToTodaySignal` (Task 2).
- Produces: nothing.

- [ ] **Step 1: Write the failing end-to-end test**

Append to `apps/web/src/components/RecordingsPanel.test.tsx`, inside the top-level `describe`. This is the test that proves the three components are actually connected - Tasks 1 and 2 each pass with the wiring absent:

```tsx
  // End to end across three components: the button is in the toolbar, the selected day is in the panel,
  // and the month is inside the Calendar tab. The selected day starts as today and is never changed here,
  // so this only passes if the panel drives the month signal as well as the day.
  it("returns the calendar to today's month from the toolbar, after paging away", async () => {
    renderList();
    await screen.findByText("Weekly Standup");
    fireEvent.click(screen.getByRole("tab", { name: "Calendar" }));

    const now = new Date();
    const monthName = (offset: number) =>
      new Date(now.getFullYear(), now.getMonth() + offset, 1).toLocaleString("en", { month: "long" });
    const monthHeading = (offset: number) => new RegExp(`${monthName(offset)}\\s+\\d{4}`, "i");

    await screen.findByText(monthHeading(0));
    fireEvent.click(screen.getByRole("button", { name: /next month/i }));
    await waitFor(() => expect(screen.getByText(monthHeading(1))).toBeTruthy());

    fireEvent.click(screen.getByRole("button", { name: "Go to today" }));

    expect(await screen.findByText(monthHeading(0))).toBeTruthy();
  });
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
cd apps/web && npx vitest run src/components/RecordingsPanel.test.tsx -t "returns the calendar to today"
```

Expected: FAIL. The panel does not yet pass `onGoToToday`, so either the button is absent or clicking it throws on `onGoToToday` being undefined.

- [ ] **Step 3: Wire the panel**

In `apps/web/src/components/RecordingsPanel.tsx`, immediately after the `selectedDay` state declaration (around line 109):

```tsx
  // Bumped by the toolbar's Go to today, and watched by CalendarTab to bring its month grid back. It is a
  // separate signal from `selectedDay` because that already defaults to today: pressing the button while
  // today is selected changes no day at all, and the month still has to move.
  const [goToTodaySignal, setGoToTodaySignal] = useState(0);
  function goToToday() {
    setSelectedDay(dayKey(new Date()));
    setGoToTodaySignal((n) => n + 1);
  }
```

Add `onGoToToday={goToToday}` to the `<ListToolbar ... />` element (beside `selectedDay`), and `goToTodaySignal={goToTodaySignal}` to the `<CalendarTab ... />` element.

- [ ] **Step 4: Run the test to verify it passes**

```bash
cd apps/web && npx vitest run src/components/RecordingsPanel.test.tsx
```

Expected: PASS, whole file.

- [ ] **Step 5: Typecheck and run the whole web suite**

```bash
cd apps/web && npm run build && npx vitest run
```

Expected: build clean, all tests pass. `npm run build` runs `tsc`, which is what catches any component still missing a required prop.

- [ ] **Step 6: Commit**

```bash
git add apps/web/src/components/RecordingsPanel.tsx apps/web/src/components/RecordingsPanel.test.tsx
git commit -m "feat: wire Go to today from the toolbar through to the calendar month

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 4: Docs, version, release, PR

**Files:**
- Modify: `apps/web/src/lib/releases.ts` (CAPABILITIES row + new `RELEASES[0]`)
- Modify: `README.md`, `docs/features.md`
- Modify: `version.json` + the four mirrors

**Interfaces:**
- Consumes: everything above.
- Produces: nothing.

- [ ] **Step 1: Update the three feature inventories in lockstep**

`apps/web/src/lib/releases.ts`, `CAPABILITIES`, **Google & calendars** row - after "...and a line marking the current time.", insert:

```
 A **Go to today** button on the panel toolbar brings both the day and the month grid back to today after you have browsed ahead.
```

`README.md` line 50, **Google sign-in & Calendar** row - after "...and a line marking the current time.", insert the same sentence.

`docs/features.md`, in the "**The Calendar tab's day view is a time grid**" bullet, after "The calendar a meeting came from is shown in the block's tooltip.", add:

```
A **Go to today** button sits on the panel toolbar whenever the Calendar tab is open (in shared rooms too,
unlike the two syncs beside it): it returns the selected day and the month grid to today in one click, which
matters most after you have paged the grid forward - the day was already today, so only the grid had moved.
```

- [ ] **Step 2: Bump version.json, and watch the mirror tests fail**

```bash
node -e "require('fs').writeFileSync('version.json', '{ \"version\": \"0.230.0\" }\n')"
cd apps/web && npx vitest run src/lib/versionMirrors.test.ts src/lib/releases.test.ts
```

Expected: FAIL - four mirror assertions plus `RELEASES[0].version`.

- [ ] **Step 3: Bump the four mirrors**

Set `"version": "0.230.0"` in `apps/web/package.json`, `apps/desktop/package.json` and `integrations/n8n-nodes-diariz/package.json`; set `<Version>0.230.0</Version>` in `src/Diariz.Api/Diariz.Api.csproj`.

- [ ] **Step 4: Add the release entry**

Insert as the new first element of `RELEASES` in `apps/web/src/lib/releases.ts`. `pr` is filled in at Step 8:

```ts
  {
    version: "0.230.0",
    date: "2026-08-19",
    pr: 0,
    headline: "A Go to today button on the calendar",
    summary:
      "The Calendar tab opens on today, but until now there was no way back to it. Page the month grid forward to check next month's meetings and the only way home was clicking back a month at a time.\n\nThere is now a Go to today button on the panel toolbar, to the left of the two sync buttons. It appears whenever the Calendar tab is open - including in shared rooms, where the sync buttons do not - and one click puts both the selected day and the month grid back on today.",
    added: [
      "A Go to today button on the Calendar tab's toolbar, returning the selected day and the month grid to today in one click.",
    ],
  },
```

- [ ] **Step 5: Run the version tests to verify they pass**

```bash
cd apps/web && npx vitest run src/lib/versionMirrors.test.ts src/lib/releases.test.ts
```

Expected: PASS.

- [ ] **Step 6: Full verification**

```bash
cd apps/web && npm run build && npx vitest run
```

Expected: build clean, all tests pass.

The .NET side is untouched by this change, but the API csproj version was bumped, so confirm the solution still builds:

```bash
dotnet build Diariz.slnx
```

Expected: build succeeded. Only the pre-existing `SSH.NET` NU1903 advisory should appear - no new warnings.

- [ ] **Step 7: Commit and push**

```bash
git add version.json apps/web/package.json apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj integrations/n8n-nodes-diariz/package.json apps/web/src/lib/releases.ts README.md docs/features.md
git commit -m "chore: release 0.230.0 - calendar Go to today"
git push -u origin feat/calendar-go-to-today
```

Add the `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>` trailer to the commit message.

- [ ] **Step 8: Open the PR, then backfill its number**

```bash
gh pr create --title "feat: Go to today button on the calendar toolbar (0.230.0)" --body "..."
```

The body should state: what the button does; that it is gated on the Calendar tab only and so appears in shared rooms unlike the two syncs; that the month grid needed its own signal because `selectedDay` already defaults to today; and the deployment surface - **web-only redeploy, no desktop release, no API change**.

Read the PR number from the command's output. Do **not** guess it as last + 1 - issues and Dependabot PRs share the sequence, and no test catches a wrong `pr:` value. Then replace `pr: 0` with the real number, re-run `npx vitest run src/lib/releases.test.ts`, commit and push.

- [ ] **Step 9: Confirm CI**

```bash
gh pr checks --watch
```

Expected: all required checks green.

## Verification summary

Before claiming completion:

```bash
cd apps/web && npm run build && npx vitest run
```

```bash
dotnet build Diariz.slnx
```

Plus the specific evidence: the pre-existing `"starts on the current month again after being left and reopened"` test in `CalendarTab.test.tsx` passing **unedited**, and the Task 3 end-to-end test failing before the panel wiring existed.
