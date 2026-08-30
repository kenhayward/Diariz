/// Orchestrates one live capture: begins the recording server-side, asks the recorder for a fragment
/// whenever the chunker says a chunk has ended, queues each fragment durably, and finalises at stop.
///
/// It exists as its own module so `Recorder.tsx` gains a small, readable hook rather than another
/// hundred lines of lifecycle, and so this can be tested without a MediaRecorder.
///
/// The one rule that matters more than the rest: **a live session is optional**. If beginning it
/// fails, `start` returns null and the recorder carries on exactly as it does today - buffer locally,
/// upload at stop. A briefly unreachable server must never cost someone their meeting.

import { advance, emptyChunkerState, shouldCut, DEFAULT_CHUNKER_LIMITS, type ChunkerLimits, type ChunkerState } from "./liveChunker";
import { createChunkQueue, type ChunkQueue, type ChunkStore } from "./liveChunkQueue";

export interface LiveSessionDeps {
  begin: () => Promise<{ id: string; sessionId: string }>;
  upload: (recordingId: string, sessionId: string, chunk: {
    sequence: number; blob: Blob; startMs: number; endMs: number;
  }) => Promise<void>;
  finalize: (recordingId: string) => Promise<void>;
  /// Ask the MediaRecorder to emit everything since the last fragment. The blob arrives later, via
  /// `offerFragment` - `requestData` is fire-and-forget by design.
  requestFragment: () => void;
  store: ChunkStore;
  limits?: ChunkerLimits;
  /// Reported when a chunk upload keeps failing, so the UI can say the recording is not yet safe.
  onTrouble?: (message: string) => void;
}

export interface LiveSession {
  readonly recordingId: string;
  /// One level reading from the recorder's meter. Fires `requestFragment` when a chunk has ended.
  tick(dtMs: number, level: number, paused: boolean): void;
  /// A fragment the recorder produced. Spans the recorded-clock range since the previous one.
  offerFragment(blob: Blob, atRecordedMs: number): Promise<void>;
  /// Finish: queue whatever is left, drain, then ask the server to concatenate.
  finish(atRecordedMs: number, tail?: Blob): Promise<void>;
  /// Sequences still held locally - none means everything reached the server.
  pending(): Promise<number[]>;
}

export async function startLiveSession(deps: LiveSessionDeps): Promise<LiveSession | null> {
  let begun: { id: string; sessionId: string };
  try {
    begun = await deps.begin();
  } catch {
    // The whole point of returning null rather than throwing: the caller falls back to today's
    // behaviour and the user never learns anything went wrong.
    return null;
  }

  const limits = deps.limits ?? DEFAULT_CHUNKER_LIMITS;
  const queue: ChunkQueue = createChunkQueue({
    store: deps.store,
    upload: (chunk) => deps.upload(begun.id, begun.sessionId, chunk),
  });

  let chunker: ChunkerState = emptyChunkerState();
  let chunkStartMs = 0;
  // Guards against asking for a second fragment before the first has arrived, which would produce
  // two chunks covering the same span.
  let awaitingFragment = false;

  const drain = async () => {
    try {
      await queue.drain();
    } catch {
      deps.onTrouble?.("Some audio has not reached the server yet.");
    }
  };

  return {
    recordingId: begun.id,

    tick(dtMs, level, paused) {
      chunker = advance(chunker, { dtMs, level, paused }, limits);
      if (awaitingFragment || !shouldCut(chunker, limits)) return;
      awaitingFragment = true;
      chunker = emptyChunkerState();
      deps.requestFragment();
    },

    async offerFragment(blob, atRecordedMs) {
      awaitingFragment = false;
      if (blob.size === 0) return;
      await queue.enqueue(blob, chunkStartMs, atRecordedMs);
      chunkStartMs = atRecordedMs;
      void drain();
    },

    async finish(atRecordedMs, tail) {
      if (tail && tail.size > 0) {
        await queue.enqueue(tail, chunkStartMs, atRecordedMs);
        chunkStartMs = atRecordedMs;
      }
      await drain();
      await deps.finalize(begun.id);
    },

    pending: () => queue.pendingSequences(),
  };
}
