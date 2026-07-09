/// Weaving the note-taker's notes into the transcript view: each timed note appears right after the segment
/// that was being spoken when it was written (the "closest time match"), as its own row. Pure so it can be
/// unit-tested; the anchor rule mirrors the backend `TranscriptNoteAnchor` (which uses it to keep same-speaker
/// text either side of a note from merging together).

export type WovenRow<S, N> =
  | { kind: "segment"; seg: S; index: number }
  | { kind: "note"; note: N };

/// The index of the segment a note captured at `capturedMs` attaches after, or -1 for the very top (the note
/// was written before the first segment started). Segment starts are expected in display order.
export function anchorIndex(startsMs: number[], capturedMs: number): number {
  let idx = -1;
  for (let i = 0; i < startsMs.length; i++) if (startsMs[i] <= capturedMs) idx = i;
  return idx;
}

/// Interleave `segments` and `notes` into one ordered list of rows. Only notes with a `capturedAtMs` are
/// woven in (pre-meeting notes with no timestamp aren't part of the timeline); notes sharing an anchor keep
/// chronological order. Each segment row carries its original index so the caller can key playback highlight
/// off the segment position rather than the woven position.
export function weaveTranscript<
  S extends { startMs: number },
  N extends { capturedAtMs: number | null },
>(segments: S[], notes: N[]): WovenRow<S, N>[] {
  const starts = segments.map((s) => s.startMs);
  const byAnchor = new Map<number, N[]>();
  for (const n of notes) {
    if (n.capturedAtMs == null) continue;
    const idx = anchorIndex(starts, n.capturedAtMs);
    (byAnchor.get(idx) ?? byAnchor.set(idx, []).get(idx)!).push(n);
  }
  const sorted = (idx: number) =>
    (byAnchor.get(idx) ?? []).slice().sort((a, b) => (a.capturedAtMs ?? 0) - (b.capturedAtMs ?? 0));

  const rows: WovenRow<S, N>[] = [];
  for (const n of sorted(-1)) rows.push({ kind: "note", note: n });
  segments.forEach((seg, index) => {
    rows.push({ kind: "segment", seg, index });
    for (const n of sorted(index)) rows.push({ kind: "note", note: n });
  });
  return rows;
}
