/// Weaving the note-taker's notes into the transcript view: each timed note appears right after the segment
/// that was being spoken when it was written (the "closest time match"), as its own row. Pure so it can be
/// unit-tested; the anchor rule mirrors the backend `TranscriptNoteAnchor` (which uses it to keep same-speaker
/// text either side of a note from merging together).

/// The index of the segment a note captured at `capturedMs` attaches after, or -1 for the very top (the note
/// was written before the first segment started). Segment starts are expected in display order.
export function anchorIndex(startsMs: number[], capturedMs: number): number {
  let idx = -1;
  for (let i = 0; i < startsMs.length; i++) if (startsMs[i] <= capturedMs) idx = i;
  return idx;
}

/// `P` defaults to `never` so the two-argument call site (no screenshots) narrows to just `segment` | `note`
/// after excluding `"note"` - the conditional drops the `screenshot` branch entirely rather than leaving it
/// in the union with an uninhabited `shot: never`, which would still block narrowing to `segment`.
export type WovenRow<S, N, P = never> =
  | { kind: "segment"; seg: S; index: number }
  | { kind: "note"; note: N }
  | ([P] extends [never] ? never : { kind: "screenshot"; shot: P });

/// Unconditional shape of a woven row, used internally while building the list. `WovenRow` prunes the
/// `screenshot` branch entirely when `P` is `never` (see above), which the compiler can't see through while
/// `P` is still a live type parameter inside this function body - so we build with this plain union and cast
/// to `WovenRow` at the return, once `P` is resolved to a concrete type (or `never`) at the call site.
type Row<S, N, P> =
  | { kind: "segment"; seg: S; index: number }
  | { kind: "note"; note: N }
  | { kind: "screenshot"; shot: P };

/// Interleave `segments` with the timed items captured during the meeting - the note-taker's notes and any
/// screenshots - into one ordered list of rows. Only notes with a `capturedAtMs` are woven in (pre-meeting
/// notes have no place on the timeline); screenshots always have one. Items sharing an anchor are ordered
/// by capture time regardless of kind, so a note and a screenshot taken seconds apart read in the order
/// they happened. Each segment row carries its original index so the caller can key playback highlight off
/// the segment position rather than the woven position.
export function weaveTranscript<
  S extends { startMs: number },
  N extends { capturedAtMs: number | null },
  P extends { capturedAtMs: number } = never,
>(segments: S[], notes: N[], shots: P[] = []): WovenRow<S, N, P>[] {
  const starts = segments.map((s) => s.startMs);

  type Timed = { at: number; row: Row<S, N, P> };
  const byAnchor = new Map<number, Timed[]>();
  const add = (at: number, row: Row<S, N, P>) => {
    const idx = anchorIndex(starts, at);
    const bucket = byAnchor.get(idx);
    if (bucket) bucket.push({ at, row });
    else byAnchor.set(idx, [{ at, row }]);
  };

  for (const note of notes) {
    if (note.capturedAtMs == null) continue;
    add(note.capturedAtMs, { kind: "note", note });
  }
  for (const shot of shots) add(shot.capturedAtMs, { kind: "screenshot", shot });

  const sorted = (idx: number) =>
    (byAnchor.get(idx) ?? [])
      .slice()
      .sort((a, b) => a.at - b.at)
      .map((t) => t.row);

  const rows: Row<S, N, P>[] = [];
  for (const row of sorted(-1)) rows.push(row);
  segments.forEach((seg, index) => {
    rows.push({ kind: "segment", seg, index });
    for (const row of sorted(index)) rows.push(row);
  });
  return rows as WovenRow<S, N, P>[];
}
