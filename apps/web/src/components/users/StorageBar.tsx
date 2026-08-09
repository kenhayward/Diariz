import { formatBytes, storagePercent } from "../../lib/format";

/// A used/quota bar. Turns red past 85% so a nearly full account is visible while scanning the list, rather
/// than only once uploads start failing. `--hub-red` is not used here: this is a fill, and the app's red fill
/// elsewhere is Tailwind's.
export default function StorageBar({
  usedBytes,
  quotaBytes,
  height = 4,
}: {
  usedBytes: number;
  quotaBytes: number;
  height?: number;
}) {
  const pct = storagePercent(usedBytes, quotaBytes);
  return (
    <div
      className="w-full overflow-hidden rounded-full bg-gray-200 dark:bg-gray-700"
      style={{ height }}
      // Decorative: the figures underneath say the same thing in words.
      aria-hidden
    >
      <div
        className={`h-full rounded-full ${pct >= 85 ? "bg-red-500" : "bg-green-500"}`}
        style={{ width: `${Math.min(100, pct)}%` }}
      />
    </div>
  );
}

/// `1.9 GB / 5 GB`, the line that sits under (or beside) the bar.
export function storageText(usedBytes: number, quotaBytes: number): string {
  return `${formatBytes(usedBytes)} / ${formatBytes(quotaBytes)}`;
}
