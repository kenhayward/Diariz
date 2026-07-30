import { decodeJwtPayload } from "./jwt";

/// How long (ms) until we should silently refresh the access token: a little before it expires, so a long
/// session (e.g. a recording in progress) never lapses. Returns 0 when already within the skew window, or
/// null when the token has no usable `exp` (so the caller schedules nothing).
export function refreshDelayMs(token: string | null, nowMs: number, skewMs = 60_000): number | null {
  const exp = decodeJwtPayload(token)?.["exp"];
  if (typeof exp !== "number") return null;
  return Math.max(0, exp * 1000 - skewMs - nowMs);
}

/// How long to wait before re-attempting a refresh that failed.
///
/// A refresh can fail for a reason that has nothing to do with the session - the API restarting during a
/// redeploy, a dropped connection, a sleeping laptop. Without a retry, one such failure inside the 60s
/// pre-expiry window ends the sliding session: the token then lapses, and Stop on a recording in progress
/// returns 401, which redirects to /login and unmounts the recorder. The audio survives (it is stashed
/// before upload), but the user is thrown out of a meeting for a blip.
export const REFRESH_RETRY_DELAYS_MS = [5_000, 15_000, 45_000, 120_000] as const;

/// The delay for retry number `attempt` (0-based). **Never returns null**: the ladder holds at its final
/// interval instead of giving up, because the token outlives it several times over (120 minutes against a
/// few minutes of retries), so stopping early would strand a session a slightly longer outage would have
/// saved. Retrying a dead server every two minutes costs nothing.
export function refreshRetryDelayMs(attempt: number): number {
  const i = Math.min(Math.max(attempt, 0), REFRESH_RETRY_DELAYS_MS.length - 1);
  return REFRESH_RETRY_DELAYS_MS[i];
}
