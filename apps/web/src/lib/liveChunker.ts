/// Decides where one live-capture chunk ends and the next begins. Pure: no MediaRecorder, no
/// AudioContext, no timers - the recorder feeds it the level readings it already computes for the
/// input meter, and acts on the answer.
///
/// The shape of the decision comes from measurement rather than taste (see
/// docs/Streaming_Capture_and_Live_Transcript.md section 3):
///
///   - Cutting at a natural pause rather than on a fixed clock keeps most chunks from starting or
///     ending mid-word, which is where Whisper produces noise.
///   - A minimum length exists because speaker clustering needs something to cluster. Clips under
///     about 20 s in the benchmark contained a single voice, which made them look far cheaper than a
///     real chunk - the cost step is where the second speaker appears, not at a duration.
///   - A maximum exists because somebody monologuing must not produce one unbounded chunk. The point
///     of the feature is that audio reaches the server while the meeting is still running.

import { nextSilenceMs, SILENCE_LEVEL } from "./audioLevel";

export interface ChunkerLimits {
  /// Shortest chunk a pause may end. Below this the diarizer has too little to work with.
  minMs: number;
  /// Longest chunk, cut at regardless of what the audio is doing.
  maxMs: number;
  /// How long the input must stay near-silent to count as a boundary rather than a gap between words.
  pauseMs: number;
}

/// The fallback limits, used when the server does not send its own (an older API). The server's values
/// win - see `LiveRecording.chunkLimits` - so these can be retuned without a deploy of the web app.
///
/// A word spoken at the START of a chunk cannot leave the browser until that chunk closes, so `maxMs` is
/// the dominant term in live latency: everything downstream - upload, queue, GPU, delivery - measured at
/// well under ten seconds combined, against a chunk that used to run up to 45 s.
///
/// They were 20 s / 45 s, chosen so pyannote had enough audio to cluster speakers in. That is a real
/// cost of shortening them: less to cluster means speakers churn more early in a meeting and are
/// corrected retroactively more often. It is worth paying because it is the transcript people read, and
/// the stitcher already relabels backwards when two voices turn out to be one.
export const DEFAULT_CHUNKER_LIMITS: ChunkerLimits = {
  minMs: 6_000,
  maxMs: 12_000,
  // Shorter than the recorder's 15 s silence hint and longer than the gap between words. The
  // dictation engine uses 800 ms for the same job; kept a little tighter here because a chunk
  // boundary is cheaper to get slightly wrong than a dictated utterance.
  pauseMs: 700,
};

export interface ChunkerState {
  /// Recorded-clock milliseconds since this chunk began. Pause-aware, because the caller passes 0
  /// for dtMs while paused - a wall-clock chunker would fire straight through a long pause and emit
  /// nothing but silence.
  elapsedMs: number;
  /// Consecutive near-silent milliseconds, reset the moment sound returns.
  silentMs: number;
  /// Whether any real speech has been heard in this chunk. A capture that opens on silence must not
  /// emit an empty chunk the moment it reaches the minimum.
  heardSpeech: boolean;
  /// Whether the recorder is currently paused.
  paused: boolean;
}

export interface ChunkerInput {
  dtMs: number;
  /// Normalised meter level, 0..1 - the same value the input meter renders.
  level: number;
  paused: boolean;
}

export function emptyChunkerState(): ChunkerState {
  return { elapsedMs: 0, silentMs: 0, heardSpeech: false, paused: false };
}

/// Fold one tick into the state. Never decides anything - `shouldCut` does.
export function advance(
  state: ChunkerState,
  input: ChunkerInput,
  _limits: ChunkerLimits = DEFAULT_CHUNKER_LIMITS,
): ChunkerState {
  if (input.paused) {
    // Freeze everything. Silence accumulated while paused is not a boundary, it is an absence.
    return { ...state, paused: true };
  }
  return {
    elapsedMs: state.elapsedMs + input.dtMs,
    silentMs: nextSilenceMs(state.silentMs, input.level, input.dtMs),
    heardSpeech: state.heardSpeech || input.level >= SILENCE_LEVEL,
    paused: false,
  };
}

/// Whether the chunk that produced `state` should be closed now.
export function shouldCut(
  state: ChunkerState,
  limits: ChunkerLimits = DEFAULT_CHUNKER_LIMITS,
): boolean {
  if (state.paused) return false;
  if (!state.heardSpeech) return false;
  if (state.elapsedMs >= limits.maxMs) return true;
  return state.elapsedMs >= limits.minMs && state.silentMs >= limits.pauseMs;
}

/// Choose the limits to chunk with: the server's, when they make sense, otherwise the caller's own.
///
/// The server sends them so the latency/diarization trade-off can be retuned deployment-wide without a
/// web deploy. It checks rather than trusts because those values come from a configuration file
/// somebody can typo, and the failure modes are not graceful: a maximum at or below the minimum means
/// the pause rule can never fire before the forced cut, and a non-positive maximum means every tick
/// cuts - a chunk uploaded per animation frame.
///
/// All three or none. A half-applied set would be a configuration nobody chose, and harder to recognise
/// from the outside than either end of it.
export function resolveChunkerLimits(
  fromServer: { minMs: number; maxMs: number; pauseMs: number } | null | undefined,
  fallback: ChunkerLimits = DEFAULT_CHUNKER_LIMITS,
): ChunkerLimits {
  if (!fromServer) return fallback;
  const { minMs, maxMs, pauseMs } = fromServer;
  const usable =
    [minMs, maxMs, pauseMs].every((v) => Number.isFinite(v) && v > 0) && maxMs > minMs;
  return usable ? { minMs, maxMs, pauseMs } : fallback;
}
