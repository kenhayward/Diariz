/// Decides, once per session, how this browser must be asked for chunks.
///
/// `MediaRecorder.start(timeslice)` emits fragments where only the first carries the WebM/EBML header
/// and Opus codec-private data. Byte-joining them back together is the cheap option - the canonical
/// audio stays exactly what a single recording would have been - but it depends on the browser's
/// muxer, not on anything the spec promises.
///
/// The S0 spike (docs/Streaming_Capture_and_Live_Transcript.md section 5.1 findings) verified that
/// joining works on Chromium 148 and Electron 43, and could not test Firefox or Safari. Rather than
/// assume, this probes at runtime: record two short fragments, join them, try to decode. A browser
/// that fails falls back to stopping and restarting the recorder per chunk, which is self-contained
/// by construction and is already in production for dictation.
///
/// It never rejects. An unknown browser degrades to the slower option; it does not lose the meeting.

export type ChunkFraming =
  /// `start(timeslice)`: fragments are joined, and the canonical audio is byte-identical.
  | "fragments"
  /// Stop and restart per chunk: each blob is self-contained, at the cost of a few dropped
  /// milliseconds per restart.
  | "restart";

export interface FramingProbeDeps {
  /// Record a very short clip with a timeslice, returning the fragments it produced.
  recordFragments: () => Promise<Blob[]>;
  /// Attempt to decode. Must throw or reject if the audio is not decodable.
  decode: (blob: Blob) => Promise<unknown>;
}

// Cached for the session: the probe costs about a second of recording, and the answer cannot change
// while the page is open. The in-flight promise is cached too, so two callers racing at start-up
// produce one probe rather than two.
let cached: ChunkFraming | null = null;
let inFlight: Promise<ChunkFraming> | null = null;

/// Test seam. Nothing in the app calls this.
export function resetFramingProbe(): void {
  cached = null;
  inFlight = null;
}

export async function probeFraming(deps: FramingProbeDeps): Promise<ChunkFraming> {
  if (cached) return cached;
  if (inFlight) return inFlight;

  inFlight = (async () => {
    try {
      const fragments = await deps.recordFragments();
      // Fewer than two means the browser ignored the timeslice, so there is no join to trust.
      if (fragments.length < 2) return "restart";

      // Decode the JOIN, not a fragment. A lone fragment after the first fails in every browser -
      // that is the whole problem - so probing one would always answer "restart".
      await deps.decode(new Blob(fragments, { type: fragments[0].type || "audio/webm" }));
      return "fragments";
    } catch {
      return "restart";
    }
  })();

  try {
    cached = await inFlight;
    return cached;
  } finally {
    inFlight = null;
  }
}
