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
  minPx = 12,
  maxPx = 28,
): number {
  if (maxWeight <= minWeight) return Math.round((minPx + maxPx) / 2);
  const t = (Math.log(weight + 1) - Math.log(minWeight + 1)) / (Math.log(maxWeight + 1) - Math.log(minWeight + 1));
  return Math.round(minPx + t * (maxPx - minPx));
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
