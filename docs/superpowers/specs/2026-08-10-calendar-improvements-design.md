# Calendar improvements design

**Date:** 2026-08-10
**Status:** approved for planning

Five changes to the calendar surfaces, shipped as one release.

1. Toast when a recording ends by itself.
2. The day heading counts "events", not "meetings".
3. Show that an event recurs, and list previous recordings of the same series.
4. Attendees becomes a disclosure, collapsed by default.
5. A calendar-driven auto-end asks "Extend this meeting?" while people are still talking.

**Deployment surface: server redeploy only.** Web + API + one migration. No desktop
release: `apps/desktop` is not touched (see "Outlook needs no desktop change" below).

---

## Background: the three calendar sources

Everything in item 3 turns on how each source identifies a recurring series. All three
already carry what is needed; none of it is currently read.

| Source | Recurs? | Series key | Where it lives today |
|---|---|---|---|
| Google | `recurringEventId` present on an instance | that value (the master's id) | in the API response, `GoogleCalendarClient.ParseEvent` never reads it |
| `.ics` | `source.RecurrenceRule is not null` | the raw `UID` | `IcsCalendar.MakeId` computes both, then discards the fact |
| Outlook | `OutlookCalendarEvent.IsRecurring` | `Uid` before the `#` | already persisted, end to end |

### Outlook needs no desktop change

`OutlookCalendarEvent.Uid` is documented as a per-occurrence id, and on that reading
Outlook would have no series key at all - which is what made this look like a desktop
release. That documentation is stale. `apps/desktop/src/outlookSync.js:84` says:

```js
uid: raw.isRecurring === true ? `${raw.uid}#${raw.start}` : raw.uid,
```

with the comment recording that Outlook returned one `GlobalAppointmentID` per *series*,
not per occurrence ("87 occurrences came back sharing 24 ids - one per series"). So a
recurring occurrence is already stored as `{seriesId}#{occurrenceStart}`, and the series
key is `Uid` up to the first `#`. Nothing needs to change on the desktop.

Two consequences to respect:

- Only split when `IsRecurring` is true. `dedupeUids` (outlookSync.js:112) appends the same
  `#{start}` suffix to a *non*-recurring event when two share a uid, so the separator alone
  does not mean "recurring".
- The `Uid` XML doc on `OutlookCalendarEvent` and the parallel comment in
  `OutlookReader/Program.cs:225-228` both assert the per-occurrence reading. Correct them in
  this PR; leaving them is how the next reader repeats the mistake.

---

## Item 1: toast when a recording ends by itself

**Problem.** Three code paths call `Recorder.stop()` automatically - the schedule watcher
(`Recorder.tsx:479`), the silence watcher (`Recorder.tsx:806`), and a replacing `start()` -
and the user is told nothing. A recording they expected to be running has simply gone.

**Change.** `stop()` takes an optional reason:

```ts
type StopReason = "schedule" | "calendar" | "silence";
function stop(reason?: StopReason)
```

Absent means the user pressed Stop. Each reason toasts its own line, so the user learns
*why* rather than just *that*:

| Reason | Copy (en) |
|---|---|
| `schedule` | Recording stopped - your auto-stop time was reached. |
| `calendar` | Recording stopped - the meeting has ended. |
| `silence` | Recording stopped - the meeting went quiet. |

The silence line deliberately names no duration. `silenceSeconds` is configured in seconds
and may not be a whole number of minutes, so interpolating it needs a duration formatter and
a plural rule in four languages to say something the user already configured and can read in
Preferences.

A user-pressed Stop stays silent (they know). A take replaced by a new `start()` stays
silent (it is not an ending, it is a handover, and the new recording is the feedback).

**Wiring.** `useToast()` from `lib/toast.tsx`. `ToastProvider` is at
`WorkspaceLayout.tsx:26` and wraps `Workspace` -> `CaptureBar` -> `Recorder`, so the real
provider is in scope.

> `toast.tsx:14` defaults the context to a **silent no-op** rather than throwing. A
> component rendered outside the provider therefore compiles, passes every test, and
> shows nothing - the exact shape of the `HubPopoverProvider` defect in PR #500. A test
> must render the real `WorkspaceLayout` tree and prove a toast raised from the recorder's
> position reaches the provider. Asserting `showToast` was called on a mock does not
> establish this.

## Item 2: "6 events"

`CalendarTab.tsx:189` renders `calDayMeetingCount`. Rename the key to `calDayEventCount`
and change the value in all four catalogues, so the key stops disagreeing with what it
renders:

| Locale | one | other |
|---|---|---|
| en | `{{count}} event` | `{{count}} events` |
| de | `{{count}} Termin` | `{{count}} Termine` (unchanged - already correct) |
| es | `{{count}} evento` | `{{count}} eventos` |
| fr | `{{count}} événement` | `{{count}} événements` |

`calDayRecordingCount` is untouched. The local variable `meetings` in `DayHeading` renames
to `events` with it.

## Item 3: recurrence badge and series history

### 3a. Carry recurrence through the projection

`CalendarEvent` (`GoogleCalendarClient.cs:22` - the shared projection for all three
sources) gains two optional fields, defaulted so no existing construction site breaks:

```csharp
bool Recurring = false, string? SeriesId = null
```

Populated at each source:

- **Google** (`ParseEvent`): read `recurringEventId`; present means `Recurring = true` and
  that value is the `SeriesId`.
- **`.ics`** (`ParseEvents`/`MakeId`): `Recurring = source.RecurrenceRule is not null`;
  `SeriesId` = the same `uid` `MakeId` already computes. Lift the uid resolution (including
  the blank-uid `noid-{stamp}` fallback) out of `MakeId` so the id and the series key are
  derived from one expression rather than two that must agree.
- **Outlook** (`OutlookCalendarStore`): `Recurring = e.IsRecurring`; `SeriesId` = `e.Uid`
  before the first `#`, **only** when `IsRecurring`.

`SeriesId` is namespaced by construction - a Google master id, an `.ics` UID and an
Outlook Global Object ID cannot collide in practice - and is never used as a route
parameter, so it needs no length ceiling beyond the column's.

The web `CalendarEvent` type gains `recurring?: boolean`. It does **not** gain `seriesId`:
the browser never needs the key, because the sibling lookup is resolved server-side from
the event id (below). Keeping it off the wire keeps a long opaque internal id out of the
client.

### 3b. Store the series key on the link

`RecordingCalendarLink` gains a nullable `SeriesId`, `HasMaxLength(1024)` to match the
`EventId`/`CalendarId`/`Summary` columns beside it. Written in `PutCalendarLink`
(`RecordingsController.cs:860`), which is the **only** production site that constructs a
link, so one assignment covers both the manual pick and the recorder's auto-link.

**Why stored and not derived on read.** Deriving siblings at query time would mean joining
`RecordingCalendarLink` to `OutlookCalendarEvents` for Outlook links - and that mirror is a
**rolling window** (`PastDays`/`FutureDays`, swept every sync). Last month's standup
occurrence is gone from it, so the join would silently lose exactly the history the feature
exists to show. A stored key is durable and window-independent.

**Why raw and not hashed.** Hashing to a fixed-width token (as `OutlookEventId` does for
public ids) would be tidier, but it cannot be reproduced in SQL, and the backfill below is
where most of this feature's value lands for an existing user.

### 3c. Backfill in the migration

New links populate themselves. Existing ones are backfilled **in the migration**, not on
startup - the seeder runs every boot, so a backfill there re-applies itself and undoes
later changes.

- **Google**: an instance id is `{masterId}_{yyyyMMddTHHmmssZ}`. Split on the last `_` and
  accept only when the suffix matches that exact shape.
- **`.ics`**: our own `MakeId` produces the same `{uid}_{stamp}` shape, and the uid may
  itself contain `_`, so the same last-underscore-plus-shape-check rule applies.
- **Outlook**: join on `l."EventId" = 'outlook:' || e."Id"::text` (that is exactly
  `OutlookEventId.EventKey`) and take `split_part(e."Uid", '#', 1)` where `e."IsRecurring"`.
  Covers occurrences still inside the rolling window; older ones stay null.

Everything else stays null, which reads as "not part of a series I can identify" and simply
shows no history. `MaintenanceController.CurrentFormat` does **not** move: this is an
additive nullable column, so an older backup restores cleanly.

### 3d. Surface it

**Badge.** `CalendarEventDetails` shows a small "Recurring" badge beside the calendar name
when `event.recurring`. Nothing about the badge depends on the series key, so it works for
every source from day one, including Outlook links predating the backfill.

**History.** New endpoint:

```
GET /api/calendar/events/{eventId}/recordings
```

Resolves the event through `ICalendarAggregator`, reads its `SeriesId`, and returns the
signed-in user's recordings whose link carries the same `SeriesId` - excluding the event
itself, newest first, capped at 10. Empty list (not 404) when the event does not recur or
nothing matches, so the client has one shape to render. Ownership is enforced by joining
through `Recording.UserId`, as every other recording query does.

> `CalendarController`'s constructor currently takes three services and gains
> `DiarizDbContext`. Controller constructor changes have a second construction site in
> `tests/Diariz.Api.IntegrationTests`, which a unit-test-only run does not compile. Build
> `Diariz.slnx` before pushing.

**Component.** A new `SeriesRecordings` owns that query and renders the list; it is placed
next to `CalendarEventDetails` by both the event page and the recording Overview.
`CalendarEventDetails` stays presentational - it takes an event and renders it, with no
data fetching of its own.

## Item 4: attendees collapses

The attendees block in `CalendarEventDetails.tsx:83-104` becomes a real disclosure:
a `<button>` with `aria-expanded` and `aria-controls` pointing at the list, collapsed on
first render. The summary keeps the count visible while closed ("Attendees (7)"), so
collapsing hides the names, not the fact that there are seven of them.

`CalendarEventDetails` is shared by the event-preview page and the recording Overview, so
this applies to both. That is deliberate: one component behaving two ways by surface is
harder to reason about than one that behaves consistently, and the attendee list is equally
incidental in both places.

## Item 5: extend this meeting

**Problem.** A calendar-started take ends at `resolveCalendarStopAt` whether or not the
meeting is still going. An overrunning meeting loses its ending.

### The two stop targets split

Today `start()` folds the calendar stop into `scheduledStopRef` with `earlierStop`
(`Recorder.tsx:797`), which makes them indistinguishable when the watcher fires - so it
cannot know whether to ask or to stop. They separate:

- `scheduledStopRef` - the user's own auto-stop. Unchanged meaning.
- `calendarStopRef` - the calendar-derived stop. New.
- The "stops at HH:MM" display keeps showing `earlierStop(scheduled, calendar)`, so nothing
  changes visually.
- The watcher checks both. The user's own auto-stop still ends the take at once
  (`stop("schedule")`): they asked for a hard stop and they get one. Only the calendar stop
  asks.

### Ask only when someone is talking

"Still talking" is already tracked. `SilenceState` carries `heardSound` and `silentMs`;
`SilenceWatcher` currently keeps that state private and only reports the terminal event.
It gains a reader so the recorder can ask what the room is doing right now.

New pure helper in `calendarRecording.ts`, alongside the existing rules:

```ts
export function shouldPromptExtend(state: SilenceState, recentMs: number): boolean
```

True when sound has been heard and `silentMs < recentMs` (that is: someone spoke recently).
False otherwise, which is the empty-room case - there is nobody to ask, so the take ends
with the `calendar` toast from item 1.

This is safe to reach for unconditionally: `resolveCalendarStopAt` returns null unless the
calendar setting is enabled, and the silence watcher starts under that same condition, so
wherever a calendar stop exists the watcher exists too.

### The prompt

A persistent panel **floating below the record controls**, in the recorder's `relative`
root - the same treatment the unsaved-recording and attach-retry banners already get, and
for the same recorded reason: `Recorder.tsx:1123` and `:1300` both note that the capture bar
is fixed-height, so anything added to its flow grows the bar and pushes the page down. That
is also why the status bar is not an option here: `setStatus` takes text and a tone, with no
room for the two buttons this needs. Not a toast (auto-dismisses after 4s) and not a modal
(steals focus mid-meeting). Two actions:

- **Keep recording** - pushes `calendarStopRef` out by the user's existing "minutes after
  end" value (`CalendarRecordingSettings.afterMinutes`, default 3) via a new pure
  `extendedStopAt(nowMs, afterMinutes)`, and re-arms. If the meeting is *still* running at
  the new target, it asks again.
- **Stop now** - `stop()` with **no** reason, so it raises no toast. Pressing it is a user
  stop like any other: the user just decided the meeting is over, and telling them the
  meeting is over would be absurd. This is why the reason parameter is optional rather than
  derived from which watcher fired.

Plus an OS notification, because the user is in Teams or Zoom and not looking at Diariz -
which is precisely the case this feature exists for. The renderer raises it with the Web
Notification API: no CSP on the SPA, no `setPermissionRequestHandler` in `apps/desktop`
(so Electron grants by default), and `setAppUserModelId` is already set on win32, which is
what Windows requires. Permission is requested lazily at the first prompt, never on load,
and a denial degrades silently to the in-app bar alone.

### If nobody answers

The recording keeps going. That is the requirement, and the silence watcher is the floor:
when the room empties, the existing silence rule ends the take and toasts.

**The one hole.** That floor only exists if the user's silence setting is above zero. With
silence off, an unanswered prompt would record until the browser stops it. So when
`silenceSeconds <= 0`, the prompt carries a visible countdown of `afterMinutes` and ends
the take when it expires. With silence on - the default - there is no countdown and no
deadline, exactly as asked.

---

## Testing

The repo is TDD: failing test first, watch it fail, then the minimal code.

**Pure logic** (`calendarRecording.ts`) is where the item 5 rules live, matching how that
file already isolates decisions from timers and Web Audio. `shouldPromptExtend` and
`extendedStopAt` get direct unit tests: sound heard recently, sound heard long ago, no
sound ever, threshold boundaries, and a non-positive `afterMinutes` falling back to the
default the way `resolveCalendarStopAt` does.

**Server**: `ParseEvent` reading `recurringEventId` (present and absent); `.ics` recurrence
flag and uid; the Outlook `#` split, including the `dedupeUids` case where a
**non**-recurring event carries a `#` and must **not** be split; the new endpoint's
ownership scoping, exclusion of the current event, and empty-list-not-404 behaviour.

**Migration backfill** needs the integration harness, not the in-memory provider: it is raw
SQL against real Postgres (`split_part`, a join, string matching). Assert each of the three
id shapes backfills, and that an unrecognisable id stays null.

**Web**: the toast reaching the real provider through the real `WorkspaceLayout` tree (see
the warning under item 1); the attendees disclosure toggling `aria-expanded` and revealing
the list; the day heading rendering "events"; `SeriesRecordings` rendering a list and an
empty state.

**What jsdom cannot prove.** The prompt bar's placement and fit are geometric. jsdom
computes no layout, so a class-presence assertion is not evidence the bar fits beside the
recorder at a narrow content column. Verify that in the running app with
`getBoundingClientRect`, and record the measured widths in the test's comment.

## Release checklist

Per CLAUDE.md, one PR, one release. This is a functional enhancement: **minor +1, build
reset to 0**.

- `version.json` and its four mirrors (`apps/web`, `apps/desktop`,
  `Diariz.Api.csproj`, `integrations/n8n-nodes-diariz`).
- `RELEASES[0]` in `apps/web/src/lib/releases.ts`, matching `version.json`.
- `CAPABILITIES` row + README Features row + `docs/features.md` bullet, in lockstep - the
  scope grows by recurrence awareness and the extend prompt.
- `docs/Data_Schema.md` - the new `RecordingCalendarLinks.SeriesId` column and the
  migration-history row.
- `docs/Overall_Synopsis_of_Platform.md` - the new endpoint and the extend flow.
- Help content: `outlook-calendar.md` and the recording help article describe what happens
  when a recording ends by itself, which item 5 changes. Update the behaviour a user relies
  on, not merely the feature inventory.
- **No em or en dashes** in any user-facing string, i18n catalogue, or release note.
- All four locales for every new key.

## Out of scope

- Any change to `apps/desktop`. If one becomes necessary, stop and re-scope: it turns a
  server redeploy into a tagged desktop release.
- Backfilling Outlook series ids for occurrences that have aged out of the rolling window.
- A user setting for the extend prompt. It reuses `afterMinutes`; a second knob can come
  later if the reused one proves wrong.
- Editing calendars. Every calendar path in Diariz is read-only and stays so.
