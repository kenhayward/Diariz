/// The auto-capture loop: sample the screen on an interval, and file a screenshot each time it settles
/// on something new.
///
/// This owns the orchestration only. Where the frames come from is behind `SlideCaptureFrames`, which is
/// what keeps the loop testable - the real implementation holds a `getDisplayMedia` stream and two
/// canvases (see `displayMediaFrames`), and neither exists in a test environment. What actually needs
/// covering is everything around it: the stamping, the dedupe, the capture limit, and what happens when
/// grabs start failing.
///
/// Runs in the renderer rather than the desktop shell because the per-sample cost of
/// `desktopCapturer.getSources()` is ~430ms whatever size you ask for, against ~12ms for sampling a warm
/// stream (spec §14). The renderer also owns the pause-aware recording clock, which is what lets a
/// capture be stamped with the moment its slide appeared.

import { createDetector, dhash, HASH_SIZE, type SlideDetector } from "./slideDetector";

/// Samples come off a canvas, so they are RGBA. Reading them as BGRA would silently mis-weight the
/// colour channels (see slideDetector's PixelOrder) - stated once, here, so both ends of the hash chain
/// cannot drift apart.
function hashOf(bytes: Uint8ClampedArray): Uint8Array {
  return dhash(bytes, HASH_SIZE + 1, HASH_SIZE, { pixelOrder: "rgba" });
}

/// A full-resolution frame ready to file, plus the hash of that same frame so the detector can confirm
/// the screen did not move on between deciding to grab and grabbing.
export interface CommittedFrame {
  full: Blob;
  thumb: Blob;
  width: number;
  height: number;
  hash: Uint8Array;
}

export interface SlideCaptureFrames {
  /// One detection sample: (HASH_SIZE + 1) x HASH_SIZE RGBA bytes, or null when no frame is available
  /// yet (the stream is still starting). Null is not a failure.
  sample(): Uint8ClampedArray | null;
  /// The current frame at full resolution, or null when it could not be grabbed.
  commit(): Promise<CommittedFrame | null>;
  /// Release the underlying capture. Must be idempotent.
  close(): void;
}

export interface CapturedSlide {
  full: Blob;
  thumb: Blob;
  width: number;
  height: number;
  /// Offset into the recording at which the slide appeared.
  capturedAtMs: number;
}

/// Why the loop stopped on its own. A user-driven stop does not report.
export type StopReason = "cap-reached" | "frames-failed";

export interface SlideCaptureOptions {
  frames: SlideCaptureFrames;
  /// The recorded, pause-aware clock.
  nowMs: () => number;
  /// Whether sampling should be skipped for now - a paused recording. Not a stop: the stream stays open
  /// (it costs ~0.1% of a core) so resuming needs no fresh getDisplayMedia grant, and the detector keeps
  /// its committed slide, so the slide that was on screen before the pause is not filed twice.
  isSuspended?: () => boolean;
  /// Ceiling on captures filed by this loop, mirroring the recording's screenshot limit.
  maxCaptures: number;
  onCapture: (slide: CapturedSlide) => void;
  onStopped: (reason: StopReason) => void;
  sampleIntervalMs?: number;
  detector?: SlideDetector;
}

export interface SlideCapture {
  /// Begin sampling on the interval.
  start(): void;
  /// One sample. Exposed so tests can drive the loop without timers; `start` calls it.
  tick(): Promise<void>;
  /// Stop sampling and release the capture. Idempotent, and reports nothing - a stop the caller asked
  /// for is not news to the caller.
  stop(): void;
}

const DEFAULT_SAMPLE_INTERVAL_MS = 1000;

/// Consecutive failed grabs before the loop gives up. One is a hiccup - a compositor stall, a display
/// mode change - and retrying costs nothing. A run of them means the capture is gone, and continuing
/// would leave the toggle lit over a loop that can never produce anything.
const MAX_CONSECUTIVE_FAILURES = 3;

export function createSlideCapture(options: SlideCaptureOptions): SlideCapture {
  const {
    frames,
    nowMs,
    isSuspended,
    maxCaptures,
    onCapture,
    onStopped,
    sampleIntervalMs = DEFAULT_SAMPLE_INTERVAL_MS,
    detector = createDetector(),
  } = options;

  let timer: ReturnType<typeof setInterval> | null = null;
  let running = true;
  // A grab is far slower than the sample interval on a busy machine, so ticks must not stack: two
  // overlapping grabs would fight over the same frame source and confirm each other's candidates.
  let inFlight = false;
  let captured = 0;
  let consecutiveFailures = 0;

  const halt = (reason: StopReason) => {
    if (!running) return;
    stop();
    onStopped(reason);
  };

  function stop() {
    if (timer !== null) {
      clearInterval(timer);
      timer = null;
    }
    running = false;
    frames.close();
  }

  async function tick(): Promise<void> {
    if (!running || inFlight) return;
    // A paused recording freezes its clock, so a capture taken now would be filed at an offset that never
    // advances - every slide during the pause stacked onto one moment in the transcript - and would be of
    // a screen the user has stepped away from.
    if (isSuspended?.()) return;

    const bytes = frames.sample();
    // No frame yet - the stream is still coming up. Not a failure, and not something to count.
    if (!bytes) return;

    const candidate = detector.observe(hashOf(bytes), nowMs());
    if (!candidate) return;

    inFlight = true;
    try {
      const frame = await frames.commit();
      if (!frame) {
        // Nothing to confirm against, so the candidate is dropped and the next samples will detect the
        // same change again - unless the source has died, which the failure run catches.
        detector.reject();
        if (++consecutiveFailures >= MAX_CONSECUTIVE_FAILURES) halt("frames-failed");
        return;
      }
      consecutiveFailures = 0;

      const outcome = detector.confirm(frame.hash);
      // The screen moved on mid-grab. Filing this image under this timestamp would put the next slide's
      // picture on the previous slide's moment - wrong in a way nothing about it looks wrong.
      if (!outcome.accepted) return;
      // A slide the presenter has already shown. It is now the committed state either way, but a second
      // copy in the transcript is exactly the tidying-up this feature exists to remove.
      if (outcome.duplicate) return;

      onCapture({
        full: frame.full,
        thumb: frame.thumb,
        width: frame.width,
        height: frame.height,
        capturedAtMs: candidate.firstSeenAtMs,
      });

      if (++captured >= maxCaptures) halt("cap-reached");
    } finally {
      inFlight = false;
    }
  }

  return {
    start() {
      if (timer !== null || !running) return;
      timer = setInterval(() => void tick(), sampleIntervalMs);
    },
    tick,
    stop,
  };
}
