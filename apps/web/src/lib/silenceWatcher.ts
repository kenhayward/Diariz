// Web Audio shell for the calendar-event "stop on silence" rule. The decision logic is pure and lives in
// calendarRecording.ts; this only owns the AudioContext/AnalyserNode and the polling loop.
//
// Deliberately independent of HubLevelMeter, which computes the same RMS for the on-screen bars: that meter
// is mounted by the record hub and unmounts while the recording is paused or the popover is closed, so
// hanging an auto-stop off it would make the recording's fate depend on what the user happens to have open.
// This one lives and dies with the recording itself.

import { rms, normalizeLevel } from "./audioLevel";
import { idleSilence, nextSilenceState, shouldStopForSilence, type SilenceState } from "./calendarRecording";

export interface SilenceWatcher {
  /// Stop counting while the recording is paused. Pausing disables the capture track, so the analyser reads
  /// pure silence - without this, pausing a meeting for five minutes would end the recording.
  setPaused(paused: boolean): void;
  /// The current silence run, so a caller can ask whether anyone is talking *now* rather than only being told
  /// when the run reaches its threshold. Read by the extend prompt.
  state(): SilenceState;
  /// Tear down the analyser and context. Safe to call more than once.
  stop(): void;
}

/// Poll interval. Silence is measured in tens of seconds, so this is deliberately far coarser than the
/// meter's rAF loop - it costs nothing and keeps working when the tab is backgrounded (where rAF stalls,
/// which for an auto-stop would mean never firing).
const TICK_MS = 500;

type AudioContextCtor = typeof AudioContext;

/**
 * Watch `stream` and call `onSilent` once it has been continuously near-silent for `thresholdMs`, having
 * first heard something. Fires at most once. Returns null when Web Audio is unavailable (jsdom, older
 * browsers), which means the take simply has no silence auto-stop rather than failing to record.
 *
 * A non-positive `thresholdMs` means "never auto-stop", not "stop looking": `shouldStopForSilence` already
 * refuses to fire on one, and that user is precisely the one whose extend prompt has no silence floor under
 * it - so the room still has to be observable through `state()`. Watching costs one analyser read every
 * half-second.
 */
export function startSilenceWatcher(
  stream: MediaStream,
  thresholdMs: number,
  onSilent: () => void,
  /// Every level reading, for a caller that needs to know how loud the room is rather than only when
  /// it has been quiet for long enough. Live capture drives its chunk boundaries off this.
  ///
  /// Deliberately hung here rather than off HubLevelMeter: that meter runs on rAF, which stalls in a
  /// backgrounded tab - and a chunker that stops while the tab is in the background would stop
  /// chunking for most of a meeting. This loop is a plain interval and keeps running. It also
  /// reports while paused (with `paused: true`) rather than going silent, so a chunker can tell "no
  /// audio because paused" from "no audio because quiet".
  onLevel?: (level: number, dtMs: number, paused: boolean) => void,
): SilenceWatcher | null {
  const w = window as unknown as { AudioContext?: AudioContextCtor; webkitAudioContext?: AudioContextCtor };
  const Ctx = w.AudioContext ?? w.webkitAudioContext;
  if (!Ctx) return null;

  let ctx: AudioContext;
  let source: MediaStreamAudioSourceNode;
  let analyser: AnalyserNode;
  try {
    ctx = new Ctx();
    source = ctx.createMediaStreamSource(stream);
    analyser = ctx.createAnalyser();
    analyser.fftSize = 256;
    analyser.smoothingTimeConstant = 0.6;
    source.connect(analyser); // tap only - never connect to ctx.destination, that would echo the mic
  } catch {
    // A stream with no audio track, or a context the browser refuses to open. No auto-stop, no crash.
    return null;
  }

  const time = new Uint8Array(analyser.fftSize);
  let state: SilenceState = idleSilence();
  let paused = false;
  let fired = false;
  let timer: number | null = window.setInterval(() => {
    if (paused) {
      onLevel?.(0, TICK_MS, true);
      return;
    }
    if (fired) return;
    analyser.getByteTimeDomainData(time);
    const level = normalizeLevel(rms(time));
    onLevel?.(level, TICK_MS, false);
    state = nextSilenceState(state, level, TICK_MS);
    if (shouldStopForSilence(state, thresholdMs)) {
      fired = true;
      onSilent();
    }
  }, TICK_MS);

  const teardown = () => {
    if (timer !== null) {
      window.clearInterval(timer);
      timer = null;
    }
    try {
      source.disconnect();
      void ctx.close();
    } catch {
      // Already closed (double stop, or the page is tearing down) - nothing to do.
    }
  };

  return {
    setPaused(next: boolean) {
      paused = next;
    },
    state: () => state,
    stop: teardown,
  };
}
