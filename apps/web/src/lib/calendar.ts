import type { RecordingSummary } from "./types";

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
