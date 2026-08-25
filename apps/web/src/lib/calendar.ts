import type { CalendarEvent, RecordingSummary } from "./types";

/// A single cell in the month grid. `key` is the local YYYY-MM-DD; `inMonth` is false for the
/// leading/trailing days that belong to the adjacent months (shown greyed for context).
export interface CalendarDay {
  date: Date;
  key: string;
  inMonth: boolean;
}

const pad = (n: number) => String(n).padStart(2, "0");

/// Local calendar-date key (YYYY-MM-DD) for a Date — uses local components so a recording is filed
/// under the day the user actually sees, regardless of timezone/UTC offset.
export function dayKey(d: Date): string {
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

export function isoToDayKey(iso: string): string {
  return dayKey(new Date(iso));
}

/// A fixed 6-row × 7-column grid for the given month (Monday-first), covering the whole month plus the
/// adjacent days that fill the first/last weeks. Built from local-midnight Dates (constructed via the
/// y/m/d Date ctor) so it's DST-safe.
export function buildMonthGrid(year: number, month: number): CalendarDay[][] {
  const first = new Date(year, month, 1);
  const startOffset = (first.getDay() + 6) % 7; // Monday = 0
  const weeks: CalendarDay[][] = [];
  for (let w = 0; w < 6; w++) {
    const week: CalendarDay[] = [];
    for (let d = 0; d < 7; d++) {
      const date = new Date(year, month, 1 - startOffset + w * 7 + d);
      week.push({ date, key: dayKey(date), inMonth: date.getMonth() === month });
    }
    weeks.push(week);
  }
  return weeks;
}

/// When a recording happened, for placing it on the calendar: the wall clock capture began, falling back to
/// the upload time for uploaded files and recordings made before the start was tracked.
///
/// Using `createdAt` here files a meeting recorded 23:30-00:30 under the *following* day - and in a different
/// cell from the very event it is linked to, which still sits on the day it started.
///
/// Exported because the day grid places blocks by the same rule that files them into a day cell: if the two
/// ever disagreed, a block would sit on a day the month grid says it isn't on.
export function recordingTime(r: RecordingSummary): string {
  return r.startedAt ?? r.createdAt;
}

/// A recording's wall-clock span, for sizing its block on the day grid.
///
/// The end prefers `endedAt`: `durationMs` is recorded-audio length and excludes paused stretches, so a
/// meeting paused for ten minutes would occupy the wrong slot. Clock skew between the two timestamps is
/// floored at the start rather than allowed to produce a negative height.
export function recordingSpan(r: RecordingSummary): { start: Date; end: Date } {
  const start = new Date(recordingTime(r));
  const endMs = r.endedAt ? new Date(r.endedAt).getTime() : start.getTime() + Math.max(0, r.durationMs);
  return { start, end: new Date(Math.max(start.getTime(), endMs)) };
}

/// A day item reduced to the day grid's coordinates: fractional local hours on `key`'s day.
///
/// `endHour` may exceed 24 for an item that runs past midnight (the layout clamps it) - and both are read
/// from real timestamps rather than `DayItem.time`, which is a *sort sentinel* (`dayItems` sets 0 for a
/// spilled-in multi-day event) and is meaningless as a coordinate.
export interface DayItemSpan {
  startHour: number;
  endHour: number;
  /// Belongs in the pinned chip row rather than on the time axis.
  allDay: boolean;
}

/// Fractional local hours since midnight - wall clock, never `(ms - dayStart) / 3600000`, which is an hour
/// out for the whole of a DST day.
function hourOfDay(d: Date): number {
  return d.getHours() + d.getMinutes() / 60 + d.getSeconds() / 3600;
}

export function dayItemSpan(item: DayItem, key: string): DayItemSpan {
  if (item.type === "recording") {
    const { start, end } = recordingSpan(item.recording);
    // Whole days elapsed, so a recording that ran past midnight reports endHour > 24 rather than wrapping.
    const dayOffset = Math.round((midnightOf(end).getTime() - midnightOf(start).getTime()) / 86_400_000);
    return { startHour: hourOfDay(start), endHour: hourOfDay(end) + dayOffset * 24, allDay: false };
  }

  const e = item.event;
  const start = new Date(e.start);
  const end = new Date(e.end);
  const midnight = (d: Date) => d.getHours() === 0 && d.getMinutes() === 0 && d.getSeconds() === 0;
  // A date-only entry from the server, a span that began on an earlier day (no meaningful start on this
  // one), or - for older payloads with no flag - a local-midnight-to-midnight span. `end > start` keeps a
  // zero-length event out of this: that is a timed event at its own hour, not an all-day one.
  const allDay =
    e.allDay === true ||
    isoToDayKey(e.start) !== key ||
    (midnight(start) && midnight(end) && end.getTime() > start.getTime());
  const dayOffset = Math.round((midnightOf(end).getTime() - midnightOf(start).getTime()) / 86_400_000);
  return { startHour: hourOfDay(start), endHour: hourOfDay(end) + dayOffset * 24, allDay };
}

const midnightOf = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate());

/// The set of local day-keys that have at least one recording (drives which calendar cells are
/// highlighted and selectable).
export function recordingDayKeys(recordings: RecordingSummary[]): Set<string> {
  return new Set(recordings.map((r) => isoToDayKey(recordingTime(r))));
}

/// Recordings that started on the given local day-key, oldest first (so the day reads top-to-bottom in time).
export function recordingsForDay(recordings: RecordingSummary[], key: string): RecordingSummary[] {
  return recordings
    .filter((r) => isoToDayKey(recordingTime(r)) === key)
    .sort((a, b) => new Date(recordingTime(a)).getTime() - new Date(recordingTime(b)).getTime());
}

/// The set of local day-keys a set of calendar events covers — expands multi-day and all-day events
/// across every day they touch. An all-day event's end is Google's exclusive next-midnight, so a span
/// that ends exactly at local midnight does not count the end day.
export function eventDayKeys(events: CalendarEvent[]): Set<string> {
  const keys = new Set<string>();
  for (const e of events) {
    const start = new Date(e.start);
    const end = new Date(e.end);
    // Walk local midnights from the start day up to (and including) the last covered day.
    const last = new Date(end);
    const endsAtMidnight = end.getHours() === 0 && end.getMinutes() === 0 && end.getSeconds() === 0 && end.getMilliseconds() === 0;
    if (endsAtMidnight && end.getTime() > start.getTime()) last.setDate(last.getDate() - 1);
    const cursor = new Date(start.getFullYear(), start.getMonth(), start.getDate());
    const lastDay = new Date(last.getFullYear(), last.getMonth(), last.getDate());
    while (cursor.getTime() <= lastDay.getTime()) {
      keys.add(dayKey(cursor));
      cursor.setDate(cursor.getDate() + 1);
    }
  }
  return keys;
}

/// The [timeMin, timeMax) ISO window covering the whole visible month grid (first cell to the day after
/// the last cell) — the range to ask Google Calendar for so every visible cell can be coloured.
export function visibleGridRange(year: number, month: number): { timeMin: string; timeMax: string } {
  const grid = buildMonthGrid(year, month);
  const firstCell = grid[0][0].date;
  const lastCell = grid[grid.length - 1][6].date;
  const timeMax = new Date(lastCell.getFullYear(), lastCell.getMonth(), lastCell.getDate() + 1);
  return { timeMin: firstCell.toISOString(), timeMax: timeMax.toISOString() };
}

/// One entry in a day's merged timeline — either a recording or a calendar event.
export type DayItem =
  | { type: "recording"; time: number; recording: RecordingSummary }
  | { type: "event"; time: number; event: CalendarEvent };

/// The selected day's recordings and calendar events, merged and ordered by time of day. An event that
/// began on an earlier day (a multi-day span) sorts to the top of this day (time 0).
///
/// A linked event is **deduped**: if a recording linked to it (`recording.calendarEventId === event.id`) is
/// being drawn on *this* day, the standalone event row is dropped - the recording row (which shows both the
/// mic and calendar icons) represents the pair.
///
/// The suppressing recording has to be one of this day's, which is why the id set is filled by the same loop
/// that emits the recording rows rather than swept from every recording the caller holds. A link routinely
/// crosses a day boundary - Outlook keeps one GlobalAppointmentID when a meeting is rescheduled, so the event
/// moves to another day while the recording made under its old slot stays attached to it, and a manual link
/// can pair the two freely. Suppressing on a cross-day link removed the meeting from the calendar altogether:
/// nothing was drawn on the day it had moved to, and the recording that was meant to stand in for it sat on a
/// different day entirely. Drawing both - the event on its day, the recording on its own - is the truth about
/// where they are, and it keeps this in step with `dayEventCount`, which never dropped the event from the
/// header count.
export function dayItems(recordings: RecordingSummary[], events: CalendarEvent[], key: string): DayItem[] {
  const linkedEventIds = new Set<string>();
  const items: DayItem[] = [];
  for (const r of recordings) {
    const at = recordingTime(r);
    if (isoToDayKey(at) !== key) continue;
    items.push({ type: "recording", time: new Date(at).getTime(), recording: r });
    if (r.calendarEventId != null) linkedEventIds.add(r.calendarEventId);
  }
  for (const e of events) {
    if (linkedEventIds.has(e.id)) continue; // represented by this day's recording row (both icons)
    if (!eventDayKeys([e]).has(key)) continue;
    // Starts this day → sort by its start; started earlier (spilled in) → sort at the top.
    const startsToday = isoToDayKey(e.start) === key;
    items.push({ type: "event", time: startsToday ? new Date(e.start).getTime() : 0, event: e });
  }
  return items.sort((a, b) => a.time - b.time);
}

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

/// Localized weekday initials (Mon-first) for the column headers.
export function weekdayLabels(locale: string): string[] {
  const fmt = new Intl.DateTimeFormat(locale, { weekday: "short" });
  // 2024-01-01 is a Monday; emit 7 consecutive days.
  return Array.from({ length: 7 }, (_, i) => fmt.format(new Date(2024, 0, 1 + i)));
}

/// Localized "Month YYYY" heading for the calendar.
export function monthLabel(year: number, month: number, locale: string): string {
  return new Intl.DateTimeFormat(locale, { month: "long", year: "numeric" }).format(new Date(year, month, 1));
}
