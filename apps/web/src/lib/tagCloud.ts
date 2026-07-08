/// Pure helpers for the Tags tab (the cross-transcript tag cloud). The view layer drives these so the
/// font-size scaling and the tag -> recordings filtering are unit-testable without React.

import type { RecordingSummary, TagCloudEntry } from "./types";

/// Font size (px) for a tag whose aggregate weight sits in [minWeight, maxWeight]. Log-scaled so one
/// runaway tag doesn't flatten everything else to the floor; all-equal weights (including a single tag)
/// land on the midpoint. Bounds default to the left panel's 12-28px; the expanded modal passes larger ones.
export function fontSizeFor(
  weight: number,
  minWeight: number,
  maxWeight: number,
  minPx = 11,
  maxPx = 22,
): number {
  return Math.round(minPx + norm(weight, minWeight, maxWeight) * (maxPx - minPx));
}

/// A subtle colour for a tag by aggregate weight, so more important tags read as warmer/stronger alongside
/// their larger size. Interpolates a calm blue (least important) -> indigo -> violet (most important) at a
/// fixed medium lightness so it stays legible on both light and dark backgrounds (inline colours can't adapt
/// to the theme). All-equal weights collapse to the mid colour.
export function tagColor(weight: number, minWeight: number, maxWeight: number): string {
  const t = maxWeight <= minWeight ? 0.5 : norm(weight, minWeight, maxWeight);
  const hue = Math.round(212 + t * 68); // 212 (blue) -> 280 (violet)
  const sat = Math.round(30 + t * 40); // 30% -> 70%
  return `hsl(${hue} ${sat}% 55%)`;
}

/// The `limit` most-used tags (by recording count, then weight as a tie-break) — the count slider's filter.
/// A non-positive limit yields an empty list; a limit at/above the total returns every tag.
export function topTagsByCount(tags: TagCloudEntry[], limit: number): TagCloudEntry[] {
  if (limit <= 0) return [];
  return [...tags]
    .sort((a, b) => b.count - a.count || b.weight - a.weight)
    .slice(0, limit);
}

/// Log-scaled 0..1 position of `weight` within [minWeight, maxWeight]; 0.5 when the range is degenerate.
function norm(weight: number, minWeight: number, maxWeight: number): number {
  if (maxWeight <= minWeight) return 0.5;
  return (Math.log(weight + 1) - Math.log(minWeight + 1)) / (Math.log(maxWeight + 1) - Math.log(minWeight + 1));
}

/// The recordings to list under the cloud: with a selected tag, only that tag's recordings; with none, the
/// union of all tagged recordings. Order follows the given recordings list (the server's newest-first), and
/// ids missing from it (a refetch race) are silently dropped. An unknown tag yields an empty list.
export function recordingsForTags(
  recordings: RecordingSummary[],
  tags: TagCloudEntry[],
  selectedTag: string | null,
): RecordingSummary[] {
  const wanted = new Set<string>();
  if (selectedTag !== null) {
    const entry = tags.find((t) => t.tag === selectedTag);
    if (!entry) return [];
    for (const id of entry.recordingIds) wanted.add(id);
  } else {
    for (const t of tags) for (const id of t.recordingIds) wanted.add(id);
  }
  return recordings.filter((r) => wanted.has(r.id));
}
