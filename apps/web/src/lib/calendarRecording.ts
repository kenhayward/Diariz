// Pure rules for a recording started from a calendar event: when it should end by itself, and how silence
// is tracked towards that. Kept free of timers and Web Audio (mirroring recorderSchedule.ts / audioLevel.ts)
// so every edge here is unit-testable; Recorder.tsx and silenceWatcher.ts own the shells.

import { SILENCE_LEVEL } from "./audioLevel";

/// The user's Preferences -> Recordings settings, as they apply to one take.
export interface CalendarRecordingSettings {
  /// Master switch. While false the recording runs until the user stops it, exactly as before.
  enabled: boolean;
  /// Minutes to keep recording past the invite's end time.
  afterMinutes: number;
  /// Seconds of continuous silence that also ends the recording.
  silenceSeconds: number;
}

// Mirrors the server-side defaults; a value that survives to here non-positive is a bug or a hand-edited
// API call, and stopping instantly would be a far worse answer than the default.
const DEFAULT_AFTER_MINUTES = 3;

/**
 * The absolute moment a calendar-started recording should stop, or null when it should run until stopped
 * by hand (auto-stop off, or an event with no usable end time - an all-day entry, say).
 *
 * Deliberately never returns a time already past: joining a meeting after its scheduled end is normal
 * (it overran, or you were late), and the naive `end + N` would then be behind `startedAtMs` and stop the
 * take on the schedule watcher's first tick. In that case the same wait is measured from the start instead,
 * so "record 3 minutes past the end" still gives you three minutes of recording.
 */
export function resolveCalendarStopAt(
  endsAt: string | null | undefined,
  settings: CalendarRecordingSettings,
  startedAtMs: number,
): number | null {
  if (!settings.enabled) return null;
  if (!endsAt) return null;
  const end = Date.parse(endsAt);
  if (Number.isNaN(end)) return null;

  const minutes = settings.afterMinutes > 0 ? settings.afterMinutes : DEFAULT_AFTER_MINUTES;
  const waitMs = minutes * 60_000;
  const fromEvent = end + waitMs;
  return fromEvent > startedAtMs ? fromEvent : startedAtMs + waitMs;
}

/// Whichever of two stop targets comes first, ignoring nulls. The user's own auto-stop choice and the
/// calendar event's end are independent answers to "when does this finish"; the earlier one wins.
export function earlierStop(a: number | null, b: number | null): number | null {
  if (a === null) return b;
  if (b === null) return a;
  return Math.min(a, b);
}

/// How much uninterrupted silence has accumulated, and whether anything has been heard at all.
export interface SilenceState {
  /// True once the input has risen above the silence threshold at least once in this recording.
  heardSound: boolean;
  /// Milliseconds of continuous near-silence since the last sound. Reset to 0 whenever sound returns.
  silentMs: number;
}

export const idleSilence = (): SilenceState => ({ heardSound: false, silentMs: 0 });

/**
 * Fold one meter reading into the silence run.
 *
 * The `heardSound` gate is the point of this being a state machine rather than a running total: a take
 * started before anyone speaks (you join early, or the host is still setting up) is silent from the outset,
 * and a bare accumulator would end it after `silenceSeconds` without a word ever being recorded. Silence
 * only counts as "the meeting broke up" once there was a meeting to break up.
 */
export function nextSilenceState(prev: SilenceState, level: number, dtMs: number): SilenceState {
  if (level >= SILENCE_LEVEL) return { heardSound: true, silentMs: 0 };
  if (!prev.heardSound) return prev;
  return { heardSound: true, silentMs: prev.silentMs + dtMs };
}

/// Whether the silence run has reached the user's threshold. A non-positive threshold never stops (the
/// setting is off or nonsensical), matching how the durations are clamped everywhere else.
export function shouldStopForSilence(state: SilenceState, thresholdMs: number): boolean {
  return thresholdMs > 0 && state.heardSound && state.silentMs >= thresholdMs;
}
