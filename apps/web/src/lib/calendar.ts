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

/// The set of local day-keys that have at least one recording (drives which calendar cells are
/// highlighted and selectable).
export function recordingDayKeys(recordings: RecordingSummary[]): Set<string> {
  return new Set(recordings.map((r) => isoToDayKey(r.createdAt)));
}

/// Recordings created on the given local day-key, oldest first (so the day reads top-to-bottom in time).
export function recordingsForDay(recordings: RecordingSummary[], key: string): RecordingSummary[] {
  return recordings
    .filter((r) => isoToDayKey(r.createdAt) === key)
    .sort((a, b) => new Date(a.createdAt).getTime() - new Date(b.createdAt).getTime());
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
export function dayItems(recordings: RecordingSummary[], events: CalendarEvent[], key: string): DayItem[] {
  const items: DayItem[] = [];
  for (const r of recordings) {
    if (isoToDayKey(r.createdAt) === key) items.push({ type: "recording", time: new Date(r.createdAt).getTime(), recording: r });
  }
  for (const e of events) {
    if (!eventDayKeys([e]).has(key)) continue;
    // Starts this day → sort by its start; started earlier (spilled in) → sort at the top.
    const startsToday = isoToDayKey(e.start) === key;
    items.push({ type: "event", time: startsToday ? new Date(e.start).getTime() : 0, event: e });
  }
  return items.sort((a, b) => a.time - b.time);
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
