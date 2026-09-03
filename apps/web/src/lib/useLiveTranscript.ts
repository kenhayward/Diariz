import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { api } from "./api";
import { applyAppend, emptyLiveTranscript, lagSeconds, type LiveTranscript } from "./liveTranscript";

export interface LiveState {
  transcript: LiveTranscript;
  /// The server has stopped transcribing live. Not permanent - it resumes once it has caught up.
  degraded: boolean;
}

export type LiveEvent =
  | { kind: "append"; recordingId: string; sequence: number; segments: LiveTranscript["segments"] }
  | { kind: "degraded"; recordingId: string; sequence: number }
  /// The capture this panel is following has changed - a new meeting started. Not a hub event: it is
  /// raised locally, because nothing on the server knows that this page has moved on.
  | { kind: "recording-changed"; recordingId: string };

/// Fold one hub event into the live state. Pure, so the interesting rules are testable without a hub,
/// a fetch or a component.
///
/// Returns the **same object** when nothing applies. The hub is per user, so a page with one recording
/// open receives events for every other recording that user has running; allocating a new state for
/// each would re-render the panel on somebody else's meeting.
export function nextLiveState(state: LiveState, event: LiveEvent): LiveState {
  // Handled before the recording filter below, because it is the one event that is ABOUT the recording
  // changing - the filter would discard it for naming a different one, which is the whole point of it.
  if (event.kind === "recording-changed") {
    if (event.recordingId === state.transcript.recordingId) return state;
    return { transcript: emptyLiveTranscript(event.recordingId), degraded: false };
  }

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

  // Follow the recording the caller is on. useState only ever seeds from its FIRST argument, so without
  // this the transcript of the meeting before stays on screen under the new meeting's heading until its
  // first chunk lands - which is exactly what happened in a real session.
  useEffect(() => {
    setState((prev) => nextLiveState(prev, { kind: "recording-changed", recordingId: recordingId ?? "" }));
    inFlight.current.clear();
  }, [recordingId]);

  const onAppend = useCallback(
    async (e: { recordingId: string; sequence: number }) => {
      if (!recordingId || e.recordingId !== recordingId) return;
      if (inFlight.current.has(e.sequence)) return;
      inFlight.current.add(e.sequence);
      try {
        // The transcript alone, not the whole recording. `getRecording` also carries the metadata,
        // speakers, action items, calendar link, visible rooms, summary and meeting minutes - a payload
        // that grows all meeting, and this runs every time a chunk lands. The server resolves the
        // speaker name and its suggestion flag, which this used to do here by fetching every speaker
        // and matching labels.
        const live = await api.getLiveTranscript(e.recordingId);
        const segments = live.segments.map((s) => ({
          id: s.id,
          startMs: s.startMs,
          endMs: s.endMs,
          text: s.text,
          sequence: e.sequence,
          // Undefined rather than null, because that is what "no speaker" means to the panel.
          speaker: s.speaker ?? undefined,
          speakerIsSuggestion: s.speakerIsSuggestion || undefined,
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
