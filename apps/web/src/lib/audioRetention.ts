/// How long a recording's audio has left before the nightly auto-delete job removes it. Drives the amber
/// "16d left" note on the hub's audio chip, which is the only warning a user gets that the audio (and so
/// re-transcribe) is about to go away.

const DAY_MS = 24 * 60 * 60 * 1000;

/// Whole days until the audio is deleted, or null when nothing is scheduled — auto-delete is off, the
/// owner protected the audio, or it is already gone. Rounds up, so the final hours still read "1d left"
/// rather than "0d left"; a date that has already passed reads 0 (the job simply hasn't run yet).
export function retentionDaysLeft(scheduledDeletionAt: string | null, now: Date): number | null {
  if (!scheduledDeletionAt) return null;
  const ms = new Date(scheduledDeletionAt).getTime() - now.getTime();
  return ms <= 0 ? 0 : Math.ceil(ms / DAY_MS);
}
