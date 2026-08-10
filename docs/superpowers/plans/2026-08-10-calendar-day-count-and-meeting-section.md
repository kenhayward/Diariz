# Calendar Day Count and Meeting Section Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Calendar tab's day heading count every event on the day, and move a linked recording's full invite off the recordings page into a "Calendar Event" section you drill into, leaving a compact clickable summary behind.

**Architecture:** The day count becomes a pure function counting events with the same day-matching predicate `dayItems` uses, minus the linked-skip that made the heading count what is drawn rather than what is on the calendar. The invite moves into a new `SectionKey` rendered through the existing `DetailSections`, so it inherits the standard breadcrumb, the recording header above it, and the fallback-to-hub behaviour every other section already has.

**Tech Stack:** React 19 + TypeScript + Vite + Tailwind v4, vitest + @testing-library/react (jsdom).

**Spec:** `docs/superpowers/specs/2026-08-10-calendar-day-count-and-meeting-section-design.md`

## Global Constraints

- **This is web-only.** No API, no schema, no migration. Nothing under `src/Diariz.Api`, `src/Diariz.Domain`, or `apps/desktop/` may change. If a change to any of those appears necessary, stop and escalate.
- **No em or en dashes** (`—`, `–`) in any user-facing string: UI text, i18n catalogues (`apps/web/src/locales/**`), release notes, help content. Use a plain hyphen `-`.
- **Every new i18n key must be added to all four locales**: `en`, `de`, `es`, `fr` under `apps/web/src/locales/<locale>/workspace.json`.
- **TDD is mandatory.** Write the failing test, run it, watch it fail for the stated reason, then write the minimal code. Quote the real failure output in your report.
- **Test output must be pristine** - a passing run has no errors or warnings.
- **`npm run build` must pass** (it runs the TypeScript typecheck).
- **Never `git add -A`.** Stage explicit paths. This repo accumulates agent scratch files that have been swept into a commit before.
- **Never commit or push to `main`.** Work happens on `fix/calendar-day-count-and-meeting-section`, which already exists and holds the spec commit.
- **Run the FULL web suite** (`cd apps/web && npm test`), not just the file you changed. `MeetingCard` and `CalendarEventDetails` are rendered from several test files.

---

## File Structure

**Task 1 - the day count**
- `apps/web/src/lib/calendar.ts` - a new pure `dayEventCount`, beside `dayItems` which it deliberately does not reuse.
- `apps/web/src/components/nav/CalendarTab.tsx` - computes it and passes it to `DayHeading`, which stops deriving the number from what it draws.

**Task 2 - the section**
- `apps/web/src/lib/detailSection.ts` - the new `"meeting"` key.
- `apps/web/src/pages/RecordingDetail.tsx` - the `DetailSection` entry, added only when a link exists.
- locales - `detailSectionMeeting`.

**Task 3 - the card**
- `apps/web/src/components/detail/MeetingCard.tsx` - body becomes a summary and the click target.
- `apps/web/src/pages/RecordingDetail.tsx` - wires `onOpen` to `selectTab("meeting")`.
- locales - `calAttendeeCount`, `hubMeetingOpen`.

**Task 4** - version, release notes, docs, help.

---

### Task 1: The day heading counts every event

A day with six meetings, five of them recorded, currently reads "1 event · 5 recordings". The heading counts what the grid draws, and the grid deliberately omits an event whose recording stands in for it.

**Files:**
- Modify: `apps/web/src/lib/calendar.ts` (add beside `dayItems`, which ends at `:179`)
- Modify: `apps/web/src/components/nav/CalendarTab.tsx:108-111` (the memo block), `:165` (the `DayHeading` call), `:180-201` (`DayHeading` itself)
- Test: `apps/web/src/lib/calendar.test.ts`, `apps/web/src/components/nav/CalendarTab.test.tsx`

**Interfaces:**
- Consumes: nothing.
- Produces: `export function dayEventCount(events: CalendarEvent[], key: string): number`. Nothing later in this plan uses it.

- [ ] **Step 1: Write the failing component test**

This is the primary red test, because the defect lives in the *wiring*, not the arithmetic. Add it to `apps/web/src/components/nav/CalendarTab.test.tsx` inside the main `describe`. Read the top of that file first: it already mocks `../../lib/api`, and other tests already set `vi.mocked(api.getCalendarEvents)` - match that style exactly.

```tsx
  // The grid draws a linked event as its recording row, not as an event chip - but it is still an event on
  // the calendar that day, and a heading that says "1 event" when six meetings happened is simply wrong.
  it("counts every event on the day, including ones that already have a recording", async () => {
    const today = new Date();
    const at = (h: number) => new Date(today.getFullYear(), today.getMonth(), today.getDate(), h).toISOString();
    vi.mocked(api.getCalendarEvents).mockResolvedValue([
      { id: "linked", summary: "Standup", start: at(9), end: at(10), htmlLink: null },
      { id: "loose", summary: "Review", start: at(11), end: at(12), htmlLink: null },
    ]);

    renderTab([{ ...rec, calendarEventId: "linked" }]);

    expect(await screen.findByText(/2 events/)).toBeTruthy();
  });
```

`renderTab` and `rec` already exist in that file. `rec` is a recording dated today; spreading `calendarEventId: "linked"` links it to the first event. If `renderTab`'s signature does not accept a recordings array, check how the file's other tests supply recordings and follow that.

- [ ] **Step 2: Run it and watch it fail**

Run: `cd apps/web && npx vitest run src/components/nav/CalendarTab.test.tsx`

Expected: FAIL. The heading renders `1 event · 1 recording`, so `findByText(/2 events/)` times out. If instead it passes, the fixture is not actually linking the recording to the event - fix the fixture, do not proceed.

- [ ] **Step 3: Write the pure function**

Append to `apps/web/src/lib/calendar.ts`, immediately after `dayItems`:

```ts
/**
 * How many calendar events cover `key`, **including** those a recording is already linked to.
 *
 * Deliberately not derived from `dayItems`. That function drops a linked event, because in the grid its
 * recording row stands in for it - correct for drawing, wrong for counting: a day of six meetings, five of
 * them recorded, would report one event. This uses the same day-matching predicate `dayItems` uses, so an
 * event spilling across midnight is counted on every day it is drawn on, and nothing else.
 *
 * Kept independent of the recording count rather than phrased as a subset ("6 events, 5 recorded") because a
 * recording need not belong to any event at all - an ad-hoc take from the Record button has no calendar link,
 * and would vanish from a subset phrasing.
 */
export function dayEventCount(events: CalendarEvent[], key: string): number {
  return events.filter((e) => eventDayKeys([e]).has(key)).length;
}
```

- [ ] **Step 4: Write the pure tests**

Add to `apps/web/src/lib/calendar.test.ts`. Follow the file's existing import style and fixtures.

```ts
describe("dayEventCount", () => {
  const ev = (id: string, start: string, end: string) =>
    ({ id, summary: id, start, end, htmlLink: null }) as CalendarEvent;

  it("counts an event on the day", () => {
    const events = [ev("a", "2026-08-10T09:00:00Z", "2026-08-10T10:00:00Z")];
    expect(dayEventCount(events, "2026-08-10")).toBe(1);
  });

  it("counts an event on every day it spans, matching how the grid draws it", () => {
    const events = [ev("overnight", "2026-08-10T22:00:00Z", "2026-08-11T01:00:00Z")];
    expect(dayEventCount(events, "2026-08-10")).toBe(1);
    expect(dayEventCount(events, "2026-08-11")).toBe(1);
  });

  it("counts nothing on an unrelated day", () => {
    const events = [ev("a", "2026-08-10T09:00:00Z", "2026-08-10T10:00:00Z")];
    expect(dayEventCount(events, "2026-08-12")).toBe(0);
  });
});
```

> The overnight case is written against UTC instants but `eventDayKeys` walks **local** midnights, so the exact day keys depend on the runner's timezone. Before committing, run it and check the expectations match what `eventDayKeys` actually produces for those instants in this environment; adjust the instants (not the assertion shape) so the event genuinely spans two local days. An assertion you had to weaken to pass is worse than none.

- [ ] **Step 5: Wire it into the tab**

In `apps/web/src/components/nav/CalendarTab.tsx`, add `dayEventCount` to the existing import from `../../lib/calendar`, then add a memo beside `selectedItems` (`:108-111`):

```tsx
  // Counted from the events themselves, not from `selectedItems`: that list drops a linked event because its
  // recording row represents it, which would make the heading report one meeting on a day that had six.
  const selectedEventCount = useMemo(
    () => (selectedDay ? dayEventCount(events, selectedDay) : 0),
    [events, selectedDay],
  );
```

Pass it at the `DayHeading` call (`:165`):

```tsx
          <DayHeading dayKey={selectedDay} items={selectedItems} eventCount={selectedEventCount} locale={i18n.language} />
```

- [ ] **Step 6: Take the count out of `DayHeading`**

Replace the signature and the two derived lines (`:180-191`):

```tsx
/// The day's date and what is on it, pinned above the grid so it does not scroll away with the hours.
function DayHeading({
  dayKey: key,
  items,
  eventCount,
  locale,
}: {
  dayKey: string;
  items: DayItem[];
  /// Every event on this day, including those drawn as their recording. Passed in rather than derived from
  /// `items`, which cannot see them.
  eventCount: number;
  locale: string;
}) {
  const { t } = useTranslation("workspace");
  const [y, m, d] = key.split("-").map(Number);
  // Built from parts, never `new Date("2026-08-08")`: the string form is parsed as UTC midnight, which
  // renders the *previous* day anywhere west of Greenwich.
  const date = new Date(y, m - 1, d);
  const recordings = items.filter((i) => i.type === "recording").length;
  const counts = [
    eventCount > 0 ? t("calDayEventCount", { count: eventCount }) : null,
    recordings > 0 ? t("calDayRecordingCount", { count: recordings }) : null,
  ].filter(Boolean).join(" · ");
```

The rest of `DayHeading` is unchanged. Note a zero count is still omitted, so a day with no events reads "3 recordings" rather than "0 events · 3 recordings".

- [ ] **Step 7: Run the tests**

Run: `cd apps/web && npx vitest run src/lib/calendar.test.ts src/components/nav/CalendarTab.test.tsx`
Expected: PASS.

Run: `cd apps/web && npm test && npm run build`
Expected: PASS, clean typecheck.

- [ ] **Step 8: Commit**

```bash
git add apps/web/src/lib/calendar.ts apps/web/src/lib/calendar.test.ts apps/web/src/components/nav/CalendarTab.tsx apps/web/src/components/nav/CalendarTab.test.tsx
git commit -m "fix(calendar): the day heading counts every event, not only unrecorded ones"
```

---

### Task 2: A Calendar Event section on the recording

**Files:**
- Modify: `apps/web/src/lib/detailSection.ts:8-29`
- Modify: `apps/web/src/pages/RecordingDetail.tsx` (imports at `:15-23`; the `detailTabs` array, which opens at `:1208` and closes at `:1580`)
- Modify: `apps/web/src/locales/{en,de,es,fr}/workspace.json`
- Test: `apps/web/src/pages/RecordingDetail.test.tsx`

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces: the `SectionKey` value `"meeting"`. Task 3 calls `selectTab("meeting")`.

**Note on the intermediate state.** After this task the invite renders in **two** places - still on the card, and now in the section. That is expected; Task 3 removes it from the card. Do not try to fix it here.

- [ ] **Step 1: Write the failing tests**

Add to `apps/web/src/pages/RecordingDetail.test.tsx`. Read the file first for how it renders the page (its providers, its `../lib/api` mock, and how it supplies a recording with a `calendarLink`) and follow that setup exactly rather than inventing one.

```tsx
  // The invite is a section you drill into, like Notes or Formulas - the recording header stays above it and
  // the breadcrumb reads "Overview > Calendar Event".
  it("offers a Calendar Event section when the recording has a linked meeting", async () => {
    renderDetailWithLinkedMeeting();

    fireEvent.click(await screen.findByRole("button", { name: /calendar event/i }));

    expect(await screen.findByRole("navigation", { name: "Calendar Event" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Overview" })).toBeTruthy();
  });

  it("has no Calendar Event section when nothing is linked", async () => {
    renderDetailWithoutLinkedMeeting();

    expect(await screen.findByText("Meeting")).toBeTruthy(); // the card is still there, unlinked
    expect(screen.queryByRole("navigation", { name: "Calendar Event" })).toBeNull();
  });
```

`SectionBreadcrumb` renders a `<nav aria-label={title}>` with a back button labelled `hubBackToOverview` ("Overview"), which is what those queries target. The two `renderDetail*` helpers are yours to write from the file's existing setup; if it already has a render helper taking a recording, parameterise that instead of adding two new ones.

In this task the section is reached by `selectTab`, which Task 3 wires to the card. Until then, drive it however the file's other section tests drive `selectTab` (the hub's tiles call it) - if there is no existing route into a section from this test file, open the section by seeding the persisted key: `localStorage.setItem("diariz.detailSection", "meeting")` before rendering, and say so in a comment.

- [ ] **Step 2: Run and watch them fail**

Run: `cd apps/web && npx vitest run src/pages/RecordingDetail.test.tsx`
Expected: FAIL - no control named "Calendar Event" exists, and `"meeting"` is not an assignable `SectionKey`, so the file may not compile.

- [ ] **Step 3: Add the section key**

`apps/web/src/lib/detailSection.ts` - add `"meeting"` to BOTH the union (`:8-16`) and the `SECTIONS` array (`:20-29`), so it survives the persistence round-trip in `initialSection`:

```ts
export type SectionKey =
  | "hub"
  | "transcript"
  | "minutes"
  | "actions"
  | "notes"
  | "speakers"
  | "files"
  | "formulas"
  | "meeting";
```

```ts
const SECTIONS: readonly SectionKey[] = [
  "hub",
  "transcript",
  "minutes",
  "actions",
  "notes",
  "speakers",
  "files",
  "formulas",
  "meeting",
];
```

Nothing else in that file changes. A recording with no linked meeting cannot land on the section even when `"meeting"` is the persisted key, because `DetailSections:36-37` already falls back to the hub when the active key matches no section - which is exactly what the second test pins.

- [ ] **Step 4: Add the copy**

`en/workspace.json`: `"detailSectionMeeting": "Calendar Event",`
`de/workspace.json`: `"detailSectionMeeting": "Kalendertermin",`
`es/workspace.json`: `"detailSectionMeeting": "Evento de calendario",`
`fr/workspace.json`: `"detailSectionMeeting": "Événement de l'agenda",`

Place it beside the other `detailTab*` / `detailSection*` keys in each file, keeping each file's existing ordering.

- [ ] **Step 5: Add the section**

In `apps/web/src/pages/RecordingDetail.tsx`, extend the existing `SectionIcons` import (`:15-23`) with `CalendarIcon`, and add two component imports beside the other component imports:

```tsx
import CalendarEventDetails from "../components/CalendarEventDetails";
import SeriesRecordings from "../components/SeriesRecordings";
```

Then append this entry at the END of the `detailTabs` array, immediately before its closing `];` (`:1580`):

```tsx
    // The meeting this recording came from. Spread conditionally rather than rendered empty: a recording with
    // no link must not offer a section at all, so that a persisted "meeting" key falls through to the hub.
    // Calendar is personal-only, so it is absent in a shared room for the same reason the card is.
    ...(rec.calendarLink && !inSharedRoom
      ? [
          {
            key: "meeting" as const,
            label: t("workspace:detailSectionMeeting"),
            icon: <CalendarIcon size={15} />,
            content: (
              <div className="space-y-3">
                <CalendarEventDetails
                  showTitle
                  event={
                    linkedEvent ?? {
                      id: rec.calendarLink.eventId,
                      summary: rec.calendarLink.summary,
                      start: rec.calendarLink.start,
                      end: rec.calendarLink.end,
                      htmlLink: rec.calendarLink.htmlLink,
                    }
                  }
                />
                {linkedEvent?.recurring && <SeriesRecordings eventId={rec.calendarLink.eventId} />}
              </div>
            ),
          },
        ]
      : []),
```

`CalendarIcon` defaults to size 13; the other section icons are rendered at 15, hence the explicit `size={15}`.

- [ ] **Step 6: Run the tests**

Run: `cd apps/web && npx vitest run src/pages/RecordingDetail.test.tsx`
Expected: PASS.

Run: `cd apps/web && npm test && npm run build`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add apps/web/src/lib/detailSection.ts apps/web/src/pages/RecordingDetail.tsx apps/web/src/pages/RecordingDetail.test.tsx apps/web/src/locales/en/workspace.json apps/web/src/locales/de/workspace.json apps/web/src/locales/es/workspace.json apps/web/src/locales/fr/workspace.json
git commit -m "feat(recording): a Calendar Event section for the linked meeting"
```

---

### Task 3: The card becomes a clickable summary

**Files:**
- Modify: `apps/web/src/components/detail/MeetingCard.tsx:19-36` (props), `:79-104` (the body)
- Modify: `apps/web/src/pages/RecordingDetail.tsx` (the `meetingCard` block, around `:1165-1174`)
- Modify: `apps/web/src/components/detail/MeetingCard.test.tsx`
- Modify: `apps/web/src/locales/{en,de,es,fr}/workspace.json`

**Interfaces:**
- Consumes: the `"meeting"` `SectionKey` from Task 2.
- Produces: `MeetingCard` gains a required `onOpen: () => void` prop.

- [ ] **Step 1: Write the failing tests**

Add to `apps/web/src/components/detail/MeetingCard.test.tsx`, and add `onOpen: vi.fn()` to the `handlers()` factory at `:40-44` so every existing render supplies it.

```tsx
  it("summarises the meeting and opens the section when the summary is activated", () => {
    renderWithClient(
      <MeetingCard
        calendarLink={link}
        linkedEvent={{ ...event, location: "Room 4", attendees: [{}, {}, {}] as never }}
        suggestion={null}
        calendarConnected
        {...h}
      />,
    );

    expect(screen.getByText("Room 4")).toBeTruthy();
    expect(screen.getByText("3 attendees")).toBeTruthy();
    // The invite's body is no longer inlined - that is the whole point of the change.
    expect(screen.queryByText("Apologies it is late for Europe.")).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: /QnR Competences merging to one/ }));
    expect(h.onOpen).toHaveBeenCalled();
  });

  // The header's actions sit outside the click target on purpose. If the whole card ever becomes clickable,
  // these two start opening the section as a side effect of changing or unlinking the meeting.
  it("does not open the section when the header actions are used", () => {
    renderWithClient(<MeetingCard calendarLink={link} linkedEvent={event} suggestion={null} calendarConnected {...h} />);

    fireEvent.click(screen.getByRole("button", { name: "Change meeting" }));
    fireEvent.click(screen.getByRole("button", { name: "Unlink meeting" }));

    expect(h.onOpen).not.toHaveBeenCalled();
  });
```

- [ ] **Step 2: Run and watch them fail**

Run: `cd apps/web && npx vitest run src/components/detail/MeetingCard.test.tsx`
Expected: FAIL - `onOpen` is not a prop, there is no button named after the event, and the description IS currently rendered.

- [ ] **Step 3: Add the copy**

`en/workspace.json`:

```json
  "calAttendeeCount_one": "{{count}} attendee",
  "calAttendeeCount_other": "{{count}} attendees",
  "hubMeetingOpen": "Open calendar event: {{name}}",
```

`de/workspace.json`:

```json
  "calAttendeeCount_one": "{{count}} Teilnehmer",
  "calAttendeeCount_other": "{{count}} Teilnehmer",
  "hubMeetingOpen": "Kalendertermin öffnen: {{name}}",
```

`es/workspace.json`:

```json
  "calAttendeeCount_one": "{{count}} asistente",
  "calAttendeeCount_other": "{{count}} asistentes",
  "hubMeetingOpen": "Abrir evento de calendario: {{name}}",
```

`fr/workspace.json`:

```json
  "calAttendeeCount_one": "{{count}} participant",
  "calAttendeeCount_other": "{{count}} participants",
  "hubMeetingOpen": "Ouvrir l'événement de l'agenda : {{name}}",
```

`calAttendees` ("Attendees") already exists and stays - it is the section label inside `CalendarEventDetails`, a different string in a different place.

- [ ] **Step 4: Rewrite the card's linked body**

In `apps/web/src/components/detail/MeetingCard.tsx`:

Replace the `CalendarEventDetails` / `SeriesRecordings` imports with the formatters:

```tsx
import { formatLongDate, formatTimeHm } from "../../lib/format";
```

Add the prop (in both the destructure at `:19-27` and the type at `:27-36`):

```tsx
  onOpen,
```
```tsx
  /// Open the recording's Calendar Event section. The card is a summary; the invite itself lives there.
  onOpen: () => void;
```

Add `i18n` to the translation hook so the date can be localised:

```tsx
  const { t, i18n } = useTranslation("workspace");
```

Replace the linked branch of the body (`:80-95`) with:

```tsx
        {linked ? (
          <MeetingSummary
            title={(linkedEvent?.summary ?? calendarLink.summary) || t("meetingUntitled")}
            start={linkedEvent?.start ?? calendarLink.start}
            end={linkedEvent?.end ?? calendarLink.end}
            location={linkedEvent?.location ?? null}
            attendees={linkedEvent?.attendees?.length ?? 0}
            locale={i18n.language}
            onOpen={onOpen}
          />
        ) : suggestion ? (
```

and add this component beside `CardAction` at the bottom of the file:

```tsx
/// The linked meeting in brief, and the way into the Calendar Event section.
///
/// Only the summary is the click target, not the whole card: "Change meeting" and "Unlink meeting" live in
/// the header, and wrapping them in a click target would need stopPropagation on both - a trap that breaks
/// silently the next time someone adds a third action.
///
/// Location and the attendee count come from the live event only. Until it resolves - and forever, for an
/// event since deleted - the card falls back to the snapshot stored on the link, which carries just the
/// title and the times, so those two lines simply do not appear.
function MeetingSummary({
  title,
  start,
  end,
  location,
  attendees,
  locale,
  onOpen,
}: {
  title: string;
  start: string;
  end: string;
  location: string | null;
  attendees: number;
  locale: string;
  onOpen: () => void;
}) {
  const { t } = useTranslation("workspace");
  return (
    <button
      type="button"
      onClick={onOpen}
      aria-label={t("hubMeetingOpen", { name: title })}
      className="w-full rounded-xl border border-gray-200 px-3 py-2 text-left hover:bg-gray-50 dark:border-gray-700 dark:hover:bg-gray-700/50"
    >
      <span className="block truncate text-sm font-medium text-indigo-600 dark:text-indigo-400">{title}</span>
      <span className="mt-0.5 block text-xs text-gray-600 dark:text-gray-400">
        {formatLongDate(start, locale)} · {formatTimeHm(start)} - {formatTimeHm(end)}
      </span>
      {location && (
        <span className="mt-0.5 block truncate text-xs text-gray-500 dark:text-gray-500">{location}</span>
      )}
      {attendees > 0 && (
        <span className="mt-0.5 block text-xs text-gray-500 dark:text-gray-500">
          {t("calAttendeeCount", { count: attendees })}
        </span>
      )}
    </button>
  );
}
```

Use `<span className="block">` rather than `<div>` inside the button: a `<div>` inside a `<button>` is invalid HTML and jsdom will warn, which breaks the pristine-output rule.

Update the component's doc comment at `:13-14`, which still says a linked invite "is far too tall for a tile" - that reasoning described the old inline rendering and is now stale.

- [ ] **Step 5: Retire the card's series tests**

The card no longer mounts `SeriesRecordings`, so the three tests at `:96-111` (`"does not fetch other recordings..."`, `"fetches other recordings..."`, `"does not fetch other recordings while the live event hasn't loaded yet"`) now assert the behaviour of a component that is not there. **Delete all three.** Their coverage moved to Task 2's section.

With them gone, `vi.mock("../../lib/api", ...)` (`:11-13`), the `api` import, the `QueryClientProvider` wrapper and the `mockClear` in `beforeEach` are all dead. Remove them and simplify `renderWithClient` to a plain `render` (renaming it accordingly), so the file does not carry a provider for a query nothing makes.

If deleting them makes any *other* test in the file fail, stop and report - that would mean the card still depends on something you removed.

- [ ] **Step 6: Wire the card to the section**

In `apps/web/src/pages/RecordingDetail.tsx`, add to the `<MeetingCard .../>` props:

```tsx
      onOpen={() => selectTab("meeting")}
```

- [ ] **Step 7: Run the tests**

Run: `cd apps/web && npx vitest run src/components/detail/MeetingCard.test.tsx src/pages/RecordingDetail.test.tsx`
Expected: PASS.

Run: `cd apps/web && npm test && npm run build`
Expected: PASS, clean typecheck, no jsdom warnings.

- [ ] **Step 8: Check it in the browser**

jsdom computes no layout, so no test above shows whether the summary reads well. Start the dev server (`.claude/launch.json` has a `web` config on port 5199), open a recording with a linked meeting, and confirm: the card shows title, time, and location/attendees when the event has them; clicking the summary opens the section with the recording header still above it and the breadcrumb reading "Overview › Calendar Event"; the back chip returns to the hub. Check both themes.

- [ ] **Step 9: Commit**

```bash
git add apps/web/src/components/detail/MeetingCard.tsx apps/web/src/components/detail/MeetingCard.test.tsx apps/web/src/pages/RecordingDetail.tsx apps/web/src/locales/en/workspace.json apps/web/src/locales/de/workspace.json apps/web/src/locales/es/workspace.json apps/web/src/locales/fr/workspace.json
git commit -m "feat(recording): the meeting card becomes a summary that opens the invite"
```

---

### Task 4: Release and docs

**Files:**
- Modify: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`
- Modify: `apps/web/src/lib/releases.ts`
- Modify: help content under `apps/web/src/content/help/en/`

**Interfaces:**
- Consumes: everything above.
- Produces: the release.

- [ ] **Step 1: Bump the version**

`version.json` currently reads `0.202.0`. Task 2 adds a section, so this is a **functional enhancement: minor +1, build reset** → **`0.203.0`**. Set that identical value in all five files. `apps/web/src/lib/versionMirrors.test.ts` fails the build if any drifts.

`apps/desktop/package.json` moves in lockstep even though no desktop release is cut - the mirror test asserts it, and desktop version numbers may skip.

- [ ] **Step 2: Add the release entry**

Prepend to `RELEASES` in `apps/web/src/lib/releases.ts`. Get the PR number with `gh pr list --limit 1 --state all --json number --jq '.[0].number'` and use the next one; confirm it once the PR is open, since Dependabot PRs and issues share the sequence.

```ts
  {
    version: "0.203.0",
    date: "2026-08-10",
    pr: 0, // replace with the real PR number
    headline: "A clearer day count, and the invite one click away",
    summary:
      "The calendar day heading now counts every event on the day, including the ones you have already " +
      "recorded - previously a day of six meetings, five of them recorded, reported one event. And a " +
      "recording's linked meeting is now a short summary you click through to, opening the full invite as a " +
      "Calendar Event section of the recording, the same way you open Notes or Actions. The recordings page " +
      "no longer carries the whole invite.",
    added: [
      "A Calendar Event section on a recording with a linked meeting, holding the full invite.",
    ],
    changed: [
      "The calendar day heading counts every event on the day, not only those without a recording.",
      "The linked meeting on a recording is now a summary - title, time, location and attendee count - that opens the full invite.",
    ],
  },
```

Adjust `version` to whatever Step 1 chose. `releases.test.ts` asserts `RELEASES[0].version === version.json`.

- [ ] **Step 3: Leave the feature inventories alone, deliberately**

The README Features table, `docs/features.md` and the About-box `CAPABILITIES` all describe *what the app can do*. Nothing new can be done here - the invite moved behind a click and a count was corrected - so none of those three changes. `docs/Data_Schema.md` does not change either (no schema).

Check `docs/Overall_Synopsis_of_Platform.md` for any sentence stating that the linked invite renders on the recording's hub or Overview; if one exists, correct it to say it is a section you drill into. If none exists, leave the file alone and say so in your report.

- [ ] **Step 4: Update the help content**

Search `apps/web/src/content/help/en/` for articles describing where a linked meeting's details appear on a recording. Update only where the **behaviour a user relies on** changed: the invite is now reached by clicking the meeting summary rather than being on the page. Help content is **ASCII only** and each article keeps its `title`/`summary`/`group`/`order` front matter; `content/help/helpContent.test.ts` enforces both. If no article describes this, change nothing and say so.

- [ ] **Step 5: Run everything**

```bash
cd apps/web && npm test && npm run build
```

Expected: PASS. `versionMirrors.test.ts`, `releases.test.ts` and `helpContent.test.ts` are the ones that catch a half-done release.

- [ ] **Step 6: Check for dashes**

Write the diff to a file and decode it as UTF-8 explicitly - do **not** pipe `git diff` into python on this machine, which decodes as cp1252 and reports a false zero:

```bash
git diff main...HEAD -- "apps/web/src/locales/*" "apps/web/src/lib/releases.ts" "apps/web/src/content/help/*" > branch.diff
python -c "import io; t=io.open('branch.diff',encoding='utf-8').read(); [print(repr(l)) for l in t.splitlines() if l.startswith('+') and ('—' in l or '–' in l)]"
```

Expected: no output. Delete `branch.diff` - do not commit it.

- [ ] **Step 7: Commit**

```bash
git add version.json apps/web/package.json apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj integrations/n8n-nodes-diariz/package.json apps/web/src/lib/releases.ts
git commit -m "chore(release): 0.203.0 - day event count and the Calendar Event section"
```

Stage any docs or help files you actually changed alongside these.

---

## Self-Review

**Spec coverage.** Change 1 → Task 1 (`dayEventCount`, the wiring, the zero-omission rule). Change 2's section → Task 2 (key, label, entry, the fallback-to-hub test). Change 2's card → Task 3 (summary fields, body-only click target, `SeriesRecordings` moved). Both new i18n keys the spec named are in Tasks 2 and 3. The release checklist → Task 4, including the spec's judgement that the feature inventories do not change.

**One thing the spec named that this plan does not do.** The spec's accessibility note says the section "is reached by state, not a route". That is inherent to using `selectTab`, so there is no step for it - flagged here so a reviewer does not go looking for one.

**Timezone hazard flagged, not hidden.** Task 1's overnight test depends on the runner's local timezone, because `eventDayKeys` walks local midnights while the fixture is written in UTC. The step tells the implementer to verify the day keys rather than adjust the assertion until it passes.

**Type consistency.** `dayEventCount(events, key)` in Task 1 is consumed nowhere else. `SectionKey` gains `"meeting"` in Task 2 and is used as `selectTab("meeting")` in Task 3. `MeetingCard`'s new `onOpen: () => void` is added in Task 3 and supplied in the same task by `RecordingDetail`; the `handlers()` test factory is updated in the same step so no existing test renders without it.

**Known intermediate state, deliberate.** Between Tasks 2 and 3 the invite renders twice - once on the card, once in the section. Task 2's brief says so explicitly so its reviewer does not flag it as duplication.
