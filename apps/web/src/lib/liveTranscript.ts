/// The transcript of a meeting still in progress, as the page holds it.
///
/// Pure: it folds append events into an ordered list and answers how far behind the text is. No
/// SignalR, no fetching, no React - the component owns those and hands the events here.
///
/// Two properties are worth stating because they are what the tests pin, and both come from the same
/// fact: **an append can arrive more than once, and out of order.** Redis streams are at-least-once on
/// the server side, and the hub can re-push; a model that simply concatenated would show a sentence
/// twice in the middle of a transcript, which reads as a transcription fault rather than a delivery one.
///
/// Segments carry a speaker from phase 3 on. The raw diarization label is meaningless across chunks - the
/// server stitches each chunk's voices onto one label per meeting first, and what arrives here is already
/// that stitched identity, named where the voice matches somebody enrolled.

export interface LiveSegment {
  id: string;
  startMs: number;
  endMs: number;
  text: string;
  /// The chunk this segment came from. Replacement is keyed on it, not on segment ids: a re-transcribe
  /// can split or merge lines, and matching by id would strand the ones that no longer exist.
  sequence: number;
  /// Who is speaking: a person's name where the voice was recognised, otherwise the meeting-stable label
  /// the server minted. Undefined only for a transcript recorded before speakers were stitched.
  speaker?: string;
  /// The server is asking rather than asserting - close enough to suggest, not close enough to apply. It
  /// is shown differently so a guess is legible as a guess.
  speakerIsSuggestion?: boolean;
}

export interface LiveTranscript {
  recordingId: string;
  segments: LiveSegment[];
  highestSequence: number;
}

export interface LiveAppend {
  recordingId: string;
  sequence: number;
  segments: LiveSegment[];
}

export function emptyLiveTranscript(recordingId: string): LiveTranscript {
  return { recordingId, segments: [], highestSequence: -1 };
}

/// Fold one append into the transcript. Idempotent per sequence, and order-independent.
/// "UNKNOWN" is the pipeline's label for speech nobody was attributed to - what every segment carries
/// when live diarization is switched off for a GPU that cannot keep up. Rendered literally it would put
/// the word above every line and read as somebody's name, which is worse than the blank the panel
/// already handles. Normalised here rather than in the component so it holds wherever segments enter.
function withoutUnknownSpeaker(s: LiveSegment): LiveSegment {
  return s.speaker && s.speaker !== "UNKNOWN" ? s : { ...s, speaker: undefined };
}

export function applyAppend(state: LiveTranscript, append: LiveAppend): LiveTranscript {
  // The hub is per user, not per recording, so events for another meeting reach this page too - a
  // running capture and a recording being read at the same time is an ordinary thing to do.
  if (append.recordingId !== state.recordingId) return state;

  // Drop everything previously seen for this chunk, then take what arrived. A redelivery is then a
  // no-op and a correction replaces cleanly, however many lines it now has.
  const kept = state.segments.filter((s) => s.sequence !== append.sequence);
  const merged = [...kept, ...append.segments.map(withoutUnknownSpeaker)].sort(
    (a, b) => a.startMs - b.startMs || a.endMs - b.endMs,
  );

  return {
    recordingId: state.recordingId,
    segments: merged,
    highestSequence: Math.max(state.highestSequence, append.sequence),
  };
}

/// How far behind the meeting the transcript is, in whole seconds, for the status line.
///
/// Clamped at zero. An overlapping decode window can produce a segment ending marginally past a clock
/// value read a moment earlier, and "-2s behind" on screen would be nonsense.
export function lagSeconds(transcribedToMs: number | null, recordedMs: number): number {
  if (transcribedToMs === null) return 0;
  const behind = (recordedMs - transcribedToMs) / 1000;
  return behind <= 0 ? 0 : Math.round(behind);
}
