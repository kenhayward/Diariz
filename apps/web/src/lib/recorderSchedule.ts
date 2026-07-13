/// Pure schedule math for the recorder's auto-stop control (kept out of Recorder.tsx so it can be unit-tested
/// without MediaRecorder/timers, like recorderTiming.ts). The Recorder holds the chosen option; this resolves
/// it to an absolute stop time and answers "stop now?".

export type AutoStopChoice = "off" | "in15" | "in30" | "in60" | "at";

export const RELATIVE_MINUTES: Record<"in15" | "in30" | "in60", number> = { in15: 15, in30: 30, in60: 60 };

/// Parse a local "HH:MM" into today's epoch ms, or null when blank/malformed. Does not roll to tomorrow - the
/// caller (resolveStopAt) requires the result to be in the future.
export function parseTimeToday(input: string, now: number): number | null {
  const m = /^(\d{1,2}):(\d{2})$/.exec(input.trim());
  if (!m) return null;
  const h = Number(m[1]);
  const min = Number(m[2]);
  if (h > 23 || min > 59) return null;
  const d = new Date(now);
  d.setHours(h, min, 0, 0);
  return d.getTime();
}

/// The absolute stop target for a choice, or null when off / unresolved / not in the future.
/// `anchorMs` is the base for relative choices (record-start when set before recording, else the moment of
/// selection). Ignored for "at".
export function resolveStopAt(
  choice: AutoStopChoice,
  timeInput: string,
  anchorMs: number,
  now: number,
): number | null {
  if (choice === "off") return null;
  if (choice === "at") {
    const at = parseTimeToday(timeInput, now);
    return at != null && at > now ? at : null;
  }
  return anchorMs + RELATIVE_MINUTES[choice] * 60_000;
}

/// Whether recording should auto-stop now.
export function shouldStop(stopAt: number | null, now: number): boolean {
  return stopAt != null && now >= stopAt;
}
