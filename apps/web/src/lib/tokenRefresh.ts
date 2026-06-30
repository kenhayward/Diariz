import { decodeJwtPayload } from "./jwt";

/// How long (ms) until we should silently refresh the access token: a little before it expires, so a long
/// session (e.g. a recording in progress) never lapses. Returns 0 when already within the skew window, or
/// null when the token has no usable `exp` (so the caller schedules nothing).
export function refreshDelayMs(token: string | null, nowMs: number, skewMs = 60_000): number | null {
  const exp = decodeJwtPayload(token)?.["exp"];
  if (typeof exp !== "number") return null;
  return Math.max(0, exp * 1000 - skewMs - nowMs);
}
