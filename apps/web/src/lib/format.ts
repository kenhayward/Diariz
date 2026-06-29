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
