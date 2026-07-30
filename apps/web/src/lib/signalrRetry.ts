/// The reconnect schedule for the transcription hub.
///
/// `withAutomaticReconnect()` with no arguments uses the library default of [0, 2s, 10s, 30s] and then
/// **stops permanently**. That is roughly 42 seconds of trying - shorter than an API container restart,
/// which runs migrations and seeders before it listens (the compose healthcheck allows 60s). So an
/// ordinary redeploy left the hub dead for the rest of the session: live status updates stopped arriving,
/// and nothing on screen said so.

/// The interval the ladder settles at. Long enough that a permanently-down server costs nothing, short
/// enough that a recovered one is noticed quickly.
export const RECONNECT_MAX_DELAY_MS = 60_000;

const LADDER_MS = [0, 2_000, 5_000, 10_000, 20_000, 30_000, 45_000] as const;

/// Delay before reconnect attempt `previousRetryCount` (0-based). **Never returns null**, so the client
/// keeps trying indefinitely - a hub that has given up is indistinguishable, from the user's side, from
/// an app that has quietly stopped working.
export function reconnectDelayMs(previousRetryCount: number): number {
  const i = Math.max(previousRetryCount, 0);
  return i < LADDER_MS.length ? LADDER_MS[i] : RECONNECT_MAX_DELAY_MS;
}
