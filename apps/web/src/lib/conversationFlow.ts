/// The model behind the transcript's conversation-flow track: the bar under the play button that shows
/// who spoke when, as speaker-coloured spans proportional to their span of the recording, with the gaps
/// between them left as silence. Clicking or dragging the bar seeks, so the track doubles as the scrubber.
///
/// Kept pure and free of any DOM or media element so the layout and seek maths are unit-testable — the
/// component only turns spans into `<span>` widths and pointer positions into a fraction.

type SegmentLike = { speaker: string; startMs: number; endMs: number };

/// One span of the track. `label` is the speaker, or null for a silence gap (including the lead-in
/// before the first segment and the tail after the last).
export interface FlowSpan {
  label: string | null;
  startMs: number;
  endMs: number;
  /// The span's width as a percentage of the whole track, so the spans tile it exactly.
  widthPct: number;
}

/// A speaker's share of the *talk time* (not of the recording — silence is excluded), for the legend.
export interface SpeakerShare {
  label: string;
  pct: number;
}

/// The track laid out left to right: every segment as a span, every gap between them as silence.
/// Consecutive segments from the same speaker merge into one span so a speaker's turn reads as a single
/// block rather than a run of hairline slivers.
export function flowSpans(segments: SegmentLike[], durationMs: number): FlowSpan[] {
  if (durationMs <= 0 || segments.length === 0) return [];

  const sorted = [...segments].sort((a, b) => a.startMs - b.startMs);

  // Merge touching/overlapping same-speaker runs into single turns.
  const turns: { label: string; startMs: number; endMs: number }[] = [];
  for (const s of sorted) {
    const last = turns[turns.length - 1];
    if (last && last.label === s.speaker && s.startMs <= last.endMs) last.endMs = Math.max(last.endMs, s.endMs);
    else turns.push({ label: s.speaker, startMs: s.startMs, endMs: s.endMs });
  }

  const spans: FlowSpan[] = [];
  const push = (label: string | null, startMs: number, endMs: number) => {
    if (endMs <= startMs) return;
    spans.push({ label, startMs, endMs, widthPct: ((endMs - startMs) / durationMs) * 100 });
  };

  let cursor = 0;
  for (const turn of turns) {
    push(null, cursor, turn.startMs); // the silence before this turn
    push(turn.label, turn.startMs, turn.endMs);
    cursor = Math.max(cursor, turn.endMs);
  }
  push(null, cursor, durationMs); // the tail

  return spans;
}

/// Each speaker's percentage of the total talk time, largest first — the track's legend. Shares are of
/// talk time, so they always sum to 100 however much of the recording is silence.
export function speakerShares(segments: SegmentLike[]): SpeakerShare[] {
  const spoken = new Map<string, number>();
  for (const s of segments) {
    spoken.set(s.speaker, (spoken.get(s.speaker) ?? 0) + Math.max(0, s.endMs - s.startMs));
  }

  const total = [...spoken.values()].reduce((sum, ms) => sum + ms, 0);
  if (total <= 0) return [];

  // Ties break on the label so the legend's order is stable across renders rather than depending on the
  // order segments happened to arrive in.
  return [...spoken.entries()]
    .map(([label, ms]) => ({ label, pct: (ms / total) * 100 }))
    .sort((a, b) => b.pct - a.pct || a.label.localeCompare(b.label));
}

/// Where to seek to for a click/drag at `fraction` along the track. Clamped, because a drag that leaves
/// the element still reports a position — past either end it should pin to the start or the end.
export function seekMsFromFraction(fraction: number, durationMs: number): number {
  if (durationMs <= 0) return 0;
  return Math.min(Math.max(fraction, 0), 1) * durationMs;
}
