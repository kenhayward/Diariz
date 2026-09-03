/// The live notes panel's single timeline: the user's own note lines, the screen captures and the live
/// transcript merged into one list, ordered by when each thing happened.
///
/// Pure, and deliberately so. Everything a row needs in order to render - its kind, its stamp, whether a
/// transcript line prints its speaker's name - is decided here, which keeps the components dumb and puts
/// the rules that are actually interesting somewhere they can be tested without React, a media stream or
/// a hub connection.
///
/// **Recompute, never append.** `useLiveTranscript` replaces its segment array wholesale on every append
/// (the hub carries ids, so an append is a signal to refetch the whole transcript), so a caller that
/// tried to push new rows onto a previous result would duplicate corrected lines and strand deleted
/// ones. Callers `useMemo` this on `[lines, shots, segments, filter]`.

import type { LiveSegment } from "./liveTranscript";
import type { MeetingNote, ShotView } from "./types";

export type StreamFilter = "all" | "notes" | "captures";

export type StreamItem =
  | {
      kind: "transcript";
      id: string;
      atMs: number;
      segment: LiveSegment;
      /// Whether this line prints its speaker's name: true only where the speaker changed. Decided
      /// against the previous *transcript* segment, not the previous row - see `buildStream`.
      showSpeaker: boolean;
    }
  | { kind: "note"; id: string; atMs: number; note: MeetingNote }
  | { kind: "capture"; id: string; atMs: number; shot: ShotView };

export interface StreamInput {
  lines: MeetingNote[];
  shots: ShotView[];
  segments: LiveSegment[];
  filter: StreamFilter;
}

/// Merge the three sources into one timeline.
///
/// Ordering is a single stable sort on `atMs` over a deliberately ordered concatenation, which buys two
/// rules at once. Ties resolve transcript, then note, then capture - so a note written about a sentence
/// reads *after* the sentence rather than above it - and within one kind the source order survives, so
/// two notes filed in the same second stay in the order they were typed.
export function buildStream({ lines, shots, segments, filter }: StreamInput): StreamItem[] {
  const transcript: StreamItem[] =
    filter === "all"
      ? segments.map((segment, i) => ({
          kind: "transcript",
          // Prefixed by kind: segment ids come from the server and note/capture ids are minted in the
          // browser, so nothing guarantees they cannot collide - and two rows sharing a React key drops
          // one of them with no error anywhere.
          id: `t:${segment.id}`,
          atMs: segment.startMs,
          segment,
          // Compared with the previous SEGMENT rather than the previous row. A note or a capture landing
          // mid-turn must not make the speaker introduce themselves again.
          showSpeaker: Boolean(segment.speaker) && segment.speaker !== segments[i - 1]?.speaker,
        }))
      : [];

  const notes: StreamItem[] =
    filter === "all" || filter === "notes"
      ? lines.map((note) => ({
          kind: "note",
          id: `n:${note.id}`,
          // A line adopted from a pre-meeting stash has no recorded moment. It belongs at the top - it
          // was written before anything else in the list - and `null` would sort nowhere sensible.
          atMs: note.capturedAtMs ?? 0,
          note,
        }))
      : [];

  // Split out ahead of the sort rather than given a sentinel stamp. An adopted line shows 0:00 like any
  // other zero-stamped row, so it cannot be ordered by its `atMs`; putting it first in the input is what
  // a stable sort then honours, and nothing in a recording starts before zero to displace it.
  const adopted = notes.filter((n) => n.kind === "note" && n.note.capturedAtMs === null);
  const stamped = notes.filter((n) => n.kind === "note" && n.note.capturedAtMs !== null);

  const captures: StreamItem[] =
    filter === "all" || filter === "captures"
      ? shots.map((shot) => ({ kind: "capture", id: `c:${shot.id}`, atMs: shot.capturedAtMs, shot }))
      : [];

  return [...adopted, ...transcript, ...stamped, ...captures].sort((a, b) => a.atMs - b.atMs);
}

/// What the filter chips count. Always the whole meeting, never the filtered view: a "Notes 4" chip
/// that read "Notes 0" while Captures was selected would be telling the user their notes had gone.
export function streamCounts({ lines, shots }: { lines: MeetingNote[]; shots: ShotView[] }): {
  notes: number;
  captures: number;
} {
  return { notes: lines.length, captures: shots.length };
}

/// How wide the stamp column has to be, in px, for the meeting's current length.
///
/// `formatDuration` renders `m:ss` under an hour and `h:mm:ss` at or over it, so a column sized for
/// "59:59" clips the moment the clock rolls over. Driven off the elapsed clock rather than measured per
/// row, so every stamp in the list stays in one column instead of the older rows jumping when the newer
/// ones grow.
export function stampColumnPx(elapsedMs: number): 34 | 50 {
  return elapsedMs >= 3_600_000 ? 50 : 34;
}
