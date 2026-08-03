import { isSensitiveKey, scrubUrlsIn, scrubDeep, REDACTED } from "./scrub";

/**
 * A short rolling record of what the app just did, attached to a feedback submission.
 *
 * Deliberately NOT built on the error-tracking SDK's breadcrumbs: `beforeBreadcrumb` only fires once
 * the SDK is initialised, and feedback has to work on a deployment with no DSN configured. It is fed
 * instead from seams the app already owns - the axios interceptors and the router.
 *
 * Entries are scrubbed on the way IN rather than on the way out. The buffer therefore never holds a
 * value that would be unsafe to send, so there is no export path that someone can forget to cover.
 * Scrubbing on the way out is how several disclosure paths were introduced in the telemetry work.
 */
export type TrailEntry = {
  at: number;
  kind: "api" | "nav" | "mark";
  label: string;
  detail?: Record<string, unknown>;
};

/** Enough to see the sequence that led to a problem; small enough to post and to read. */
export const TRAIL_CAPACITY = 30;

let buffer: TrailEntry[] = [];

export function record(entry: Omit<TrailEntry, "at">): void {
  const safe: TrailEntry = {
    at: Date.now(),
    kind: entry.kind,
    // The label routinely carries a URL, and a URL routinely carries a credential: @microsoft/signalr
    // puts the JWT in `?access_token=` because a browser cannot set a header on a WS handshake. Labels
    // are free text like "GET /hubs/transcription?access_token=..." (a verb prefix, not a bare URL),
    // so this needs the per-token scrubUrlsIn rather than stripQueryString directly - stripQueryString
    // alone would leave the query string in place because the label doesn't start with "/" or "http".
    label: scrubUrlsIn(String(entry.label ?? "")),
  };
  if (entry.detail) {
    const scrubbed: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(entry.detail)) {
      scrubbed[k] = isSensitiveKey(k) ? REDACTED : scrubDeep(v);
    }
    safe.detail = scrubbed;
  }
  buffer.push(safe);
  if (buffer.length > TRAIL_CAPACITY) buffer = buffer.slice(buffer.length - TRAIL_CAPACITY);
}

/** A copy, so a caller cannot mutate the live buffer. */
export function snapshot(): TrailEntry[] {
  return buffer.map((e) => ({ ...e }));
}

/** Tests only. */
export function clearTrail(): void {
  buffer = [];
}
