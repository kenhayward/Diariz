/// How the List tab's recordings are ordered, and where that choice is kept.
///
/// The sort is **display only**. `computeReorder` (lib/reorder.ts) turns whatever id list it is handed into
/// the recordings' new server `Position`s, so the panel keeps feeding its reorder writes the unsorted level
/// while rendering the sorted one. Sorting a view must never rewrite an order the user arranged by hand.

import { useState } from "react";
import type { RecordingSummary } from "./types";

export type SortKey = "manual" | "date" | "name" | "duration";
export type SortDir = "asc" | "desc";

export interface ListSort {
  key: SortKey;
  dir: SortDir;
}

/// Namespaced like `PANEL_TAB_KEY`, so the app's localStorage keys stay recognisably ours.
export const LIST_SORT_KEY = "diariz.recordings.sort";

/// The order the list has always shown: whatever `Position` the server returns.
export const DEFAULT_SORT: ListSort = { key: "manual", dir: "asc" };

const KEYS: readonly SortKey[] = ["manual", "date", "name", "duration"];
const DIRS: readonly SortDir[] = ["asc", "desc"];

/// Anything unparseable or unrecognised reads as the default - a first visit, a value from an older build,
/// and a hand-edited key all have to leave the list with an order it can render.
export function parseSort(raw: string | null): ListSort {
  if (!raw) return DEFAULT_SORT;
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return DEFAULT_SORT;
  }
  if (typeof parsed !== "object" || parsed === null) return DEFAULT_SORT;
  const { key, dir } = parsed as { key?: unknown; dir?: unknown };
  if (!KEYS.includes(key as SortKey) || !DIRS.includes(dir as SortDir)) return DEFAULT_SORT;
  return { key: key as SortKey, dir: dir as SortDir };
}

const displayName = (r: RecordingSummary) => r.name ?? r.title;

/// Ascending comparator per key. `desc` is this reversed rather than a second set of comparators, so the
/// two directions cannot disagree about ties.
function compare(a: RecordingSummary, b: RecordingSummary, key: SortKey): number {
  switch (key) {
    case "date":
      return new Date(a.createdAt).getTime() - new Date(b.createdAt).getTime();
    case "name":
      return displayName(a).localeCompare(displayName(b));
    case "duration":
      return a.durationMs - b.durationMs;
    default:
      return 0;
  }
}

/// A **new** array in the chosen order. Manual returns a copy in the input's order: the caller renders this
/// and keeps the original for its reorder writes, so mutating in place would corrupt the very list the
/// display-only rule exists to protect.
export function sortRecordings(items: RecordingSummary[], sort: ListSort): RecordingSummary[] {
  if (sort.key === "manual") return [...items];
  const sign = sort.dir === "desc" ? -1 : 1;
  return [...items].sort((a, b) => sign * compare(a, b, sort.key));
}

/// The setting, persisted globally. One preference for the whole app: a user who wants newest-first wants it
/// in every folder and every room, and per-folder state would make the same list sort differently as they
/// drill. Storage being unavailable (private mode) is not an error - the sort still moves for this session,
/// it just will not survive a reload.
export function useListSort(): [ListSort, (next: ListSort) => void] {
  const [sort, setSort] = useState<ListSort>(() => {
    try {
      return parseSort(localStorage.getItem(LIST_SORT_KEY));
    } catch {
      return DEFAULT_SORT;
    }
  });

  function update(next: ListSort) {
    setSort(next);
    try {
      localStorage.setItem(LIST_SORT_KEY, JSON.stringify(next));
    } catch {
      /* storage disabled: the sort still applies this session */
    }
  }

  return [sort, update];
}
