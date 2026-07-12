/// Human-readable byte size using binary units (1024). Whole numbers show no decimal,
/// otherwise one decimal place. e.g. 0 → "0 B", 1536 → "1.5 KB", 5*1024**3 → "5 GB".
export function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return "0 B";
  const units = ["B", "KB", "MB", "GB", "TB"];
  const i = Math.min(units.length - 1, Math.floor(Math.log(bytes) / Math.log(1024)));
  const value = bytes / 1024 ** i;
  const str = Number.isInteger(value) ? String(value) : value.toFixed(1);
  return `${str} ${units[i]}`;
}

/// Duration (ms) as h:mm:ss, or m:ss under an hour. The leading unit has no zero-prefix
/// (e.g. 419000 → "6:59", 3661000 → "1:01:01"); lower units are zero-padded so columns line up.
export function formatDuration(ms: number): string {
  const total = Math.max(0, Math.round(ms / 1000));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  const pad = (n: number) => String(n).padStart(2, "0");
  return h > 0 ? `${h}:${pad(m)}:${pad(s)}` : `${m}:${pad(s)}`;
}

/// A date (ISO string) formatted for the given locale via `Intl` (falls back to the browser locale when
/// none is passed). Routes user-facing dates through the active UI language.
export function formatDate(iso: string, locale?: string): string {
  return new Date(iso).toLocaleDateString(locale || undefined);
}

const ORD = ["th", "st", "nd", "rd"];

/// An extended, human date for the given locale, e.g. English "23rd March 2026". English locales get an
/// ordinal day suffix; other locales use their natural long form ("23 mars 2026", "23. März 2026") via `Intl`.
export function formatLongDate(iso: string, locale?: string): string {
  const d = new Date(iso);
  const lang = (locale ?? "en").toLowerCase();
  if (lang.startsWith("en")) {
    const day = d.getDate();
    const v = day % 100;
    const suffix = ORD[(v - 20) % 10] || ORD[v] || ORD[0];
    const month = d.toLocaleDateString(locale || undefined, { month: "long" });
    return `${day}${suffix} ${month} ${d.getFullYear()}`;
  }
  return d.toLocaleDateString(locale || undefined, { day: "numeric", month: "long", year: "numeric" });
}

/// Time of day as fixed 24-hour "hh:mm" (local timezone), e.g. 09:05, 14:30.
export function formatTimeHm(iso: string): string {
  const d = new Date(iso);
  return `${String(d.getHours()).padStart(2, "0")}:${String(d.getMinutes()).padStart(2, "0")}`;
}

/// Duration (ms) as fixed "hh:mm" (hours:minutes, both zero-padded), e.g. 65000 → "00:01", 3900000 → "01:05".
export function formatDurationHm(ms: number): string {
  const total = Math.max(0, Math.round(ms / 1000));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  return `${String(h).padStart(2, "0")}:${String(m).padStart(2, "0")}`;
}

/// Percentage of quota used (0 when there's no quota), rounded to a whole number.
export function storagePercent(usedBytes: number, quotaBytes: number): number {
  if (quotaBytes <= 0) return 0;
  return Math.round((usedBytes / quotaBytes) * 100);
}

const BYTES_PER_GB = 1024 ** 3;

/// Bytes → gigabytes for display in the quota inputs (trimmed to avoid float noise).
export function bytesToGb(bytes: number): number {
  return Math.round((bytes / BYTES_PER_GB) * 100) / 100;
}

/// Gigabytes (as typed in the quota inputs) → bytes.
export function gbToBytes(gb: number): number {
  return Math.round(gb * BYTES_PER_GB);
}

const RELATIVE_UNITS: { unit: Intl.RelativeTimeFormatUnit; ms: number }[] = [
  { unit: "year", ms: 365 * 24 * 3600 * 1000 },
  { unit: "month", ms: 30 * 24 * 3600 * 1000 },
  { unit: "week", ms: 7 * 24 * 3600 * 1000 },
  { unit: "day", ms: 24 * 3600 * 1000 },
  { unit: "hour", ms: 3600 * 1000 },
  { unit: "minute", ms: 60 * 1000 },
  { unit: "second", ms: 1000 },
];

/// A locale-aware relative time string ("3 hours ago", "now") via `Intl.RelativeTimeFormat`, used for the
/// Formulas results list ("Generated {{time}} from the ... formula"). Picks the largest whole unit that
/// fits the gap to `now` (defaults to the real clock; a fixed `now` is passed in tests for determinism).
export function formatRelativeTime(iso: string, locale?: string, now: Date = new Date()): string {
  const diffMs = now.getTime() - new Date(iso).getTime();
  const rtf = new Intl.RelativeTimeFormat(locale || undefined, { numeric: "auto" });
  for (const { unit, ms } of RELATIVE_UNITS) {
    if (Math.abs(diffMs) >= ms || unit === "second") {
      return rtf.format(-Math.round(diffMs / ms), unit);
    }
  }
  return rtf.format(0, "second");
}
