/// Which segments a voiceprint's stored spans select, and what spans a set of ticked segments becomes.
///
/// Mirrors the server's `VoiceprintSpans` (Coverage / FromSegments). Deliberately narrower: the UI only
/// ever ticks **whole segments**, so a stored span set is always a union of segment ranges and "selected"
/// collapses to "fully covered" - there is no partial state to render, only one that can appear after a
/// re-transcribe moved the boundaries under an older selection. Ticking anything rewrites the spans from
/// the current boundaries, so that state is transient and self-healing.
///
/// The two sides can only disagree about a selection the UI could not have made, which is why this is a
/// small parallel implementation rather than a round-trip per segment.

export interface Span {
  startMs: number;
  endMs: number;
}

/// Sorted, zero-length dropped, and overlapping or touching spans collapsed.
function merge(spans: readonly Span[]): Span[] {
  const out: Span[] = [];
  for (const s of [...spans].filter((s) => s.endMs > s.startMs).sort((a, b) => a.startMs - b.startMs || a.endMs - b.endMs)) {
    const last = out[out.length - 1];
    if (last && s.startMs <= last.endMs) last.endMs = Math.max(last.endMs, s.endMs);
    // Bare spans, not a spread: callers pass whole segments, and their text and ids have no business in
    // a request body that only describes time.
    else out.push({ startMs: s.startMs, endMs: s.endMs });
  }
  return out;
}

/// True when every millisecond of `segment` is inside the selection.
///
/// **No spans means the whole speaker** - the state every sample enrolled before selection existed is in,
/// so an untouched voiceprint shows everything ticked rather than nothing.
export function isSelected(segment: Span, spans: readonly Span[]): boolean {
  if (spans.length === 0) return true;

  // Walk the merged spans, consuming the segment left to right. Merging first keeps overlapping picks
  // from sending the cursor backwards.
  let cursor = segment.startMs;
  for (const s of merge(spans)) {
    if (s.endMs <= cursor) continue;
    if (s.startMs > cursor) return false; // a gap the selection does not cover
    cursor = s.endMs;
    if (cursor >= segment.endMs) return true;
  }
  return false;
}

/// The audio a set of ticked segments occupies, ready to send. The server merges again on receipt; doing
/// it here keeps a long selection from travelling as hundreds of one-line spans.
export function spansForSegments(segments: readonly Span[]): Span[] {
  return merge(segments);
}
