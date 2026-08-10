# Calendar day count and the meeting section

**Date:** 2026-08-10
**Status:** approved for planning

Two changes, both web-only.

1. The Calendar tab's day heading undercounts events, because an event that has a recording is not counted at all.
2. A recording's linked meeting shows the whole invite inline on the recordings page. It becomes a compact, clickable summary that opens a **Calendar Event** section of the recording, matching every other drill-in section.

**Deployment surface: server redeploy only.** No API, no schema, no migration, nothing under `apps/desktop/`.

---

## Change 1: count every event on the day

**What is wrong.** `dayItems` (`apps/web/src/lib/calendar.ts:172`) deliberately skips an event whose recording exists, because the recording row stands in for it in the grid:

```ts
if (linkedEventIds.has(e.id)) continue; // represented by its recording row (both icons)
```

`DayHeading` (`apps/web/src/components/nav/CalendarTab.tsx:186-191`) then derives its event count from what survived:

```ts
const recordings = items.filter((i) => i.type === "recording").length;
const events = items.length - recordings;
```

So the heading counts **what is drawn**, not what is on the calendar. A day with six events, five of them recorded, reads "1 event · 5 recordings" - which states there was one meeting that day.

**The fix.** A new pure function in `lib/calendar.ts`:

```ts
export function dayEventCount(events: CalendarEvent[], key: string): number
```

It counts every event covering `key`, using the **same** predicate `dayItems` uses - `eventDayKeys([e]).has(key)` - so an event that spills in from the previous day is counted on both days exactly as it is drawn on both days. It differs from `dayItems` in one respect only: it does not skip linked events.

`CalendarTab` computes it for the selected day (memoised alongside `selectedItems`, which already depends on the same inputs) and passes it to `DayHeading` as a prop. `DayHeading` keeps deriving the recording count from `items` as it does today.

The day then reads "**6 events · 5 recordings**". `DayHeading` keeps its existing rule of omitting a count that is zero, so a day with no events still reads "3 recordings" rather than "0 events · 3 recordings". The `calDayEventCount` / `calDayRecordingCount` keys are unchanged - only the number fed to the first one changes.

**Why two independent counts rather than "6 events, 5 recorded".** A recording need not belong to any event - an ad-hoc take from the Record button has no calendar link at all. Phrasing the second number as a subset of the first would make those recordings invisible in the heading, or force a third clause. Keeping the two numbers independent means each is a true statement about the day on its own terms.

**The one oddity, accepted.** The grid draws one event chip while the heading says six, because five of the events appear as their recordings. That is the correct reading: the heading describes the day, not the chips.

**Testing.** `dayEventCount` is pure, so it is unit-tested directly: a linked event still counts; an event spilling across midnight counts on both days; an unrelated day counts zero. The regression that prompted this - linked events vanishing from the count - is exactly what the first case pins. `CalendarTab.test.tsx` gets a rendering test with one linked and one unlinked event asserting the heading text.

---

## Change 2: a Calendar Event section

**What is wrong.** `MeetingCard` (`apps/web/src/components/detail/MeetingCard.tsx:82`) renders the entire `CalendarEventDetails` inline. For a sparse invite that is a title and a time; for a real one it is the calendar name, a recurring badge, when/where/organiser, a collapsible attendee list, the description, and the recurring-series list - all of it pushed onto the recordings page above everything else.

**The shape it should take.** The recording-detail page is a hub plus sections you drill into (Transcript, Minutes, Actions, Notes, Speakers, Files, Formulas). `DetailSections` renders exactly one section with a `SectionBreadcrumb`: a **Back to overview** chip, a chevron, then the section's icon and name. The recording header stays above it throughout.

The linked meeting becomes one of those sections. Nothing about the chrome is new - it is the existing pattern with a new entry.

### The section

- A new `SectionKey` **`"meeting"`** in `apps/web/src/lib/detailSection.ts`, added to both the union and the `SECTIONS` array so it survives the persistence round-trip.
- Rendered through the existing `DetailSections`, so the breadcrumb reads **Back to overview › [calendar icon] Calendar Event**. The icon is the `CalendarIcon` the card already uses, so the two views agree visually.
- Its content is the full `CalendarEventDetails` (with `showTitle`), plus `SeriesRecordings` when the event recurs.
- Its name comes from a new key, **`detailSectionMeeting`** = "Calendar Event" (all four locales). The card's own header keeps the existing `hubMeetingTitle` ("Meeting"), which is a different string for a different place and is not being reused.
- The card's attendee-count line needs a second new key, **`calAttendeeCount`**, pluralised (`_one`/`_other`), since the existing `calAttendees` is a bare section label with the count rendered separately.
- It is added to `detailTabs` **only** when the recording has a `calendarLink` and the viewer is not in a shared room - the same two conditions that already gate the card, since calendar is personal-only.

**A recording with no linked event cannot land on it.** `DetailSections:36-37` already falls back to the hub when the active key matches no section, and the section key is persisted globally across recordings. So someone who was last in the Calendar Event section and opens an unlinked recording lands on the hub, with no new code needed. This is the existing behaviour for every section and needs a test rather than a change.

### The card

The card keeps its chrome, header row, subtitle and both actions exactly as they are. Its **body** changes from the whole invite to a summary:

- the event title,
- the time,
- the location, when the event has one,
- the attendee count, when the event has attendees.

That body becomes the click target that opens the section.

**Why the body and not the whole card.** "Change meeting" and "Unlink meeting" live in the header row. Making the whole card clickable would put two buttons inside a click target and require `stopPropagation` on both - a trap that breaks silently the next time someone adds a third action. Making the body the target keeps the actions ordinary buttons.

**Loading behaviour.** The card falls back to the snapshot stored on the link (`summary`, `start`, `end`, `htmlLink`) until the live event resolves. The snapshot carries no location and no attendees, so those two lines appear a beat after load on a slow calendar fetch, and never for an event that has since been deleted. That is the same fallback the card already relies on.

**`SeriesRecordings` moves off the card** into the section. It stays mounted on the standalone calendar-event page, which is unchanged.

### Accessibility

The clickable body is a real `<button>` (or a `<Link>`-styled button) with an accessible name naming the event, not a `<div>` with an `onClick`. The section it opens is reached by state, not a route, exactly as every other section is.

**Testing.** `MeetingCard.test.tsx`: the summary renders title/time/location/attendee count; the body's activation calls `onOpen`; **activating "Change meeting" or "Unlink meeting" does not call `onOpen`** - that last one is the assertion that would catch a future whole-card click target. `RecordingDetail`: the section appears when a link exists and is absent when it does not.

---

## Out of scope

- The standalone `/calendar-event/:eventId` page, reached from the Calendar tab. Unchanged.
- The suggestion and unlinked states of the card. Unchanged.
- `DayGrid.tsx:175` and `DayBlock.tsx:241` build `/calendar-event/...` without a room prefix. Harmless today - the calendar overlay is personal-only, so those links only render in a personal room where the prefix is empty - and out of scope here.

## Release checklist

Web-only, and change 2 adds a section, so this is a **functional enhancement: minor +1, build reset** - `0.202.0` → **`0.203.0`**.

1. `version.json` and its four mirrors (`apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`).
2. `RELEASES[0]` in `apps/web/src/lib/releases.ts`, matching `version.json`.
3. The About-box `CAPABILITIES` row, README Features row and `docs/features.md` bullet only if the feature inventory actually changes - this moves existing content behind a click rather than adding a capability, so a wording touch at most.
4. No `docs/Data_Schema.md` change (no schema).
5. `docs/Overall_Synopsis_of_Platform.md` only if it describes where the linked invite renders.
6. Help content: update where the behaviour a user relies on changed - the invite is now a click away from the recording rather than on it.
7. **No em or en dashes** in any user-facing string, i18n catalogue, release note or help article. All four locales for every new key (`en`, `de`, `es`, `fr`).
