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

/// The three row shapes a weave can produce, named individually so the segment/note pair can be reused
/// on its own (see the 2-argument overload below) without dragging the screenshot variant along.
type SegmentRow<S> = { kind: "segment"; seg: S; index: number };
type NoteRow<N> = { kind: "note"; note: N };
type ScreenshotRow<P> = { kind: "screenshot"; shot: P };

/// A woven row. Consumers that discriminate on `kind` (e.g. `row.kind === "screenshot" ? row.shot.id : ...`)
/// should get this type from whichever `weaveTranscript` overload they called rather than naming `P`
/// themselves - the 2-argument overload never produces a `screenshot` row, so its result type omits that
/// variant entirely.
export type WovenRow<S, N, P = never> = SegmentRow<S> | NoteRow<N> | ScreenshotRow<P>;

/// Interleave `segments` with the timed items captured during the meeting - the note-taker's notes and any
/// screenshots - into one ordered list of rows. Only notes with a `capturedAtMs` are woven in (pre-meeting
/// notes have no place on the timeline); screenshots always have one. Items sharing an anchor are ordered
/// by capture time regardless of kind, so a note and a screenshot taken seconds apart read in the order
/// they happened. Each segment row carries its original index so the caller can key playback highlight off
/// the segment position rather than the woven position.
///
/// Overloaded on argument count rather than given a single `shots?: P[]` parameter: callers that never pass
/// screenshots (the 2-argument overload) get a return type that's just `segment | note`, so a plain
/// `row.kind === "note" ? ... : row.seg` still narrows cleanly. Callers that do pass screenshots (the
/// 3-argument overload) get the full `segment | note | screenshot` union with `shot` typed as `P`. A single
/// implementation signature below backs both.
export function weaveTranscript<S extends { startMs: number }, N extends { capturedAtMs: number | null }>(
  segments: S[],
  notes: N[],
): Array<SegmentRow<S> | NoteRow<N>>;
export function weaveTranscript<
  S extends { startMs: number },
  N extends { capturedAtMs: number | null },
  P extends { capturedAtMs: number },
>(segments: S[], notes: N[], shots: P[]): WovenRow<S, N, P>[];
export function weaveTranscript<
  S extends { startMs: number },
  N extends { capturedAtMs: number | null },
  P extends { capturedAtMs: number },
>(segments: S[], notes: N[], shots: P[] = []): WovenRow<S, N, P>[] {
  const starts = segments.map((s) => s.startMs);

  type Timed = { at: number; row: WovenRow<S, N, P> };
  const byAnchor = new Map<number, Timed[]>();
  const add = (at: number, row: WovenRow<S, N, P>) => {
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

  const rows: WovenRow<S, N, P>[] = [];
  for (const row of sorted(-1)) rows.push(row);
  segments.forEach((seg, index) => {
    rows.push({ kind: "segment", seg, index });
    for (const row of sorted(index)) rows.push(row);
  });
  return rows;
}
