import type { Epoch } from "./types";

export interface EpochSpan {
  count: number;
  /// Date of the oldest release in the epoch.
  earliest: string;
  /// Date of the newest release in the epoch.
  latest: string;
}

/// How many releases an epoch covers, and the dates at either end of it.
///
/// Derived rather than stored on the epoch: a stored count and span would be a second derivation of
/// something the release list already knows, and the two would agree only by luck. The spine
/// (`ARCHIVED_SPINE`) exists so this can be computed without loading the archive itself.
///
/// Total: returns `null` when either bound is missing from the spine, rather than throwing. The bounds
/// are guaranteed to exist by `epochs.test.ts`, so a `null` here means a mistake made between test runs
/// - which should show up as a card missing its dates, not as a blank page.
export function epochSpan(epoch: Epoch, spine: ReadonlyArray<{ version: string; date: string }>): EpochSpan | null {
  // The spine runs newest first, so the epoch's newest release (`to`) sits at the lower index.
  const newest = spine.findIndex((r) => r.version === epoch.to);
  const oldest = spine.findIndex((r) => r.version === epoch.from);
  if (newest === -1 || oldest === -1 || oldest < newest) return null;

  return {
    count: oldest - newest + 1,
    earliest: spine[oldest].date,
    latest: spine[newest].date,
  };
}
