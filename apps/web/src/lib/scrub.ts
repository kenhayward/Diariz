// Redaction rules for anything sent to the error tracker.
//
// SYNC OBLIGATION: this deny-list exists in three places and they must stay in step:
//   - src/Diariz.Worker/telemetry.py
//   - src/Diariz.Api/Services/SentryScrubber.cs
//   - this file
// Each has a test asserting the shared set below. Those tests catch a REMOVAL from one copy, but
// they cannot catch an ADDITION made to only one copy - if you add an entry here, add it to the
// other two and to their tests.

// Imports NOTHING. That is the point: apps/web/src/lib/trail.ts needs these rules and must work with
// the error-tracking SDK absent, and telemetry.ts statically imports @sentry/react. ES imports are
// hoisted, so a trail that imported from telemetry.ts would drag the SDK in regardless of DSN.

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
 * Anything that is not a URL is returned unchanged. `null`/`undefined` coerce to `""` (matching
 * isSensitiveKey's String() coercion above) rather than throwing - Task 3 wires this to Sentry
 * fields typed `string | undefined`, and a throw here would happen inside a beforeSend hook, taking
 * the whole scrub pipeline down at exactly the moment something has already gone wrong.
 */
export function stripQueryString(value: string): string {
  const raw = String(value ?? "");
  const cut = raw.indexOf("?");
  if (cut === -1) return raw;
  // Only treat it as a URL if what precedes the "?" looks like one; otherwise leave free text alone.
  const head = raw.slice(0, cut);
  const isUrl = /^https?:\/\//i.test(head) || head.startsWith("/");
  return isUrl ? head : raw;
}

/**
 * Apply stripQueryString to every whitespace-separated token, for descriptions like "GET <url>".
 * `null`/`undefined` coerce to `""` for the same reason as stripQueryString above.
 */
export function scrubUrlsIn(text: string): string {
  return String(text ?? "").split(" ").map(stripQueryString).join(" ");
}

/** True for a value a recursive walk should descend into (object or array, but not null). */
export function isWalkable(value: unknown): value is object {
  return !!value && typeof value === "object";
}

export const CIRCULAR = "[circular]";

/**
 * Depth ceiling for both recursive walks that use it (this module's `scrubDeep`, and
 * `scrubAttributes` in telemetry.ts, which imports this constant rather than keeping its own copy).
 *
 * The `seen` set already makes cycles impossible, so this is purely a stack-overflow backstop for a
 * pathologically deep (but acyclic) structure - these walks run inside error hooks, where a
 * `RangeError` would drop the event and, on the breadcrumb path, throw from inside the SDK's own
 * handler. 20 is far past anything real: the SDK's own `normalizeDepth` default is 3, so it flattens
 * everything below that itself before an event is serialised.
 */
export const MAX_DEPTH = 20;

/**
 * Recursively redact sensitive values by field name. Pure: returns a new structure, never mutates the
 * input.
 *
 * Cycle-guarded for the same reason `scrubAttributes` is, and it is not theoretical: before the guard
 * existed, handing `beforeBreadcrumb` a self-referencing `data` object threw
 * `RangeError: Maximum call stack size exceeded` out of THIS function - breadcrumb data is arbitrary
 * app-supplied structure, and a throw from inside a breadcrumb hook is worse than the leak the
 * recursion was added to close, because it takes the page with it.
 */
export function scrubDeep<T>(value: T, seen: WeakSet<object> = new WeakSet(), depth = 0): T {
  if (!isWalkable(value)) return value;
  if (seen.has(value) || depth >= MAX_DEPTH) return CIRCULAR as unknown as T;
  seen.add(value);
  try {
    if (Array.isArray(value)) {
      return value.map((item) => scrubDeep(item, seen, depth + 1)) as unknown as T;
    }
    const out: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(value as Record<string, unknown>)) {
      out[k] = isSensitiveKey(k) ? REDACTED : scrubDeep(v, seen, depth + 1);
    }
    return out as unknown as T;
  } finally {
    seen.delete(value);
  }
}
