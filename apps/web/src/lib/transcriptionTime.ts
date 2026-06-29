import { formatDuration } from "./format";

/// Split a processing duration (ms) into whole **days** plus the remaining **h:mm:ss / m:ss** clock
/// (reusing `formatDuration`, whose leading unit has no zero-prefix). Drives the account-menu total
/// ("Transcription d days hh:mm:ss" — the days part is dropped when zero) and the detail subtitle.
export function transcriptionTimeParts(ms: number): { days: number; clock: string } {
  const totalSeconds = Math.max(0, Math.round(ms / 1000));
  const days = Math.floor(totalSeconds / 86400);
  const remainderMs = (totalSeconds - days * 86400) * 1000;
  return { days, clock: formatDuration(remainderMs) };
}
