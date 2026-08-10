# Calendar Improvements Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Tell the user when a recording ends by itself, show which calendar events recur (and what was recorded of the series before), collapse the attendee list, and let an overrunning meeting be extended rather than cut off.

**Architecture:** The shared `CalendarEvent` projection gains `Recurring`/`SeriesId`, populated from each of the three sources, which already carry the information. The series key is copied onto `RecordingCalendarLink` when a link forms and backfilled by the migration, so sibling recordings are found by a durable stored key rather than a join to Outlook's rolling-window mirror. In the recorder, the calendar stop target separates from the user's own auto-stop so the two can behave differently, and every automatic end reports a reason that drives a toast.

**Tech Stack:** ASP.NET Core (.NET 10) + EF Core/Npgsql, React 19 + TypeScript + Vite + Tailwind v4, vitest + @testing-library/react, xUnit + Testcontainers.

**Spec:** `docs/superpowers/specs/2026-08-10-calendar-improvements-design.md`

## Global Constraints

- **No em or en dashes** (`—`, `–`) in any user-facing string: UI text, i18n catalogues (`apps/web/src/locales/**`, `src/Diariz.Api/locales/**`), release notes, help content. Use a plain hyphen `-`. Code and internal docs are unaffected.
- **Every new i18n key must be added to all four locales**: `en`, `de`, `es`, `fr` under `apps/web/src/locales/<locale>/workspace.json`.
- **TDD is mandatory.** Write the failing test, run it, watch it fail for the stated reason, then write the minimal code. No production code without a preceding failing test.
- **Test output must be pristine** - a passing run has no errors or warnings.
- **Never `git add -A`.** Stage explicit paths. The repo accumulates agent scratch files that have been swept into a commit before.
- **Never commit or push to `main`.** Work happens on `feat/calendar-improvements`, which already exists and already holds the spec commit.
- **Postgres-only model configuration** goes behind `Database.IsNpgsql()`. Nothing in this plan needs it, but do not introduce any.
- **Npgsql rejects a non-zero-offset `DateTimeOffset`** for a `timestamptz` column. Call `.ToUniversalTime()` before storing one.
- **Build `Diariz.slnx`, not just the unit test project**, before considering a server task done: a unit-only run does not compile the integration project, and controller constructor changes have broken it before.
- **`dotnet test --filter "Name=X"` does not work in this repo** despite what CLAUDE.md says. Use `--filter "FullyQualifiedName~X"`.
- **Do not touch anything the Electron build ships**: `apps/desktop/src/**`, `apps/desktop/build/**`, `apps/desktop/electron-builder.config.js`, or desktop dependencies. Changing any of those converts this from a server redeploy into a tagged desktop release. If one appears necessary, stop and escalate.
  - **One deliberate exception, Task 2 only:** a comment correction in `apps/desktop/native/Diariz.OutlookReader/Program.cs`. That is the standalone native COM reader's C# source, compiled separately and not part of the Electron bundle, so editing a comment in it ships nothing. No other file under `apps/desktop/` may change.
- **`apps/desktop/package.json` is bumped in Task 9** as a version mirror only (`versionMirrors.test.ts` asserts it). That is not a desktop change and cuts no release - desktop version numbers are allowed to skip.

---

## File Structure

**Server - calendar projection (Task 2)**
- `src/Diariz.Api/Services/GoogleCalendarClient.cs` - the shared `CalendarEvent` record gains two fields; `ParseEvent` reads `recurringEventId`.
- `src/Diariz.Api/Services/IcsCalendar.cs` - uid resolution lifts out of `MakeId` so the id and the series key derive from one expression.
- `src/Diariz.Api/Services/OutlookCalendarStore.cs` - `Project` maps `IsRecurring` and splits `Uid` at the `#`.
- `src/Diariz.Domain/Entities/OutlookCalendarEvent.cs`, `apps/desktop/native/Diariz.OutlookReader/Program.cs` - **comment corrections only** (see the note in Task 2; `Program.cs` is native reader source, not the Electron shell, and no desktop build is produced).

**Server - link storage (Task 3)**
- `src/Diariz.Domain/Entities/RecordingCalendarLink.cs` - nullable `SeriesId`.
- `src/Diariz.Domain/DiarizDbContext.cs` - its length.
- `src/Diariz.Domain/Migrations/*_AddCalendarSeriesId.cs` - the column plus the backfill.
- `src/Diariz.Api/Controllers/RecordingsController.cs` - one assignment in `LinkCalendar`.

**Server - sibling lookup (Task 4)**
- `src/Diariz.Api/Contracts/ApiDtos.cs` - `SeriesRecordingDto`.
- `src/Diariz.Api/Controllers/CalendarController.cs` - the endpoint; gains a `DiarizDbContext`.

**Web (Tasks 1, 5, 6, 7, 8)**
- `apps/web/src/components/nav/CalendarTab.tsx` - the day-heading count.
- `apps/web/src/components/CalendarEventDetails.tsx` - recurrence badge, attendees disclosure.
- `apps/web/src/components/SeriesRecordings.tsx` - **new**; owns its own query, so `CalendarEventDetails` stays presentational.
- `apps/web/src/pages/CalendarEventDetail.tsx`, `apps/web/src/pages/RecordingDetail.tsx` - mount `SeriesRecordings`.
- `apps/web/src/lib/api.ts`, `apps/web/src/lib/types.ts` - the client method and types.
- `apps/web/src/lib/calendarRecording.ts` - two new pure rules.
- `apps/web/src/lib/silenceWatcher.ts` - expose the live state.
- `apps/web/src/components/Recorder.tsx` - stop reasons, the split stop targets, the extend prompt.
- `apps/web/src/locales/{en,de,es,fr}/workspace.json` - all new keys.

---

### Task 1: The day heading counts events, not meetings

The Calendar tab's day heading reads "6 meetings". Those rows are calendar events - holidays, birthdays and out-of-office days among them - so "events" is what they are.

**Files:**
- Modify: `apps/web/src/components/nav/CalendarTab.tsx:186-191`
- Modify: `apps/web/src/locales/en/workspace.json:59-60`, and the same two lines in `de`, `es`, `fr`
- Test: `apps/web/src/components/nav/CalendarTab.test.tsx`

**Interfaces:**
- Consumes: nothing.
- Produces: nothing later tasks depend on.

- [ ] **Step 1: Write the failing test**

Append to the main `describe` block in `apps/web/src/components/nav/CalendarTab.test.tsx`. `renderTab()` already exists in that file and renders one recording (`rec`) on today with no calendar events, so the heading should show only the recording count and never the word "meeting":

```tsx
  // The day heading counts calendar *events* - holidays, birthdays and out-of-office days are all in here,
  // and none of them is a meeting.
  it("counts the day's items as events, not meetings", async () => {
    renderTab();
    expect(await screen.findByText("Today call")).toBeTruthy();
    expect(screen.getByText("1 recording")).toBeTruthy();
    expect(screen.queryByText(/meeting/i)).toBeNull();
  });
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `cd apps/web && npx vitest run src/components/nav/CalendarTab.test.tsx`

Expected: this new test PASSES already (no events are rendered, so no "meetings" string appears). That makes it a test that cannot fail, which is not acceptable. **Delete it** and write the one below instead, which renders an actual event.

```tsx
  // The day heading counts calendar *events* - holidays, birthdays and out-of-office days are all in here,
  // and none of them is a meeting.
  it("counts the day's items as events, not meetings", async () => {
    const today = new Date();
    const at = (h: number) => new Date(today.getFullYear(), today.getMonth(), today.getDate(), h).toISOString();
    vi.mocked(api.getCalendarEvents).mockResolvedValue([
      { id: "ev1", summary: "Standup", start: at(9), end: at(10), htmlLink: null },
      { id: "ev2", summary: "Review", start: at(11), end: at(12), htmlLink: null },
    ]);

    renderTab();

    expect(await screen.findByText("2 events")).toBeTruthy();
    expect(screen.queryByText(/meeting/i)).toBeNull();
  });
```

Run it again. Expected: FAIL with the heading rendering `2 meetings`, so `findByText("2 events")` times out.

> Check the top of `CalendarTab.test.tsx` for how `api` is mocked and how `vi.mocked(api.getCalendarEvents)` is already being set in other tests, and match that style. If the file resets mocks in `beforeEach`, set the resolved value inside the test as above.

- [ ] **Step 3: Rename the key in all four catalogues**

In `apps/web/src/locales/en/workspace.json` replace lines 59-60:

```json
  "calDayEventCount_one": "{{count}} event",
  "calDayEventCount_other": "{{count}} events",
```

`de/workspace.json`:

```json
  "calDayEventCount_one": "{{count}} Termin",
  "calDayEventCount_other": "{{count}} Termine",
```

`es/workspace.json`:

```json
  "calDayEventCount_one": "{{count}} evento",
  "calDayEventCount_other": "{{count}} eventos",
```

`fr/workspace.json`:

```json
  "calDayEventCount_one": "{{count}} événement",
  "calDayEventCount_other": "{{count}} événements",
```

Leave `calDayRecordingCount_*` untouched in every file. Keep each file's existing key ordering - these lines sit where `calDayMeetingCount_*` sat.

- [ ] **Step 4: Update the component**

In `apps/web/src/components/nav/CalendarTab.tsx`, replace lines 186-191:

```tsx
  const recordings = items.filter((i) => i.type === "recording").length;
  const events = items.length - recordings;
  const counts = [
    events > 0 ? t("calDayEventCount", { count: events }) : null,
    recordings > 0 ? t("calDayRecordingCount", { count: recordings }) : null,
  ].filter(Boolean).join(" · ");
```

- [ ] **Step 5: Run the tests**

Run: `cd apps/web && npx vitest run src/components/nav/CalendarTab.test.tsx`
Expected: PASS.

Then confirm the old key is gone everywhere:

Run: `cd apps/web && grep -rn "calDayMeetingCount" src/ ; echo "exit=$?"`
Expected: no matches (`exit=1`).

- [ ] **Step 6: Commit**

```bash
git add apps/web/src/components/nav/CalendarTab.tsx apps/web/src/components/nav/CalendarTab.test.tsx apps/web/src/locales/en/workspace.json apps/web/src/locales/de/workspace.json apps/web/src/locales/es/workspace.json apps/web/src/locales/fr/workspace.json
git commit -m "fix(calendar): the day heading counts events, not meetings"
```

---

### Task 2: Carry recurrence through the calendar projection

`CalendarEvent` is the shared projection for Google, `.ics` and Outlook alike. All three already know whether an event recurs and what its series key is; none of them says so.

**Files:**
- Modify: `src/Diariz.Api/Services/GoogleCalendarClient.cs:22-26` (the record), `:257-279` (`ParseEvent`)
- Modify: `src/Diariz.Api/Services/IcsCalendar.cs:41-95`
- Modify: `src/Diariz.Api/Services/OutlookCalendarStore.cs:76-101`
- Modify: `src/Diariz.Domain/Entities/OutlookCalendarEvent.cs:25-30` (comment only)
- Modify: `apps/desktop/native/Diariz.OutlookReader/Program.cs:225-228` (comment only)
- Test: `tests/Diariz.Api.Tests/GoogleCalendarClientTests.cs`, `tests/Diariz.Api.Tests/IcsCalendarTests.cs`, `tests/Diariz.Api.Tests/OutlookCalendarStoreTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `CalendarEvent` with two extra positional-optional fields, `bool Recurring = false` and `string? SeriesId = null`. Task 3 reads `ev.SeriesId`; Task 4 reads both; Task 5 reads `recurring` over the wire.

**Background the implementer needs.** `OutlookCalendarEvent.Uid` is documented as a per-occurrence id, which would mean Outlook has no series key at all. That documentation is wrong. `apps/desktop/src/outlookSync.js:84` stores a recurring occurrence's uid as `` `${raw.uid}#${raw.start}` `` because Outlook returned **one `GlobalAppointmentID` per series** ("87 occurrences came back sharing 24 ids"). So the series key is `Uid` up to the first `#`. Only split when `IsRecurring` is true: `dedupeUids` (outlookSync.js:112) appends the same suffix to a *non*-recurring event when two collide, so the separator alone does not mean "recurring".

- [ ] **Step 1: Write the failing tests**

In `tests/Diariz.Api.Tests/GoogleCalendarClientTests.cs`, add:

```csharp
    /// <summary>Google expands a series into instances (singleEvents=true) and stamps each one with the
    /// master's id in <c>recurringEventId</c>. That is the series key, and it was being dropped.</summary>
    [Fact]
    public void ParseEvent_ReadsRecurringEventIdAsTheSeriesKey()
    {
        var json = """
        {
          "id": "abc_20260810T090000Z",
          "recurringEventId": "abc",
          "summary": "Weekly standup",
          "start": { "dateTime": "2026-08-10T09:00:00Z" },
          "end":   { "dateTime": "2026-08-10T09:30:00Z" }
        }
        """;
        var ev = GoogleCalendarClient.ParseEvent(JsonDocument.Parse(json).RootElement);

        Assert.NotNull(ev);
        Assert.True(ev!.Recurring);
        Assert.Equal("abc", ev.SeriesId);
    }

    /// <summary>A one-off carries no <c>recurringEventId</c>, and must not be reported as a series.</summary>
    [Fact]
    public void ParseEvent_LeavesAOneOffNonRecurring()
    {
        var json = """
        {
          "id": "xyz",
          "summary": "Coffee",
          "start": { "dateTime": "2026-08-10T09:00:00Z" },
          "end":   { "dateTime": "2026-08-10T09:30:00Z" }
        }
        """;
        var ev = GoogleCalendarClient.ParseEvent(JsonDocument.Parse(json).RootElement);

        Assert.NotNull(ev);
        Assert.False(ev!.Recurring);
        Assert.Null(ev.SeriesId);
    }
```

> A C# raw string literal (`"""`) carries the source file's line endings, so never byte-compare one against generated output. These only parse JSON, so it does not bite here - but do not introduce a snapshot comparison against one.

In `tests/Diariz.Api.Tests/IcsCalendarTests.cs`, add:

```csharp
    /// <summary>A feed's recurring series: every expanded instance reports the series, keyed by the raw UID -
    /// which is exactly the prefix of the instance id, so the two cannot disagree.</summary>
    [Fact]
    public void ParseEvents_ReportsTheSeriesForARecurringEvent()
    {
        var ics = """
        BEGIN:VCALENDAR
        VERSION:2.0
        BEGIN:VEVENT
        UID:standup@x.test
        DTSTART:20260810T090000Z
        DTEND:20260810T093000Z
        RRULE:FREQ=WEEKLY;COUNT=3
        SUMMARY:Standup
        END:VEVENT
        END:VCALENDAR
        """;
        var events = IcsCalendar.ParseEvents(
            ics,
            DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-09-01T00:00:00Z"),
            "src1", "Feed", "#123456");

        Assert.NotEmpty(events);
        Assert.All(events, e =>
        {
            Assert.True(e.Recurring);
            Assert.Equal("standup@x.test", e.SeriesId);
            Assert.StartsWith("standup@x.test_", e.Id);
        });
    }

    /// <summary>A one-off in the same feed is not a series, and its id stays the bare UID.</summary>
    [Fact]
    public void ParseEvents_LeavesAOneOffNonRecurring()
    {
        var ics = """
        BEGIN:VCALENDAR
        VERSION:2.0
        BEGIN:VEVENT
        UID:oneoff@x.test
        DTSTART:20260810T140000Z
        DTEND:20260810T150000Z
        SUMMARY:Review
        END:VEVENT
        END:VCALENDAR
        """;
        var ev = Assert.Single(IcsCalendar.ParseEvents(
            ics,
            DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-09-01T00:00:00Z"),
            "src1", "Feed", null));

        Assert.False(ev.Recurring);
        Assert.Null(ev.SeriesId);
        Assert.Equal("oneoff@x.test", ev.Id);
    }
```

In `tests/Diariz.Api.Tests/OutlookCalendarStoreTests.cs`, add the two below. Match the file's existing helper for seeding a source + event row rather than inventing one - read the top of the file first and reuse whatever it already has for building an `OutlookCalendarEvent` and a `OutlookCalendarStore` over a `TestDb.Create()` context.

```csharp
    /// <summary>Outlook returns one GlobalAppointmentID for a whole series, so the desktop qualifies a
    /// recurring occurrence's uid as `{series}#{start}` (outlookSync.js). The part before the '#' is the
    /// series key - which is why Outlook needs no new field to support series history.</summary>
    [Fact]
    public async Task GetEvent_ReportsTheSeriesForARecurringOccurrence()
    {
        // ...seed a source and one event row with IsRecurring = true and
        //    Uid = "040000008200E00074C5B7101A82E008#2026-08-10T09:00:00Z"
        var ev = await store.GetEventAsync(userId, OutlookEventId.EventKey(rowId));

        Assert.NotNull(ev);
        Assert.True(ev!.Recurring);
        Assert.Equal("040000008200E00074C5B7101A82E008", ev.SeriesId);
    }

    /// <summary>dedupeUids appends the same '#{start}' suffix to a NON-recurring event when two share a uid,
    /// so the separator alone must never be read as "this recurs" - or two unrelated one-offs that happened
    /// to collide would be presented to the user as the same recurring meeting.</summary>
    [Fact]
    public async Task GetEvent_DoesNotSplitANonRecurringUidThatContainsAHash()
    {
        // ...seed one event row with IsRecurring = false and Uid = "collided-uid#2026-08-10T09:00:00Z"
        var ev = await store.GetEventAsync(userId, OutlookEventId.EventKey(rowId));

        Assert.NotNull(ev);
        Assert.False(ev!.Recurring);
        Assert.Null(ev.SeriesId);
    }
```

- [ ] **Step 2: Run the tests and watch them fail**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~GoogleCalendarClientTests|FullyQualifiedName~IcsCalendarTests|FullyQualifiedName~OutlookCalendarStoreTests"`

Expected: compile error - `CalendarEvent` has no member `Recurring` or `SeriesId`.

- [ ] **Step 3: Add the fields to the shared record**

`src/Diariz.Api/Services/GoogleCalendarClient.cs:22-26`. Append to the existing XML doc: `<see cref="Recurring"/>/<see cref="SeriesId"/> mark an occurrence of a repeating series and identify which series, keyed differently per source (Google's master id, an <c>.ics</c> UID, an Outlook Global Object ID) but never compared across them.`

```csharp
public record CalendarEvent(
    string Id, string? Summary, DateTimeOffset Start, DateTimeOffset End, string? HtmlLink,
    string? Description = null, string? Location = null,
    CalendarAttendee? Organizer = null, IReadOnlyList<CalendarAttendee>? Attendees = null,
    string? CalendarId = null, string? CalendarName = null, string? Color = null, bool AllDay = false,
    bool Recurring = false, string? SeriesId = null);
```

- [ ] **Step 4: Populate it from Google**

In `ParseEvent`, after the `location` line (`:266`):

```csharp
        // Instances are expanded server-side (singleEvents=true), and Google stamps each one with the
        // master's id. Its presence IS the "this recurs" signal - there is no separate flag.
        var recurringEventId = item.TryGetProperty("recurringEventId", out var re) ? re.GetString() : null;
```

and replace the return at `:278`:

```csharp
        return new CalendarEvent(id, summary, start, end, htmlLink, description, location, organizer, attendees,
            AllDay: allDay, Recurring: recurringEventId is not null, SeriesId: recurringEventId);
```

- [ ] **Step 5: Populate it from `.ics`**

In `src/Diariz.Api/Services/IcsCalendar.cs`, replace `MakeId` (`:86-95`) with two functions, so the instance id and the series key come from one uid rather than two that must agree:

```csharp
    /// <summary>The event's UID, or a start-derived stand-in for a feed that omits one. Shared by the
    /// instance id and the series key so the two can never disagree about what the series is.</summary>
    private static string ResolveUid(IcalEvent source, DateTimeOffset startAt) =>
        string.IsNullOrWhiteSpace(source.Uid) ? $"noid-{startAt.UtcDateTime:yyyyMMddTHHmmssZ}" : source.Uid;

    /// <summary>A stable per-instance id: the raw UID for a one-off, UID + occurrence timestamp for a recurring
    /// series (so each instance is addressable), mirroring Google's expanded-instance ids.</summary>
    private static string MakeId(string uid, bool recurring, DateTimeOffset startAt) =>
        recurring ? $"{uid}_{startAt.UtcDateTime:yyyyMMddTHHmmssZ}" : uid;
```

Inside the `foreach` in `ParseEvents`, after `var allDay = ...` (`:52`):

```csharp
                var recurring = source.RecurrenceRule is not null;
                var uid = ResolveUid(source, startAt);
```

and change the `new CalendarEvent(...)` at `:54-67` so `Id:` becomes `MakeId(uid, recurring, startAt)` and two arguments are appended after `AllDay: allDay`:

```csharp
                    AllDay: allDay,
                    Recurring: recurring,
                    SeriesId: recurring ? uid : null));
```

- [ ] **Step 6: Populate it from Outlook**

In `src/Diariz.Api/Services/OutlookCalendarStore.cs`, add above `Project`:

```csharp
    /// <summary>The series key for a recurring occurrence: the desktop stores a recurring uid as
    /// <c>{seriesId}#{occurrenceStart}</c> (outlookSync.js), because Outlook returns one
    /// <c>GlobalAppointmentID</c> for the whole series rather than one per instance. Only ever called for a
    /// recurring row: <c>dedupeUids</c> gives a colliding <b>non</b>-recurring event the same suffix, so the
    /// separator alone does not mean "this repeats".</summary>
    private static string SeriesUid(string uid)
    {
        var hash = uid.IndexOf('#');
        return hash < 0 ? uid : uid[..hash];
    }
```

In `Project`, before the `return`:

```csharp
        var recurring = e.IsRecurring;
```

and append two arguments after `e.AllDay` in the `new CalendarEvent(...)` call:

```csharp
            e.AllDay,
            recurring,
            recurring ? SeriesUid(e.Uid) : null);
```

- [ ] **Step 7: Correct the two stale comments**

`src/Diariz.Domain/Entities/OutlookCalendarEvent.cs:25-30` currently claims the id is "distinct per instance of a recurring series (Outlook stamps the occurrence date into it)". Replace that clause:

```csharp
    /// <summary>Outlook's <c>GlobalAppointmentID</c> - the Global Object ID, the same value EWS returns as
    /// <c>calendar:UID</c>. Stable across moves between folders and stores, which is why it beats
    /// <c>EntryID</c> (unstable).
    /// <para><b>It is per-series, not per-occurrence.</b> Outlook was expected to stamp the occurrence date
    /// into it; against a real calendar it does not (87 occurrences returned 24 ids). So the desktop qualifies
    /// a recurring occurrence as <c>{globalId}#{occurrenceStart}</c> before pushing - see
    /// <c>apps/desktop/src/outlookSync.js</c>. The part before the <c>#</c> is therefore the <b>series</b> key,
    /// which is what <c>OutlookCalendarStore</c> projects as <c>CalendarEvent.SeriesId</c>. Falls back to
    /// <c>entry:{sha1(EntryID)}</c> for local appointments that carry no Global Object ID.</para></summary>
```

Apply the same correction to the parallel comment at `apps/desktop/native/Diariz.OutlookReader/Program.cs:225-228` (which asserts the same wrong thing). This is the native COM reader's source, **not** the Electron shell - no desktop build is produced by editing a comment in it.

- [ ] **Step 8: Run the tests**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~GoogleCalendarClientTests|FullyQualifiedName~IcsCalendarTests|FullyQualifiedName~OutlookCalendarStoreTests"`
Expected: PASS.

Then the whole solution, because the record change ripples:

Run: `dotnet build Diariz.slnx`
Expected: no errors, no warnings.

- [ ] **Step 9: Commit**

```bash
git add src/Diariz.Api/Services/GoogleCalendarClient.cs src/Diariz.Api/Services/IcsCalendar.cs src/Diariz.Api/Services/OutlookCalendarStore.cs src/Diariz.Domain/Entities/OutlookCalendarEvent.cs apps/desktop/native/Diariz.OutlookReader/Program.cs tests/Diariz.Api.Tests/GoogleCalendarClientTests.cs tests/Diariz.Api.Tests/IcsCalendarTests.cs tests/Diariz.Api.Tests/OutlookCalendarStoreTests.cs
git commit -m "feat(calendar): carry recurrence and the series key through the event projection"
```

---

### Task 3: Store the series key on the recording link

To list the other recordings of a series we need a key on the link itself. It has to be **stored**, not derived on read: Outlook's mirror is a rolling window that sweeps old occurrences, so a join to it would silently lose exactly the past meetings this feature exists to show.

**Files:**
- Modify: `src/Diariz.Domain/Entities/RecordingCalendarLink.cs`
- Modify: `src/Diariz.Domain/DiarizDbContext.cs:200-208`
- Modify: `src/Diariz.Api/Controllers/RecordingsController.cs:854-872`
- Create: `src/Diariz.Domain/Migrations/<timestamp>_AddCalendarSeriesId.cs` (generated, then hand-edited)
- Test: `tests/Diariz.Api.IntegrationTests/CalendarSeriesMigrationTests.cs` (new)

**Interfaces:**
- Consumes: `CalendarEvent.SeriesId` (Task 2).
- Produces: `RecordingCalendarLink.SeriesId` (`string?`, max length 1024), populated on every new link. Task 4 queries on it.

- [ ] **Step 1: Write the failing migration test**

Create `tests/Diariz.Api.IntegrationTests/CalendarSeriesMigrationTests.cs`. Model it on `MeetingTypeConversionMigrationTests.cs`, which already does exactly this: create a scratch database on the shared container, migrate to the migration *before* the one under test, insert old-shape rows, then roll forward. Read that file first and reuse its `ScratchAtOldSchemaAsync` / `Context` / `ExecAsync` / `ScalarAsync` helpers verbatim, changing only the `Before`/`Target` constants.

```csharp
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;

namespace Diariz.Api.IntegrationTests;

/// <summary>The series-id backfill. A fresh migrated database has no links at all, so the rest of the suite
/// runs this SQL against zero rows and proves nothing. These build a scratch database at the previous schema,
/// insert links of each id shape a live instance actually holds, and roll forward.</summary>
[Collection(IntegrationCollection.Name)]
public class CalendarSeriesMigrationTests(ContainersFixture fx)
{
    private const string Before = "<the migration immediately before AddCalendarSeriesId>";
    private const string Target = "<the AddCalendarSeriesId migration id>";

    [Fact]
    public async Task Backfill_DerivesTheSeriesFromEachIdShape()
    {
        var cs = await ScratchAtOldSchemaAsync();

        // A Google recurring instance: {masterId}_{stamp}.
        // An .ics recurring instance: our own MakeId, same shape, and the uid may itself contain '_'.
        // A one-off: no stamp suffix, so no series.
        // An Outlook link: resolved by joining the mirror on 'outlook:' || Id.
        await ExecAsync(cs, """
            INSERT INTO "AspNetUsers" ("Id","UserName","Email","EmailConfirmed","PhoneNumberConfirmed","TwoFactorEnabled","LockoutEnabled","AccessFailedCount")
            VALUES ('11111111-1111-1111-1111-111111111111','u@x.test','u@x.test',true,false,false,false,0);
            """);
        // ...insert four Recordings and their RecordingCalendarLinks with EventIds:
        //   'abc_20260810T090000Z', 'my_uid@x.test_20260810T090000Z', 'plain-one-off',
        //   'outlook:{the mirror row's Id}'
        // ...and one OutlookCalendarEvents row with IsRecurring = true and
        //   Uid = 'GLOBALSERIES#2026-08-10T09:00:00Z'

        await using (var db = Context(cs))
            await db.Database.GetService<IMigrator>().MigrateAsync(Target);

        Assert.Equal("abc", await ScalarAsync<string>(cs,
            """SELECT "SeriesId" FROM "RecordingCalendarLinks" WHERE "EventId" = 'abc_20260810T090000Z';"""));
        Assert.Equal("my_uid@x.test", await ScalarAsync<string>(cs,
            """SELECT "SeriesId" FROM "RecordingCalendarLinks" WHERE "EventId" = 'my_uid@x.test_20260810T090000Z';"""));
        Assert.Null(await ScalarAsync<string>(cs,
            """SELECT "SeriesId" FROM "RecordingCalendarLinks" WHERE "EventId" = 'plain-one-off';"""));
        Assert.Equal("GLOBALSERIES", await ScalarAsync<string>(cs,
            """SELECT "SeriesId" FROM "RecordingCalendarLinks" WHERE "EventId" LIKE 'outlook:%';"""));
    }
}
```

Fill in the insert statements against the real column lists - check `DiarizDbContextModelSnapshot.cs` for `Recordings`, `RecordingCalendarLinks` and `OutlookCalendarEvents` required columns, and supply every NOT NULL one. `Before` and `Target` are filled in at Step 4 once the migration exists.

- [ ] **Step 2: Add the column**

`src/Diariz.Domain/Entities/RecordingCalendarLink.cs`, after `CalendarId`:

```csharp
    /// <summary>The recurring series this occurrence belongs to, or null for a one-off (and for links made
    /// before this column existed whose id shape could not be read). Keyed per source - a Google master id, an
    /// <c>.ics</c> UID, an Outlook Global Object ID - so it is only ever compared with another link's, never
    /// interpreted.
    /// <para>Stored rather than derived on read because Outlook's mirror is a rolling window: a join to
    /// <c>OutlookCalendarEvents</c> would find nothing for last month's occurrence, which is precisely the
    /// history the series list exists to show.</para></summary>
    public string? SeriesId { get; set; }
```

`src/Diariz.Domain/DiarizDbContext.cs`, inside the `RecordingCalendarLink` block after the `CalendarId` line:

```csharp
            e.Property(l => l.SeriesId).HasMaxLength(1024);
```

- [ ] **Step 3: Populate it when a link forms**

`src/Diariz.Api/Controllers/RecordingsController.cs`, in `LinkCalendar` after `link.CalendarId = ...` (`:863`):

```csharp
        // The recurring series, when the source knows of one. This is the ONLY production site that builds a
        // link, so both the manual pick and the recorder's auto-link are covered by this one assignment.
        link.SeriesId = ev.SeriesId;
```

- [ ] **Step 4: Generate the migration and add the backfill**

```bash
dotnet ef migrations add AddCalendarSeriesId --project src/Diariz.Domain --startup-project src/Diariz.Api
```

Then hand-edit the generated `Up` so it reads exactly as below (the `AddColumn` call is already generated - append the three `Sql` calls after it), and fill `Before`/`Target` in the test from the migrations directory listing:

```csharp
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SeriesId",
                table: "RecordingCalendarLinks",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            // Backfill, so an established weekly meeting shows its history on day one rather than starting
            // empty. Done here and not in the Seeder: the Seeder runs on every boot, so a data move there
            // re-applies itself and undoes whatever the user did in between.

            // Google expands a series into instances as {masterId}_{yyyyMMddTHHmmssZ}, and our own .ics
            // MakeId produces the same shape. An .ics UID may itself contain '_', so match the timestamp
            // suffix rather than splitting on the first separator.
            migrationBuilder.Sql("""
                UPDATE "RecordingCalendarLinks"
                SET "SeriesId" = regexp_replace("EventId", '_[0-9]{8}T[0-9]{6}Z$', '')
                WHERE "SeriesId" IS NULL
                  AND "EventId" ~ '_[0-9]{8}T[0-9]{6}Z$'
                  AND "EventId" NOT LIKE 'outlook:%';
                """);

            // Outlook's public event id is 'outlook:' + the mirror row's Guid (OutlookEventId.EventKey), and a
            // recurring occurrence's uid is {series}#{start}. Only occurrences still inside the rolling window
            // can be resolved; older links stay null, which reads as "no series I can identify".
            migrationBuilder.Sql("""
                UPDATE "RecordingCalendarLinks" l
                SET "SeriesId" = split_part(e."Uid", '#', 1)
                FROM "OutlookCalendarEvents" e
                WHERE l."SeriesId" IS NULL
                  AND e."IsRecurring"
                  AND l."EventId" = 'outlook:' || e."Id"::text;
                """);
        }
```

The generated `Down` (a single `DropColumn`) is correct as-is.

> Do **not** bump `MaintenanceController.CurrentFormat`. This is an additive nullable column, so an older backup still restores cleanly - the fence is for changes an old dump cannot survive.

- [ ] **Step 5: Run the migration test**

Run: `dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~CalendarSeriesMigrationTests"`
Expected: PASS. (Needs Docker.)

If it fails on the Outlook row, check that `uuid::text` in Postgres yields the same lowercase hyphenated form `OutlookEventId.EventKey` produces with `{eventId:D}` - it does, and a mismatch means the inserted id was formatted differently in the test.

- [ ] **Step 6: Run the full server suites**

Run: `dotnet build Diariz.slnx && dotnet test tests/Diariz.Api.Tests`
Expected: PASS, no warnings.

- [ ] **Step 7: Commit**

```bash
git add src/Diariz.Domain/Entities/RecordingCalendarLink.cs src/Diariz.Domain/DiarizDbContext.cs src/Diariz.Domain/Migrations src/Diariz.Api/Controllers/RecordingsController.cs tests/Diariz.Api.IntegrationTests/CalendarSeriesMigrationTests.cs
git commit -m "feat(calendar): store the recurring series on a recording's calendar link"
```

---

### Task 4: The series-recordings endpoint

**Files:**
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs` (near `CalendarLinkDto`, `:158-165`)
- Modify: `src/Diariz.Api/Controllers/CalendarController.cs`
- Modify: `tests/Diariz.Api.Tests/CalendarControllerTests.cs:51-59` (the `Build` helper gains a db)
- Test: `tests/Diariz.Api.Tests/CalendarControllerTests.cs`

**Interfaces:**
- Consumes: `CalendarEvent.SeriesId` (Task 2), `RecordingCalendarLink.SeriesId` (Task 3).
- Produces: `GET /api/calendar/events/{eventId}/recordings` returning `SeriesRecordingDto[]`:
  `record SeriesRecordingDto(Guid Id, string Title, string? Name, DateTimeOffset StartsAt, DateTimeOffset EndsAt)`.
  Task 6 consumes it.

- [ ] **Step 1: Write the failing tests**

`CalendarControllerTests.Build` currently constructs the controller with three arguments. It gains a fourth. Change the helper first:

```csharp
    private static CalendarController Build(
        FakeCalendarClient cal, Guid userId, FakeIcsClient? ics = null, IGoogleCalendarSelectionStore? selection = null,
        IOutlookCalendarStore? outlook = null, DiarizDbContext? db = null)
    {
        db ??= TestDb.Create();
        return new CalendarController(
            cal,
            new CalendarAggregator(cal, ics ?? new FakeIcsClient(), outlook ?? new NoOutlookDevices(), TestDb.Create()),
            selection ?? new GoogleCalendarSelectionStore(TestDb.Create()),
            db)
        {
            ControllerContext = Http.Context(userId),
        };
    }
```

Then add:

```csharp
    /// <summary>The other recordings of the same recurring meeting: found by the stored series key, scoped to
    /// the caller, and never including the occurrence being viewed.</summary>
    [Fact]
    public async Task SeriesRecordings_ReturnsTheOwnersOtherRecordingsOfTheSameSeries()
    {
        var me = Guid.NewGuid();
        var someoneElse = Guid.NewGuid();
        var db = TestDb.Create();

        var mine = new Recording { Id = Guid.NewGuid(), UserId = me, Title = "Standup 3 Aug" };
        var current = new Recording { Id = Guid.NewGuid(), UserId = me, Title = "Standup 10 Aug" };
        var theirs = new Recording { Id = Guid.NewGuid(), UserId = someoneElse, Title = "Their standup" };
        var otherSeries = new Recording { Id = Guid.NewGuid(), UserId = me, Title = "Retro" };
        db.AddRange(mine, current, theirs, otherSeries);
        db.AddRange(
            Link(mine.Id, "abc_20260803T090000Z", "abc", "2026-08-03T09:00:00Z"),
            Link(current.Id, "abc_20260810T090000Z", "abc", "2026-08-10T09:00:00Z"),
            Link(theirs.Id, "abc_20260727T090000Z", "abc", "2026-07-27T09:00:00Z"),
            Link(otherSeries.Id, "zzz_20260803T140000Z", "zzz", "2026-08-03T14:00:00Z"));
        await db.SaveChangesAsync();

        var cal = new FakeCalendarClient
        {
            Event = new CalendarEvent("abc_20260810T090000Z", "Standup",
                DateTimeOffset.Parse("2026-08-10T09:00:00Z"), DateTimeOffset.Parse("2026-08-10T09:30:00Z"), null,
                Recurring: true, SeriesId: "abc"),
        };

        var result = await Build(cal, me, db: db).SeriesRecordings("abc_20260810T090000Z", default);

        var rows = Assert.IsAssignableFrom<IReadOnlyList<SeriesRecordingDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        var only = Assert.Single(rows);
        Assert.Equal(mine.Id, only.Id);
    }

    /// <summary>A one-off has no series, so the answer is an empty list rather than a 404: the client renders
    /// one shape either way.</summary>
    [Fact]
    public async Task SeriesRecordings_ReturnsEmptyForANonRecurringEvent()
    {
        var cal = new FakeCalendarClient
        {
            Event = new CalendarEvent("plain", "Coffee",
                DateTimeOffset.Parse("2026-08-10T09:00:00Z"), DateTimeOffset.Parse("2026-08-10T09:30:00Z"), null),
        };

        var result = await Build(cal, Guid.NewGuid()).SeriesRecordings("plain", default);

        var rows = Assert.IsAssignableFrom<IReadOnlyList<SeriesRecordingDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Empty(rows);
    }

    /// <summary>An id that resolves to nothing is a 404, matching the single-event endpoint beside it.</summary>
    [Fact]
    public async Task SeriesRecordings_404sWhenTheEventIsGone()
    {
        var result = await Build(new FakeCalendarClient { Event = null }, Guid.NewGuid())
            .SeriesRecordings("missing", default);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    private static RecordingCalendarLink Link(Guid recordingId, string eventId, string seriesId, string startsAt) =>
        new()
        {
            RecordingId = recordingId,
            EventId = eventId,
            CalendarId = "primary",
            SeriesId = seriesId,
            StartsAt = DateTimeOffset.Parse(startsAt),
            EndsAt = DateTimeOffset.Parse(startsAt).AddMinutes(30),
        };
```

Add `using Diariz.Domain.Entities;` to the file if it is not already there.

- [ ] **Step 2: Run the tests and watch them fail**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~CalendarControllerTests"`
Expected: compile error - no `SeriesRecordings` member, no `SeriesRecordingDto`, and the four-argument constructor does not exist.

- [ ] **Step 3: Add the DTO**

`src/Diariz.Api/Contracts/ApiDtos.cs`, after `CalendarLinkDto` (`:160`):

```csharp
/// <summary>One earlier recording of the same recurring meeting. The times are the <b>occurrence's</b>, taken
/// from the link snapshot rather than the recording, so the list reads as a calendar history ("Tue 3 Jun")
/// rather than as upload timestamps.</summary>
public record SeriesRecordingDto(Guid Id, string Title, string? Name, DateTimeOffset StartsAt, DateTimeOffset EndsAt);
```

- [ ] **Step 4: Add the endpoint**

`src/Diariz.Api/Controllers/CalendarController.cs`. Add `using Diariz.Domain;` and `using Microsoft.EntityFrameworkCore;`, give the class a `DiarizDbContext` field, and extend the constructor:

```csharp
    private readonly DiarizDbContext _db;

    public CalendarController(
        IGoogleCalendarClient calendar, ICalendarAggregator calendars, IGoogleCalendarSelectionStore selection,
        DiarizDbContext db)
    {
        // The Google client is still needed directly for the calendar *picker* below, which is Google-specific;
        // everything event-shaped goes through the aggregator.
        _calendar = calendar;
        _calendars = calendars;
        _selection = selection;
        _db = db;
    }
```

Then, after the `Event` action (`:77`):

```csharp
    /// <summary>The caller's other recordings of the same recurring meeting, newest occurrence first.
    /// <para>Matched on the series key stored on the link when it was made, not by re-deriving it from the
    /// calendar: an Outlook occurrence from last month has already been swept out of the mirror's rolling
    /// window, so anything that consulted the calendar would return nothing for exactly the history this
    /// shows.</para></summary>
    [HttpGet("events/{eventId}/recordings")]
    [EndpointSummary("List your other recordings of a recurring meeting")]
    [EndpointDescription(
        "For an event that is part of a **repeating series**, the other recordings you have made of that same " +
        "series - so you can jump straight to what was said at the last one.\n\n" +
        "Newest first, capped at 10, and never including the occurrence you asked about. An event that does " +
        "not repeat, or one whose series you have never recorded before, returns an **empty list** rather " +
        "than an error.\n\n" +
        "Only your own recordings are ever returned. 404 when the event is gone or its calendar is not connected.")]
    public async Task<ActionResult<IReadOnlyList<SeriesRecordingDto>>> SeriesRecordings(
        string eventId, CancellationToken ct)
    {
        var ev = await _calendars.GetEventAsync(UserId, eventId, ct);
        if (ev is null) return NotFound();

        // Held in a local: a captured property access on the record does not translate to SQL.
        var seriesId = ev.SeriesId;
        if (seriesId is null) return Ok(Array.Empty<SeriesRecordingDto>());

        var rows = await _db.RecordingCalendarLinks
            .Where(l => l.SeriesId == seriesId && l.EventId != ev.Id && l.Recording!.UserId == UserId)
            .OrderByDescending(l => l.StartsAt)
            .Take(10)
            .Select(l => new SeriesRecordingDto(
                l.RecordingId, l.Recording!.Title, l.Recording.Name, l.StartsAt, l.EndsAt))
            .ToListAsync(ct);

        return Ok(rows);
    }
```

`ev.Id` inside the `Where` has the same capture problem - hoist it too: `var currentId = ev.Id;` and compare `l.EventId != currentId`.

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~CalendarControllerTests"`
Expected: PASS.

Run: `dotnet build Diariz.slnx`
Expected: no errors. The constructor gained a parameter; if anything else constructs this controller it fails here, and a unit-only run would not have told you.

- [ ] **Step 6: Regenerate the OpenAPI snapshot**

The n8n node ships a snapshot of the API surface, and adding an endpoint changes it. Run the suite that owns it:

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~OpenApi"`

Expected: the **first** run FAILS and rewrites `integrations/n8n-nodes-diariz/nodes/Diariz/generated/openapi.snapshot.json` itself; the second run passes with no further code change. Commit the regenerated file.

- [ ] **Step 7: Commit**

```bash
git add src/Diariz.Api/Contracts/ApiDtos.cs src/Diariz.Api/Controllers/CalendarController.cs tests/Diariz.Api.Tests/CalendarControllerTests.cs integrations/n8n-nodes-diariz/nodes/Diariz/generated/openapi.snapshot.json
git commit -m "feat(calendar): endpoint for other recordings of a recurring meeting"
```

---

### Task 5: Recurrence badge and collapsible attendees

**Files:**
- Modify: `apps/web/src/lib/types.ts:749-769` (the `CalendarEvent` interface)
- Modify: `apps/web/src/components/CalendarEventDetails.tsx:52-104`
- Modify: `apps/web/src/locales/{en,de,es,fr}/workspace.json`
- Test: `apps/web/src/components/CalendarEventDetails.test.tsx`

**Interfaces:**
- Consumes: the API's `recurring` field (Task 2).
- Produces: nothing later tasks depend on.

- [ ] **Step 1: Write the failing tests**

Add to `apps/web/src/components/CalendarEventDetails.test.tsx`. `fullEvent` at the top of that file has two attendees.

```tsx
import { render, screen, fireEvent } from "@testing-library/react";
```

```tsx
  // Attendees is the longest block in the panel and is rarely what you opened the event for, so it starts
  // closed - with the count still on show, so collapsing hides the names, not the fact that there are two.
  it("keeps attendees collapsed until asked, and shows the count either way", () => {
    render(<CalendarEventDetails event={fullEvent} />);

    const toggle = screen.getByRole("button", { name: /Attendees \(2\)/ });
    expect(toggle.getAttribute("aria-expanded")).toBe("false");
    expect(screen.queryByText("Going")).toBeNull();

    fireEvent.click(toggle);

    expect(toggle.getAttribute("aria-expanded")).toBe("true");
    expect(screen.getByText("Going")).toBeTruthy();
    expect(screen.getByText("No response")).toBeTruthy();
  });

  it("marks an event that repeats", () => {
    render(<CalendarEventDetails event={{ ...fullEvent, recurring: true }} />);
    expect(screen.getByText("Repeats")).toBeTruthy();
  });

  it("says nothing about repetition for a one-off", () => {
    render(<CalendarEventDetails event={fullEvent} />);
    expect(screen.queryByText("Repeats")).toBeNull();
  });
```

The existing first test asserts `screen.getByText("Going")` and `screen.getByText("No response")` with the list open by default. It will now fail. Update it to open the disclosure first:

```tsx
    fireEvent.click(screen.getByRole("button", { name: /Attendees \(2\)/ }));
    expect(screen.getByText("Going")).toBeTruthy(); // accepted -> Going
    expect(screen.getByText("No response")).toBeTruthy(); // needsAction
    expect(screen.getByText(/me@x\.test \(you\)/)).toBeTruthy(); // self, falls back to email
```

and move the `getAllByText("The Boss")` assertion after that click, changing its expected length to 2 (organiser row + attendee row) as it already is.

- [ ] **Step 2: Run the tests and watch them fail**

Run: `cd apps/web && npx vitest run src/components/CalendarEventDetails.test.tsx`
Expected: FAIL - no button named "Attendees (2)" (it is a plain `div` today), and no "Repeats" text.

- [ ] **Step 3: Add `recurring` to the web type**

`apps/web/src/lib/types.ts`, inside `CalendarEvent` after `allDay`:

```ts
  /// One occurrence of a repeating series. Reported for Google, `.ics` and mirrored Outlook alike. The series
  /// *key* is deliberately not sent: the browser never needs it, because the sibling lookup is resolved
  /// server-side from the event id (`getSeriesRecordings`).
  recurring?: boolean;
```

- [ ] **Step 4: Add the copy**

`en/workspace.json` (keep alphabetical-ish placement near the other `cal*` keys):

```json
  "calRecurring": "Repeats",
```

`de`: `"calRecurring": "Serie",`
`es`: `"calRecurring": "Se repite",`
`fr`: `"calRecurring": "Récurrent",`

- [ ] **Step 5: Render the badge**

`apps/web/src/components/CalendarEventDetails.tsx`. The calendar-name row at `:52-61` is conditional on `event.calendarName`, and a recurring event need not have one, so give the badge its own row rather than nesting it. Replace that block with:

```tsx
      {(event.calendarName || event.recurring) && (
        <div className="flex items-center gap-2 text-xs text-gray-500 dark:text-gray-400">
          {event.calendarName && (
            <>
              <span
                className="inline-block h-2.5 w-2.5 shrink-0 rounded-full"
                style={{ backgroundColor: event.color ?? "#0B8043" }}
                aria-hidden
              />
              <span className="truncate">{event.calendarName}</span>
            </>
          )}
          {event.recurring && (
            <span className="shrink-0 rounded-full bg-gray-100 px-1.5 py-0.5 text-[10px] font-medium uppercase tracking-wide text-gray-600 dark:bg-gray-800 dark:text-gray-300">
              {t("workspace:calRecurring")}
            </span>
          )}
        </div>
      )}
```

- [ ] **Step 6: Turn attendees into a disclosure**

Add to the imports at the top of the file:

```tsx
import { useState } from "react";
```

and inside the component, above the `return`:

```tsx
  // Collapsed by default: this is the longest block in the panel and rarely the reason the event was opened.
  const [attendeesOpen, setAttendeesOpen] = useState(false);
```

Replace `:83-104` with:

```tsx
      {attendees.length > 0 && (
        <div>
          <button
            type="button"
            onClick={() => setAttendeesOpen((o) => !o)}
            aria-expanded={attendeesOpen}
            aria-controls="calendar-attendees"
            className="flex w-full items-center gap-1 text-xs font-medium uppercase tracking-wide text-gray-500 hover:text-gray-700 dark:text-gray-400 dark:hover:text-gray-200"
          >
            <span aria-hidden className={attendeesOpen ? "rotate-90 transition-transform" : "transition-transform"}>
              ›
            </span>
            {/* The count stays visible closed, so collapsing hides the names, not the fact that there are seven. */}
            {t("workspace:calAttendees")} ({attendees.length})
          </button>
          {attendeesOpen && (
            <ul id="calendar-attendees" className="mt-1 space-y-0.5">
              {attendees.map((a, i) => (
                <li key={a.email ?? i} className="flex items-center justify-between gap-2">
                  <span className="min-w-0 truncate text-gray-800 dark:text-gray-200">
                    {attendeeName(a)}
                    {a.self ? ` (${t("workspace:calYou")})` : ""}
                  </span>
                  {a.responseStatus && (
                    <span className="shrink-0 text-xs text-gray-500 dark:text-gray-400">
                      {RESP_KEY[a.responseStatus] ? t(`workspace:${RESP_KEY[a.responseStatus]}`) : a.responseStatus}
                    </span>
                  )}
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
```

- [ ] **Step 7: Run the tests**

Run: `cd apps/web && npx vitest run src/components/CalendarEventDetails.test.tsx`
Expected: PASS.

Then the whole web suite, because `CalendarEventDetails` is shared by the recording Overview and other tests may assert on an open attendee list:

Run: `cd apps/web && npm test`
Expected: PASS. Fix any test that assumed attendees were expanded by clicking the disclosure first, not by changing the default.

- [ ] **Step 8: Commit**

```bash
git add apps/web/src/lib/types.ts apps/web/src/components/CalendarEventDetails.tsx apps/web/src/components/CalendarEventDetails.test.tsx apps/web/src/locales/en/workspace.json apps/web/src/locales/de/workspace.json apps/web/src/locales/es/workspace.json apps/web/src/locales/fr/workspace.json
git commit -m "feat(calendar): mark repeating events and collapse the attendee list"
```

---

### Task 6: Show earlier recordings of the series

**Files:**
- Create: `apps/web/src/components/SeriesRecordings.tsx`
- Create: `apps/web/src/components/SeriesRecordings.test.tsx`
- Modify: `apps/web/src/lib/api.ts` (after `getCalendarEvent`, `:486`), `apps/web/src/lib/types.ts`
- Modify: `apps/web/src/pages/CalendarEventDetail.tsx:96-104`
- Modify: `apps/web/src/locales/{en,de,es,fr}/workspace.json`

**Interfaces:**
- Consumes: `GET /api/calendar/events/{eventId}/recordings` (Task 4).
- Produces: `<SeriesRecordings eventId={string} />`.

- [ ] **Step 1: Write the failing test**

Create `apps/web/src/components/SeriesRecordings.test.tsx`. Follow the established web pattern: `vi.mock("../lib/api")`, render inside `MemoryRouter` + `QueryClientProvider`. Read `apps/web/src/components/RecordingsPanel.test.tsx` for the exact provider setup this repo uses and mirror it.

```tsx
import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";
import SeriesRecordings from "./SeriesRecordings";
import { api } from "../lib/api";

vi.mock("../lib/api", () => ({ api: { getSeriesRecordings: vi.fn() } }));

function renderIt() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter>
        <SeriesRecordings eventId="abc_20260810T090000Z" />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe("SeriesRecordings", () => {
  beforeEach(() => vi.mocked(api.getSeriesRecordings).mockReset());

  it("lists the earlier recordings, newest first, linking to each", async () => {
    vi.mocked(api.getSeriesRecordings).mockResolvedValue([
      { id: "r1", title: "Standup", name: "Standup 3 Aug", startsAt: "2026-08-03T09:00:00Z", endsAt: "2026-08-03T09:30:00Z" },
      { id: "r2", title: "Standup 27 Jul", name: null, startsAt: "2026-07-27T09:00:00Z", endsAt: "2026-07-27T09:30:00Z" },
    ]);

    renderIt();

    expect(await screen.findByText("Earlier recordings of this meeting")).toBeTruthy();
    const first = screen.getByRole("link", { name: /Standup 3 Aug/ });
    expect(first.getAttribute("href")).toBe("/recordings/r1");
    // name ?? title, exactly as the rest of the app labels a recording.
    expect(screen.getByRole("link", { name: /Standup 27 Jul/ })).toBeTruthy();
  });

  // A one-off, or a series never recorded before, returns []. Rendering a heading over an empty list would
  // put a permanent dead section on every non-recurring event.
  it("renders nothing at all when there is no history", async () => {
    vi.mocked(api.getSeriesRecordings).mockResolvedValue([]);
    const { container } = renderIt();
    await new Promise((r) => setTimeout(r, 0));
    expect(container.textContent).toBe("");
  });
});
```

- [ ] **Step 2: Run the test and watch it fail**

Run: `cd apps/web && npx vitest run src/components/SeriesRecordings.test.tsx`
Expected: FAIL - the module does not exist.

- [ ] **Step 3: Add the type and the client method**

`apps/web/src/lib/types.ts`, near `CalendarLink`:

```ts
/// One earlier recording of the same recurring meeting. The times are the *occurrence's* (from the link
/// snapshot), not the upload's, so the list reads as a calendar history.
export interface SeriesRecording {
  id: string;
  title: string;
  name: string | null;
  startsAt: string; // ISO
  endsAt: string; // ISO
}
```

`apps/web/src/lib/api.ts`, after `getCalendarEvent` (`:486`):

```ts
  /// Your other recordings of the same recurring meeting, newest occurrence first (max 10). Empty for a
  /// one-off or a series you have not recorded before; 404 when the event is gone.
  async getSeriesRecordings(eventId: string): Promise<SeriesRecording[]> {
    const { data } = await http.get<SeriesRecording[]>(
      `/api/calendar/events/${encodeURIComponent(eventId)}/recordings`);
    return data;
  },
```

Add `SeriesRecording` to the type import list at the top of `api.ts`.

- [ ] **Step 4: Add the copy**

`en`: `"calSeriesTitle": "Earlier recordings of this meeting",`
`de`: `"calSeriesTitle": "Frühere Aufnahmen dieses Termins",`
`es`: `"calSeriesTitle": "Grabaciones anteriores de esta reunión",`
`fr`: `"calSeriesTitle": "Enregistrements précédents de cette réunion",`

- [ ] **Step 5: Write the component**

Create `apps/web/src/components/SeriesRecordings.tsx`:

```tsx
import { Link } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api } from "../lib/api";
import { useRoomBasePath } from "../lib/rooms";
import { formatLongDate } from "../lib/format";

/// The other recordings of a recurring meeting, for jumping back to what was said last time.
///
/// Owns its own query rather than taking a list, which is what keeps `CalendarEventDetails` presentational -
/// that component is rendered in two places and neither should have to know how to fetch this.
///
/// Renders nothing when there is no history. A heading above an empty list would put a permanently dead
/// section on every one-off event, which is most of them.
export default function SeriesRecordings({ eventId }: { eventId: string }) {
  const { t, i18n } = useTranslation(["workspace"]);
  // Keep the links inside the current room, exactly as every other recording link in the app must.
  const basePath = useRoomBasePath();

  const { data = [] } = useQuery({
    queryKey: ["series-recordings", eventId],
    queryFn: () => api.getSeriesRecordings(eventId),
    retry: false,
  });

  if (data.length === 0) return null;

  return (
    <div className="mt-4 border-t pt-3 dark:border-gray-700">
      <h3 className="mb-1 text-sm font-medium text-gray-700 dark:text-gray-200">{t("workspace:calSeriesTitle")}</h3>
      <ul className="space-y-0.5">
        {data.map((r) => (
          <li key={r.id}>
            <Link
              to={`${basePath}/recordings/${r.id}`}
              className="flex items-baseline justify-between gap-2 text-sm text-indigo-600 hover:underline dark:text-indigo-400"
            >
              <span className="min-w-0 truncate">{r.name ?? r.title}</span>
              <span className="shrink-0 text-xs text-gray-500 dark:text-gray-400">
                {formatLongDate(r.startsAt, i18n.language)}
              </span>
            </Link>
          </li>
        ))}
      </ul>
    </div>
  );
}
```

> `useRoomBasePath()` is not optional. A recording link built without it silently drops the user into their Personal Room - the defect fixed in PRs #298/#299. The test above renders at `/`, where the prefix is `""`, so it cannot catch a missing one; do not remove it because the test still passes.

- [ ] **Step 6: Mount it on the event page**

`apps/web/src/pages/CalendarEventDetail.tsx`. Import it, then insert between `CalendarEventDetails` and the notes block (`:97`):

```tsx
          <CalendarEventDetails event={event} />
          <SeriesRecordings eventId={event.id} />
```

- [ ] **Step 7: Mount it on the recording Overview too**

`CalendarEventDetails` has a second mount point: `apps/web/src/components/detail/MeetingCard.tsx:78-91`, inside the `linked` branch. That is the more valuable of the two surfaces - you are looking at one standup and want the last one - and it is the same two lines.

Import `SeriesRecordings` and place it immediately after the `CalendarEventDetails` element, still inside the `linked` branch. Use `calendarLink.eventId`, which is always present there (it is what the branch is keyed on), **not** `linkedEvent?.id`, which is null until the live fetch lands:

```tsx
        {linked ? (
          <>
            <CalendarEventDetails
              showTitle
              event={
                linkedEvent ?? {
                  id: calendarLink.eventId,
                  summary: calendarLink.summary,
                  start: calendarLink.start,
                  end: calendarLink.end,
                  htmlLink: calendarLink.htmlLink,
                }
              }
            />
            <SeriesRecordings eventId={calendarLink.eventId} />
          </>
        ) : suggestion ? (
```

`SeriesRecordings` renders `null` when there is no history, so a linked one-off is unchanged.

- [ ] **Step 8: Run the tests**

Run: `cd apps/web && npx vitest run src/components/SeriesRecordings.test.tsx src/pages/CalendarEventDetail.test.tsx src/components/detail/MeetingCard.test.tsx`
Expected: PASS. If any of those mocks `../lib/api` with an explicit object, add `getSeriesRecordings: vi.fn().mockResolvedValue([])` to it - an unmocked call throws and fails the render.

Run: `cd apps/web && npm test`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add apps/web/src/components/SeriesRecordings.tsx apps/web/src/components/SeriesRecordings.test.tsx apps/web/src/components/detail/MeetingCard.tsx apps/web/src/lib/api.ts apps/web/src/lib/types.ts apps/web/src/pages/CalendarEventDetail.tsx apps/web/src/pages/CalendarEventDetail.test.tsx apps/web/src/locales/en/workspace.json apps/web/src/locales/de/workspace.json apps/web/src/locales/es/workspace.json apps/web/src/locales/fr/workspace.json
git commit -m "feat(calendar): list earlier recordings of a recurring meeting"
```

---

### Task 7: Say why a recording ended by itself

Three paths end a recording without the user touching anything, and none of them says so.

**Files:**
- Modify: `apps/web/src/components/Recorder.tsx:476-481` (watcher), `:806` (silence callback), `:869` (`stop`)
- Modify: `apps/web/src/components/WorkspaceLayout.test.tsx`
- Modify: `apps/web/src/locales/{en,de,es,fr}/workspace.json`

**Interfaces:**
- Consumes: `useToast()` from `apps/web/src/lib/toast.tsx`.
- Produces: `function stop(reason?: StopReason)` where `type StopReason = "schedule" | "calendar" | "silence"`. Task 8 calls `stop("calendar")` and adds no new reason.

- [ ] **Step 1: Write the failing test**

`toast.tsx:14` defaults the context to a **silent no-op** rather than throwing, so a component rendered outside `ToastProvider` compiles, passes every mocked test, and shows nothing. That is the shape of the `HubPopoverProvider` defect in PR #500. Prove the provider actually spans the recorder's position in the real tree.

`apps/web/src/components/WorkspaceLayout.test.tsx` already exists and already mocks `./Workspace` with a probe for exactly this purpose. Add a toast probe alongside it:

```tsx
  // toast.tsx's context defaults to a silent no-op, so a consumer outside the provider does not throw and
  // does not fail typecheck - it just never shows anything. The recorder lives inside <Workspace />, so this
  // renders the REAL layout and raises a toast from that position.
  it("puts the toast provider above the workspace, where the recorder lives", async () => {
    render(<WorkspaceLayout />);

    fireEvent.click(screen.getByText("raise-toast"));

    expect(await screen.findByText("recording stopped")).toBeTruthy();
  });
```

with the `./Workspace` mock in that file extended to render the probe button:

```tsx
vi.mock("./Workspace", () => ({
  default: () => {
    const { showToast } = useToast();
    return <button onClick={() => showToast("recording stopped")}>raise-toast</button>;
  },
}));
```

Read the existing file before editing: it already has a `./Workspace` mock for the hub-popover probe, so **extend** that mock's component rather than declaring a second `vi.mock` for the same path.

- [ ] **Step 2: Run the test and watch it fail**

Temporarily move `<ToastProvider>` inside `<Workspace />`'s sibling position (or just below `<HubPopoverProvider>`'s children) in `WorkspaceLayout.tsx` and run:

Run: `cd apps/web && npx vitest run src/components/WorkspaceLayout.test.tsx`
Expected: FAIL - the probe's toast never appears, because the probe is outside the provider.

Then restore `WorkspaceLayout.tsx` exactly as it was and re-run: PASS. This step exists only to prove the test can fail; it must leave `WorkspaceLayout.tsx` unchanged.

- [ ] **Step 3: Add the copy**

`en/workspace.json`:

```json
  "recStoppedSchedule": "Recording stopped - your auto-stop time was reached.",
  "recStoppedCalendar": "Recording stopped - the meeting has ended.",
  "recStoppedSilence": "Recording stopped - the meeting went quiet.",
```

`de/workspace.json`:

```json
  "recStoppedSchedule": "Aufnahme beendet - die eingestellte Stoppzeit wurde erreicht.",
  "recStoppedCalendar": "Aufnahme beendet - der Termin ist vorbei.",
  "recStoppedSilence": "Aufnahme beendet - es war nichts mehr zu hören.",
```

`es/workspace.json`:

```json
  "recStoppedSchedule": "Grabación detenida - se alcanzó la hora de parada automática.",
  "recStoppedCalendar": "Grabación detenida - la reunión ha terminado.",
  "recStoppedSilence": "Grabación detenida - se hizo el silencio.",
```

`fr/workspace.json`:

```json
  "recStoppedSchedule": "Enregistrement arrêté - l'heure d'arrêt automatique est atteinte.",
  "recStoppedCalendar": "Enregistrement arrêté - la réunion est terminée.",
  "recStoppedSilence": "Enregistrement arrêté - plus personne ne parlait.",
```

Note the plain hyphens. No em dashes.

- [ ] **Step 4: Give `stop` a reason**

`apps/web/src/components/Recorder.tsx`. Add near the other module-level types:

```tsx
/// Why a recording ended. Absent means the user pressed Stop, which needs no announcement - they know.
type StopReason = "schedule" | "calendar" | "silence";

/// Kept as a literal map rather than a template key, so every key is greppable in the catalogues.
const STOP_TOAST: Record<StopReason, string> = {
  schedule: "recStoppedSchedule",
  calendar: "recStoppedCalendar",
  silence: "recStoppedSilence",
};
```

Import and take the hook inside the component, beside `useStatus()`:

```tsx
import { useToast } from "../lib/toast";
```
```tsx
  const { showToast } = useToast();
```

Change the signature and add the toast at the end of `stop` (`:869-894`), after `recorderRef.current?.stop()`:

```tsx
  function stop(reason?: StopReason) {
```
```tsx
    recorderRef.current?.stop();
    // An automatic ending is the only one worth announcing: the user did not do it and would otherwise find
    // the recorder idle with no explanation. A replacing start() passes no reason either - that is a handover,
    // and the new recording is its own feedback.
    if (reason) showToast(t(STOP_TOAST[reason]));
  }
```

- [ ] **Step 5: Pass the reason from each automatic path**

The schedule watcher (`:479`):

```tsx
      if (schedule.shouldStop(scheduledStopRef.current, Date.now())) stop("schedule");
```

The silence watcher's callback (`:806`):

```tsx
            () => stop("silence"),
```

Leave every other `stop()` call as it is - the Stop button, and the replacing `start()`.

- [ ] **Step 6: Run the tests**

Run: `cd apps/web && npx vitest run src/components/WorkspaceLayout.test.tsx src/components/Recorder.test.tsx`
Expected: PASS.

Run: `cd apps/web && npm test && npm run build`
Expected: PASS, clean typecheck.

- [ ] **Step 7: Commit**

```bash
git add apps/web/src/components/Recorder.tsx apps/web/src/components/WorkspaceLayout.test.tsx apps/web/src/locales/en/workspace.json apps/web/src/locales/de/workspace.json apps/web/src/locales/es/workspace.json apps/web/src/locales/fr/workspace.json
git commit -m "feat(recorder): say why a recording ended by itself"
```

---

### Task 8: Extend this meeting

A meeting that overruns currently loses its ending. When the calendar's stop time arrives and people are still audibly talking, ask instead of stopping.

**Files:**
- Modify: `apps/web/src/lib/calendarRecording.ts`
- Modify: `apps/web/src/lib/calendarRecording.test.ts`
- Modify: `apps/web/src/lib/silenceWatcher.ts`
- Modify: `apps/web/src/components/Recorder.tsx`
- Modify: `apps/web/src/locales/{en,de,es,fr}/workspace.json`

**Interfaces:**
- Consumes: `stop(reason?: StopReason)` (Task 7), `CalendarRecordingSettings` and `SilenceState` (existing).
- Produces: `shouldPromptExtend(state: SilenceState, recentMs: number): boolean`, `extendedStopAt(nowMs: number, afterMinutes: number): number`, `RECENT_SOUND_MS: number`, and `SilenceWatcher.state(): SilenceState`.

**Design constraints the implementer must respect:**

- The capture bar is **fixed height**. `Recorder.tsx:1123` and `:1300` both record that anything added to its flow grows the bar and pushes the page down, which is why errors go to the status bar and the recovery banners float. The prompt therefore goes in the same floating popover treatment, absolutely positioned inside the recorder's `relative` root.
- The status bar is not an option: `setStatus(text, tone)` takes text only, with nowhere for two buttons.
- Unanswered, the recording **keeps going**. The silence watcher is the floor. Only when the user has turned silence off (`silenceSeconds <= 0`) does the prompt carry a countdown.

- [ ] **Step 1: Write the failing tests for the pure rules**

Append to `apps/web/src/lib/calendarRecording.test.ts`:

```ts
describe("shouldPromptExtend", () => {
  const LOUD = 0.5;
  const QUIET = 0;

  it("asks when someone has spoken recently", () => {
    let s = nextSilenceState(idleSilence(), LOUD, 1000);
    s = nextSilenceState(s, QUIET, 2_000);
    expect(shouldPromptExtend(s, 10_000)).toBe(true);
  });

  it("does not ask once the room has been quiet for a while - there is nobody to answer", () => {
    let s = nextSilenceState(idleSilence(), LOUD, 1000);
    s = nextSilenceState(s, QUIET, 30_000);
    expect(shouldPromptExtend(s, 10_000)).toBe(false);
  });

  it("does not ask when nothing has ever been heard (joined early, meeting never happened)", () => {
    let s = idleSilence();
    for (let i = 0; i < 10; i++) s = nextSilenceState(s, QUIET, 1000);
    expect(shouldPromptExtend(s, 10_000)).toBe(false);
  });

  it("treats the threshold as exclusive, matching shouldStopForSilence's inclusive one", () => {
    let s = nextSilenceState(idleSilence(), LOUD, 1000);
    s = nextSilenceState(s, QUIET, 10_000);
    expect(shouldPromptExtend(s, 10_000)).toBe(false);
  });
});

describe("extendedStopAt", () => {
  const now = Date.parse("2026-08-10T09:00:00Z");

  it("adds the user's own overrun allowance", () => {
    expect(extendedStopAt(now, 5)).toBe(now + 5 * MIN);
  });

  it("falls back to the default rather than re-asking instantly", () => {
    expect(extendedStopAt(now, 0)).toBe(now + 3 * MIN);
    expect(extendedStopAt(now, -9)).toBe(now + 3 * MIN);
  });
});
```

Add `shouldPromptExtend` and `extendedStopAt` to the import list at the top of the file.

- [ ] **Step 2: Run and watch them fail**

Run: `cd apps/web && npx vitest run src/lib/calendarRecording.test.ts`
Expected: FAIL - `shouldPromptExtend is not a function`.

- [ ] **Step 3: Write the rules**

Append to `apps/web/src/lib/calendarRecording.ts`:

```ts
/// How recently sound must have been heard for the meeting to count as still running. Deliberately short: the
/// question is "is anyone talking right now", not "has this meeting been lively", and a natural pause between
/// speakers is a few seconds at most.
export const RECENT_SOUND_MS = 10_000;

/**
 * Whether to ask before ending a calendar-started recording, rather than just ending it.
 *
 * The point is that an overrunning meeting keeps its ending. If the room has already gone quiet there is
 * nobody to ask and nothing left to capture, so the recording simply stops - the same answer as the silence
 * rule, arrived at a different way. `heardSound` carries the same guard it does everywhere else in this file:
 * a take started before anyone speaks has not gone quiet, it has not started.
 */
export function shouldPromptExtend(state: SilenceState, recentMs: number): boolean {
  return state.heardSound && state.silentMs < recentMs;
}

/// The new stop target after the user says to keep going: the same overrun allowance they already configured
/// for "record N minutes past the end", so there is one number to understand rather than two. A non-positive
/// value falls back to the default exactly as `resolveCalendarStopAt` does - asking again instantly would be a
/// far worse answer.
export function extendedStopAt(nowMs: number, afterMinutes: number): number {
  const minutes = afterMinutes > 0 ? afterMinutes : DEFAULT_AFTER_MINUTES;
  return nowMs + minutes * 60_000;
}
```

- [ ] **Step 4: Run the rule tests**

Run: `cd apps/web && npx vitest run src/lib/calendarRecording.test.ts`
Expected: PASS.

- [ ] **Step 5: Let the watcher be asked what the room is doing**

`apps/web/src/lib/silenceWatcher.ts`. Add to the interface:

```ts
  /// The current silence run, so a caller can ask whether anyone is talking *now* rather than only being told
  /// when the run reaches its threshold. Read by the extend prompt.
  state(): SilenceState;
```

and to the returned object:

```ts
  return {
    setPaused(next: boolean) {
      paused = next;
    },
    state: () => state,
    stop: teardown,
  };
```

`SilenceState` is already imported in that file.

- [ ] **Step 6: Split the two stop targets**

`apps/web/src/components/Recorder.tsx`. Beside `scheduledStopRef` (`:230`):

```tsx
  // The calendar event's own end (+ the user's overrun allowance), kept SEPARATE from the user's auto-stop
  // above even though the display shows whichever comes first. They behave differently when they fire: the
  // user's own auto-stop is a hard stop they asked for, while the calendar's only ends a meeting that has
  // actually finished - so the watcher has to be able to tell them apart. Merging them (which is what this
  // used to do) makes that impossible.
  const calendarStopRef = useRef<number | null>(null);
```

Replace the calendar block in `start()` (`:794-800`):

```tsx
      if (calendarEvent) {
        const calendarStop = resolveCalendarStopAt(calendarEvent.endsAt, calendarSettingsRef.current, Date.now());
        calendarStopRef.current = calendarStop;
        // The display still shows whichever target comes first, so nothing changes on screen.
        setScheduledStopAt(earlierStop(scheduledStopRef.current, calendarStop));
```

Leave the silence-watcher block that follows it, but change its callback to `() => stop("silence")` if Task 7 has not already.

In `stop()` (`:874-876`), clear the new ref too:

```tsx
    scheduledStopRef.current = null;
    calendarStopRef.current = null;
    setScheduledStopAt(null);
    setExtendAsk(null);
```

`applySchedule` (`:414-418`) sets `setScheduledStopAt(at)` directly, which would drop the calendar target from the display when the user changes their own auto-stop mid-recording. Change its last line to:

```tsx
    setScheduledStopAt(earlierStop(at, calendarStopRef.current));
```

- [ ] **Step 7: Ask instead of stopping**

Add the state, beside the other recorder state:

```tsx
  // The live "your meeting was due to end" question. `deadlineAt` is non-null only when the user has turned
  // the silence rule off, which is the one case where an unanswered prompt has no floor under it.
  const [extendAsk, setExtendAsk] = useState<{ deadlineAt: number | null } | null>(null);
```

Replace the watcher body (`:478-480`):

```tsx
    scheduleTimerRef.current = window.setInterval(() => {
      const now = Date.now();
      // The user's own auto-stop is a hard stop: they asked for it, so it is never negotiated.
      if (schedule.shouldStop(scheduledStopRef.current, now)) {
        stop("schedule");
        return;
      }
      // The prompt's own deadline, which only exists when the silence rule is off (see askToExtend).
      const ask = extendAskRef.current;
      if (ask?.deadlineAt != null && now >= ask.deadlineAt) {
        stop("calendar");
        return;
      }
      if (ask) return; // already asking - do not re-fire on every tick
      if (schedule.shouldStop(calendarStopRef.current, now)) askToExtend(now);
    }, 1000);
```

`extendAskRef` mirrors the state for the interval, which closes over its first render:

```tsx
  const extendAskRef = useRef<{ deadlineAt: number | null } | null>(null);
  extendAskRef.current = extendAsk;
```

and every `setExtendAsk(x)` must be paired with `extendAskRef.current = x` so the running interval sees it on the same tick. Add a small helper rather than repeating the pair:

```tsx
  function setAsk(next: { deadlineAt: number | null } | null) {
    extendAskRef.current = next;
    setExtendAsk(next);
  }
```

and use `setAsk` everywhere, including the clear in `stop()`.

Then the decision itself:

```tsx
  /// The calendar's stop time has arrived. If nobody is talking there is nothing to ask about, so the take
  /// ends exactly as it did before; if they are, hold off and ask.
  function askToExtend(nowMs: number) {
    const silence = silenceRef.current?.state();
    if (!silence || !shouldPromptExtend(silence, RECENT_SOUND_MS)) {
      stop("calendar");
      return;
    }
    // Unanswered, the recording keeps going and the silence rule ends it when the room empties. With silence
    // turned off there is no such floor, so the prompt gets a deadline of its own rather than recording until
    // the browser is closed.
    const seconds = calendarSettingsRef.current.silenceSeconds;
    const deadlineAt = seconds > 0 ? null : extendedStopAt(nowMs, calendarSettingsRef.current.afterMinutes);
    setAsk({ deadlineAt });
    notifyExtend();
  }

  function keepRecording() {
    calendarStopRef.current = extendedStopAt(Date.now(), calendarSettingsRef.current.afterMinutes);
    setScheduledStopAt(earlierStop(scheduledStopRef.current, calendarStopRef.current));
    setAsk(null);
  }
```

Import the new helpers:

```tsx
import {
  resolveCalendarStopAt, earlierStop, shouldPromptExtend, extendedStopAt, RECENT_SOUND_MS,
} from "../lib/calendarRecording";
```

- [ ] **Step 8: Notify outside the window**

The user is in Teams or Zoom, not looking at Diariz - which is the whole case for this feature. Add near the other module-level helpers in `Recorder.tsx`:

```tsx
/// Raise an OS notification for the extend prompt.
///
/// Works in both a browser and the Electron shell with no main-process involvement: the SPA has no CSP,
/// `apps/desktop` sets no permission request handler (so Electron grants by default), and `setAppUserModelId`
/// is already set on win32, which is what Windows requires for a renderer notification to appear.
///
/// Permission is asked for here, at the first moment it is actually needed, rather than on load - and a
/// refusal degrades silently to the in-app prompt, which is always shown regardless.
async function notify(title: string, body: string) {
  try {
    if (typeof Notification === "undefined") return;
    const permission =
      Notification.permission === "default" ? await Notification.requestPermission() : Notification.permission;
    if (permission === "granted") new Notification(title, { body });
  } catch {
    /* notifications unavailable - the in-app prompt still stands */
  }
}
```

and call it from `askToExtend`, replacing `notifyExtend()`:

```tsx
    void notify(t("extendNotifyTitle"), t("extendPromptText"));
```

- [ ] **Step 9: Render the prompt**

In the JSX, immediately before the recovery-banner block (`:1300`), add a sibling. It is deliberately a **separate** absolutely-positioned block rather than a fourth banner inside the existing one: that block is gated on `!recording`, and this only ever shows *while* recording, so the two can never collide.

```tsx
      {/* The meeting overran. Floated below the bar for the same reason the recovery banners are: the capture
          bar is fixed height, so an in-flow panel grows it and pushes the page down. */}
      {extendAsk && (
        <div
          data-testid="extend-prompt"
          className="absolute left-1/2 top-full z-40 mt-1 w-[28rem] max-w-[calc(100vw-2rem)] -translate-x-1/2"
        >
          <div className="flex flex-wrap items-center gap-2 rounded-lg border border-blue-300 bg-blue-50 px-3 py-2 text-sm text-blue-900 shadow-xl dark:border-blue-700 dark:bg-blue-900/30 dark:text-blue-100">
            <span>{t("extendPromptText")}</span>
            {extendAsk.deadlineAt != null && (
              <span className="text-xs text-blue-700 dark:text-blue-300">
                {t("extendEndingIn", { seconds: Math.max(0, Math.ceil((extendAsk.deadlineAt - Date.now()) / 1000)) })}
              </span>
            )}
            <div className="ml-auto flex gap-2">
              <button
                type="button"
                onClick={keepRecording}
                className="rounded bg-blue-600 px-2 py-1 text-xs text-white hover:bg-blue-700"
              >
                {t("extendKeep")}
              </button>
              <button
                type="button"
                /* A user pressing Stop needs no toast telling them the meeting is over - they just said so. */
                onClick={() => stop()}
                className="rounded border border-blue-400 px-2 py-1 text-xs dark:border-blue-700"
              >
                {t("extendStopNow")}
              </button>
            </div>
          </div>
        </div>
      )}
```

The countdown re-renders on the existing 1s elapsed ticker, which runs throughout a recording, so it needs no timer of its own.

- [ ] **Step 10: Add the copy**

`en/workspace.json`:

```json
  "extendPromptText": "This meeting was due to end, but people are still talking.",
  "extendKeep": "Keep recording",
  "extendStopNow": "Stop now",
  "extendEndingIn": "Ending in {{seconds}}s",
  "extendNotifyTitle": "Still recording?",
```

`de/workspace.json`:

```json
  "extendPromptText": "Dieser Termin sollte enden, aber es wird noch gesprochen.",
  "extendKeep": "Weiter aufnehmen",
  "extendStopNow": "Jetzt beenden",
  "extendEndingIn": "Endet in {{seconds}} s",
  "extendNotifyTitle": "Weiter aufnehmen?",
```

`es/workspace.json`:

```json
  "extendPromptText": "Esta reunión debía terminar, pero todavía se está hablando.",
  "extendKeep": "Seguir grabando",
  "extendStopNow": "Detener ahora",
  "extendEndingIn": "Termina en {{seconds}} s",
  "extendNotifyTitle": "¿Seguir grabando?",
```

`fr/workspace.json`:

```json
  "extendPromptText": "Cette réunion devait se terminer, mais la discussion continue.",
  "extendKeep": "Continuer l'enregistrement",
  "extendStopNow": "Arrêter maintenant",
  "extendEndingIn": "Fin dans {{seconds}} s",
  "extendNotifyTitle": "Continuer l'enregistrement ?",
```

- [ ] **Step 11: Run everything**

Run: `cd apps/web && npm test && npm run build`
Expected: PASS, clean typecheck.

- [ ] **Step 12: Verify the prompt in the browser**

jsdom computes no layout, so no test above proves the prompt is actually usable on screen. Start the dev server, sign in, and check with `getBoundingClientRect`:

- the prompt does **not** change the capture bar's height (measure the bar before and after it appears - the delta must be 0);
- the panel does not overflow the content column at a narrow width (drag the chat panel wide, or resize to ~900px);
- both light and dark themes.

Record the measured numbers in the PR description. If the bar's height moves at all, the prompt has landed in the bar's flow rather than the floating layer, which is the defect `Recorder.tsx:1300` exists to warn about.

- [ ] **Step 13: Commit**

```bash
git add apps/web/src/lib/calendarRecording.ts apps/web/src/lib/calendarRecording.test.ts apps/web/src/lib/silenceWatcher.ts apps/web/src/components/Recorder.tsx apps/web/src/locales/en/workspace.json apps/web/src/locales/de/workspace.json apps/web/src/locales/es/workspace.json apps/web/src/locales/fr/workspace.json
git commit -m "feat(recorder): offer to extend a meeting that overruns its invite"
```

---

### Task 9: Release, docs and help

**Files:**
- Modify: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`
- Modify: `apps/web/src/lib/releases.ts`
- Modify: `README.md`, `docs/features.md`
- Modify: `docs/Data_Schema.md`, `docs/Overall_Synopsis_of_Platform.md`
- Modify: `apps/web/src/content/help/en/*.md` (the recording and calendar articles)

**Interfaces:**
- Consumes: everything above.
- Produces: the release.

- [ ] **Step 1: Bump the version**

Read `version.json` for the current value. This is a **functional enhancement**, so **minor +1, build reset to 0** (e.g. `0.201.0` -> `0.202.0`). Set the identical value in all five files: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj` (`<Version>`), `integrations/n8n-nodes-diariz/package.json`.

`apps/desktop/package.json` moves in lockstep even though no desktop release is cut - the mirror test asserts it, and desktop version numbers are allowed to skip.

- [ ] **Step 2: Add the release entry**

Prepend to `RELEASES` in `apps/web/src/lib/releases.ts`. The `pr` number cannot be guessed from "last + 1" - Dependabot PRs and issues share the sequence, and no test catches a wrong one. Get the real number by opening the PR first (Step 7) and filling this in, or by checking `gh pr list --limit 1 --state all --json number`.

```ts
  {
    version: "0.202.0",
    date: "2026-08-10",
    pr: 0, // replace with the real PR number
    headline: "Calendar events that repeat, and meetings that overrun",
    summary:
      "Recordings now tell you when they have ended by themselves and why. A calendar event that repeats is " +
      "marked as such, and shows what you recorded of the same meeting before, so you can jump back to the " +
      "last one. The attendee list starts collapsed. And when a recording started from an invite reaches the " +
      "end of the meeting while people are still talking, it asks whether to keep going instead of cutting " +
      "you off - if nobody answers it keeps recording, and stops on its own once the room empties.",
    added: [
      "Repeating calendar events are marked, with a list of your earlier recordings of the same meeting.",
      "An \"Extend this meeting\" prompt when a calendar recording reaches its end while people are still talking.",
      "A notification when a recording stops by itself, saying which rule ended it.",
    ],
    changed: [
      "The attendee list on a calendar event starts collapsed, with the count still shown.",
      "The calendar day heading counts \"events\" rather than \"meetings\".",
    ],
  },
```

Adjust `version` to whatever Step 1 chose. `releases.test.ts` asserts `RELEASES[0].version === version.json`.

- [ ] **Step 3: Update the feature inventories in lockstep**

All three describe what exists and must agree:

- **README.md** Features table - add a row: `| Recurring meetings | Events that repeat are marked, and show your earlier recordings of the same meeting. |`
- **docs/features.md** - the matching prose bullet, in the calendar section.
- **`CAPABILITIES`** in `apps/web/src/lib/releases.ts` - the matching two-column table row.

The extend prompt is a change to recording behaviour rather than a new feature area; fold it into the existing recording/auto-stop row rather than adding a fourth.

- [ ] **Step 4: Update the reference docs**

- **`docs/Data_Schema.md`**: the new `RecordingCalendarLinks.SeriesId` column (`varchar(1024)`, nullable) with a line on why it is stored rather than derived, and a row in the migration-history table for `AddCalendarSeriesId`.
- **`docs/Overall_Synopsis_of_Platform.md`**: the new `GET /api/calendar/events/{eventId}/recordings` endpoint, and the extend flow in whatever section describes calendar-driven recording. Note there that recurrence is carried on the shared `CalendarEvent` projection from all three sources.

- [ ] **Step 5: Update the help articles**

Help content is task-oriented user prose, not a fourth inventory - update it where the **behaviour a user relies on** changed:

- The article covering recording auto-stop: a recording that ends by itself now says so, and a calendar recording asks before ending while people are still talking (and keeps recording if nobody answers).
- `apps/web/src/content/help/en/outlook-calendar.md` and the Google/calendar article: repeating meetings are marked and show earlier recordings.

Content is **ASCII only**, and each article keeps its `title` / `summary` / `group` / `order` front matter. `content/help/helpContent.test.ts` enforces both.

- [ ] **Step 6: Run every suite**

```bash
dotnet build Diariz.slnx
dotnet test tests/Diariz.Api.Tests
cd apps/web && npm test && npm run build
```

Expected: all PASS, no warnings. `versionMirrors.test.ts` and `releases.test.ts` are the ones that catch a half-done bump.

- [ ] **Step 7: Check for em dashes, then open the PR**

Scan every user-facing file the branch touched. Do **not** pipe git output into python on this machine - it decodes as cp1252 and reports a false zero. Write to a file and decode UTF-8 explicitly:

```bash
git diff main...HEAD -- "apps/web/src/locales/*" "apps/web/src/lib/releases.ts" "apps/web/src/content/help/*" "README.md" "docs/features.md" > branch.diff
python -c "import io; t=io.open('branch.diff',encoding='utf-8').read(); [print(repr(l)) for l in t.splitlines() if l.startswith('+') and ('—' in l or '–' in l)]"
```

Expected: no output. Then delete `branch.diff` - do not commit it.

Write the diff to a real file rather than piping `git diff` straight into python: on Windows the pipe decodes as cp1252, so the check silently reports zero regardless of what the diff contains. `/tmp` is not writable in every shell here either, hence a path relative to the repo root.

```bash
git push -u origin feat/calendar-improvements
gh pr create --title "feat(calendar): recurring meetings, extend prompt, and auto-end feedback" --body "..."
```

The PR body must state the **deployment surface**: this is a **server redeploy** (web + API + one migration). **No desktop release** - `apps/desktop/src/**`, `build/**` and `electron-builder.config.js` are untouched; the only file under `apps/desktop` is a comment correction in the native Outlook reader's C# source, which is not part of the Electron build.

- [ ] **Step 8: Fill in the PR number and commit**

```bash
git add apps/web/src/lib/releases.ts
git commit -m "docs: record the PR number in the release notes"
git push
```

---

## Self-Review

**Spec coverage.** Item 1 -> Task 7. Item 2 -> Task 1. Item 3 -> Tasks 2, 3, 4, 5 (badge), 6 (history). Item 4 -> Task 5. Item 5 -> Task 8. Release checklist -> Task 9. The spec's "Testing" section is distributed across the tasks; its "Out of scope" list is enforced by the global constraint against touching `apps/desktop`.

**One spec deviation, deliberate.** The spec said the extend prompt would be "a persistent bar next to the recorder"; the plan floats it below the record controls instead, because `Recorder.tsx:1123`/`:1300` record that the capture bar is fixed height and an in-flow line pushed the page down. The spec has been amended to match.

**Two things the plan asks an implementer to prove rather than assume.** Task 7 Step 2 requires temporarily breaking `WorkspaceLayout` so the toast test is seen to fail - the toast context no-ops silently, so a green test proves nothing on its own. Task 1 Step 2 explicitly discards a first-draft test that passes before the change, for the same reason.

**Both surfaces covered.** The spec puts the series list on the calendar event page *and* the recording Overview. The second mount point is `components/detail/MeetingCard.tsx:78-91` (not `RecordingDetail.tsx`, which delegates to it), inside the `linked` branch - Task 6 Step 7 gives the exact edit, including why it must key off `calendarLink.eventId` rather than `linkedEvent?.id`, which is null until the live fetch resolves.

**Type consistency check.** `CalendarEvent.Recurring`/`SeriesId` (Task 2) are read by Task 3 (`ev.SeriesId`), Task 4 (both), and Task 5 (`recurring` over the wire, `seriesId` deliberately not serialised to the browser). `SeriesRecordingDto(Guid Id, string Title, string? Name, DateTimeOffset StartsAt, DateTimeOffset EndsAt)` in Task 4 matches the web `SeriesRecording { id, title, name, startsAt, endsAt }` in Task 6 field for field. `stop(reason?: StopReason)` in Task 7 is called as `stop("calendar")` and `stop()` in Task 8, both within the declared union.
