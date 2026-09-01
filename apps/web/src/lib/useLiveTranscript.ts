import { useCallback, useMemo, useRef, useState } from "react";
import { api } from "./api";
import { applyAppend, emptyLiveTranscript, lagSeconds, type LiveTranscript } from "./liveTranscript";

export interface LiveState {
  transcript: LiveTranscript;
  /// The server has stopped transcribing live. Not permanent - it resumes once it has caught up.
  degraded: boolean;
}

export type LiveEvent =
  | { kind: "append"; recordingId: string; sequence: number; segments: LiveTranscript["segments"] }
  | { kind: "degraded"; recordingId: string; sequence: number };

/// Fold one hub event into the live state. Pure, so the interesting rules are testable without a hub,
/// a fetch or a component.
///
/// Returns the **same object** when nothing applies. The hub is per user, so a page with one recording
/// open receives events for every other recording that user has running; allocating a new state for
/// each would re-render the panel on somebody else's meeting.
export function nextLiveState(state: LiveState, event: LiveEvent): LiveState {
  if (event.recordingId !== state.transcript.recordingId) return state;

  if (event.kind === "degraded") {
    return state.degraded ? state : { ...state, degraded: true };
  }

  return {
    transcript: applyAppend(state.transcript, {
      recordingId: event.recordingId,
      sequence: event.sequence,
      segments: event.segments,
    }),
    // Text arriving means the server is transcribing again. A status line stuck on "paused" while
    // lines visibly appear would be worse than having none.
    degraded: false,
  };
}

/// The live transcript for one recording, kept up to date from hub events.
///
/// The hub carries ids rather than text, so an append is a signal to refetch: one event shape then
/// serves an append, a correction, and later a relabel, without the server having to decide which of
/// those it is sending.
export function useLiveTranscript(recordingId: string | null, recordedMs: () => number) {
  const [state, setState] = useState<LiveState>(() => ({
    transcript: emptyLiveTranscript(recordingId ?? ""),
    degraded: false,
  }));

  // Guards against two events for the same chunk racing each other's fetch, where the slower response
  // would overwrite the newer one.
  const inFlight = useRef<Set<number>>(new Set());

  const onAppend = useCallback(
    async (e: { recordingId: string; sequence: number }) => {
      if (!recordingId || e.recordingId !== recordingId) return;
      if (inFlight.current.has(e.sequence)) return;
      inFlight.current.add(e.sequence);
      try {
        const detail = await api.getRecording(e.recordingId);
        const segments = (detail.current?.segments ?? []).map((s) => ({
          id: s.id,
          startMs: s.startMs,
          endMs: s.endMs,
          text: s.revised ?? s.original,
          sequence: e.sequence,
        }));
        // The fetch returns the WHOLE transcript, so it replaces rather than appends - which also
        // makes a missed event self-healing: the next one that lands repairs the gap.
        setState((prev) => ({
          transcript: {
            recordingId: e.recordingId,
            segments,
            highestSequence: Math.max(prev.transcript.highestSequence, e.sequence),
          },
          degraded: false,
        }));
      } catch {
        // A failed refetch leaves the text as it was. The next event repairs it, and the final
        // transcript arrives regardless.
      } finally {
        inFlight.current.delete(e.sequence);
      }
    },
    [recordingId],
  );

  const onDegraded = useCallback(
    (e: { recordingId: string; sequence: number }) =>
      setState((prev) => nextLiveState(prev, { kind: "degraded", ...e })),
    [],
  );

  const lag = useMemo(() => {
    const last = state.transcript.segments.at(-1);
    return lagSeconds(last ? last.endMs : null, recordedMs());
    // recordedMs is a live clock: recomputed whenever the transcript changes, which is the only moment
    // the number can meaningfully move.
  }, [state.transcript, recordedMs]);

  return {
    transcript: recordingId ? state.transcript : null,
    degraded: state.degraded,
    lagSeconds: lag,
    onAppend,
    onDegraded,
  };
}
