// Redaction rules for anything sent to the error tracker.
//
// SYNC OBLIGATION: this deny-list exists in three places and they must stay in step:
//   - src/Diariz.Worker/telemetry.py
//   - src/Diariz.Api/Services/SentryScrubber.cs
//   - this file
// Each has a test asserting the shared set below. Those tests catch a REMOVAL from one copy, but
// they cannot catch an ADDITION made to only one copy - if you add an entry here, add it to the
// other two and to their tests.

export const REDACTED = "[redacted]";

// Exact field names that carry meeting content.
const DENY_EXACT = new Set([
  "text", "transcript", "transcription", "segments", "words", "summary",
  "minutes", "note", "notes", "content", "authorization", "cookie", "cookies",
  // ECAPA voiceprint vectors (biometric data identifying a speaker by voice).
  "embedding", "embeddings",
]);

// Substrings marking a credential regardless of the surrounding name.
const DENY_SUBSTRING = ["secret", "token", "password", "api_key", "apikey", "access_key", "accesskey"];

/** True when a field with this name must never leave the browser. */
export function isSensitiveKey(key: string): boolean {
  const lowered = String(key).toLowerCase();
  if (DENY_EXACT.has(lowered)) return true;
  return DENY_SUBSTRING.some((marker) => lowered.includes(marker));
}

/**
 * Drop a URL's query string, keeping its path.
 *
 * Load-bearing, not cosmetic: @microsoft/signalr appends `?access_token=<JWT>` to the hub's negotiate
 * and WebSocket URLs, because a browser cannot set an Authorization header on a WS handshake. A
 * key-name deny-list cannot reach that - a query string is one opaque value, not a named field. The
 * path is kept because it is diagnostically useful and carries no credential.
 *
 * Anything that is not a URL is returned unchanged.
 */
export function stripQueryString(value: string): string {
  const cut = value.indexOf("?");
  if (cut === -1) return value;
  // Only treat it as a URL if what precedes the "?" looks like one; otherwise leave free text alone.
  const head = value.slice(0, cut);
  const isUrl = /^https?:\/\//i.test(head) || head.startsWith("/");
  return isUrl ? head : value;
}

/** Apply stripQueryString to every whitespace-separated token, for descriptions like "GET <url>". */
export function scrubUrlsIn(text: string): string {
  return text.split(" ").map(stripQueryString).join(" ");
}
