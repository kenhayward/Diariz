/// Tracks *recorded* elapsed time for a recording that can be paused, so the on-screen timer and the
/// uploaded duration reflect captured audio only — never the wall-clock time spent paused. Pure and
/// deterministic (the caller supplies `now`), so it's unit-testable without timers.

export interface Timing {
  /// Milliseconds accumulated from completed (already-run) segments.
  accumulatedMs: number;
  /// Timestamp the current running segment started, or null while paused/stopped.
  runningSince: number | null;
}

/// Begin timing at `now` (a fresh recording).
export function start(now: number): Timing {
  return { accumulatedMs: 0, runningSince: now };
}

/// Fold the running segment into the accumulated total and stop the clock.
export function pause(t: Timing, now: number): Timing {
  if (t.runningSince === null) return t;
  return { accumulatedMs: t.accumulatedMs + (now - t.runningSince), runningSince: null };
}

/// Restart the clock for a new segment.
export function resume(t: Timing, now: number): Timing {
  return t.runningSince === null ? { ...t, runningSince: now } : t;
}

/// Total recorded milliseconds so far (accumulated + the current running segment, if any).
export function elapsedMs(t: Timing, now: number): number {
  return t.accumulatedMs + (t.runningSince === null ? 0 : Math.max(0, now - t.runningSince));
}
