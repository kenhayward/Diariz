/// Pure helpers for auditioning a single speaker's audio. The recording detail page drives the shared
/// <audio> element through these: build the speaker's play ranges, then on each timeupdate decide whether
/// to keep playing, seek over a gap (another speaker), or stop. Kept side-effect-free so the timing logic
/// is unit-testable without a real media element.

export interface PlayRange {
  /** Inclusive start in milliseconds. */
  start: number;
  /** Exclusive end in milliseconds. */
  end: number;
}

type SegmentLike = { speaker: string; startMs: number; endMs: number };

/// The segments spoken by `label`, as merged, sorted, gapless-within ranges. Touching or overlapping
/// spans (e.g. consecutive same-speaker rows) collapse into one range so playback doesn't micro-seek.
export function speakerRanges(segments: SegmentLike[], label: string): PlayRange[] {
  const sorted = segments
    .filter((s) => s.speaker === label)
    .map((s) => ({ start: s.startMs, end: s.endMs }))
    .sort((a, b) => a.start - b.start);

  const merged: PlayRange[] = [];
  for (const r of sorted) {
    const last = merged[merged.length - 1];
    if (last && r.start <= last.end) last.end = Math.max(last.end, r.end);
    else merged.push({ ...r });
  }
  return merged;
}

type IdSegmentLike = { id: string; startMs: number; endMs: number };

/// The play ranges for a set of selected segment ids, as merged, sorted, gapless ranges — so "Play selected"
/// plays only those segments, skipping the gaps between non-adjacent picks. Same merge as `speakerRanges`.
export function selectedRanges(segments: IdSegmentLike[], ids: Iterable<string>): PlayRange[] {
  const want = ids instanceof Set ? ids : new Set(ids);
  const sorted = segments
    .filter((s) => want.has(s.id))
    .map((s) => ({ start: s.startMs, end: s.endMs }))
    .sort((a, b) => a.start - b.start);

  const merged: PlayRange[] = [];
  for (const r of sorted) {
    const last = merged[merged.length - 1];
    if (last && r.start <= last.end) last.end = Math.max(last.end, r.end);
    else merged.push({ ...r });
  }
  return merged;
}

/// The range currently playing at `ms` (start inclusive, end exclusive), or null when `ms` falls in a
/// gap, exactly on a range end, or past the last range.
export function rangeAt(ranges: PlayRange[], ms: number): PlayRange | null {
  return ranges.find((r) => ms >= r.start && ms < r.end) ?? null;
}

/// The start (ms) of the first range that begins strictly after `ms` — i.e. where to seek next. Returns
/// null when no later range exists, signalling playback should stop.
export function nextRangeStart(ranges: PlayRange[], ms: number): number | null {
  const next = ranges.find((r) => r.start > ms);
  return next ? next.start : null;
}
