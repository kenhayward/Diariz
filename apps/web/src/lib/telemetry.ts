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

// Named imports (rather than `import * as Sentry`) so a bundler can tree-shake the integrations this
// app never enables (session replay, user feedback, tracing) - referencing the whole namespace object
// as a value (e.g. as a default parameter) defeats that, and pulls in ~150 kB gzipped of unused code.
import { init as sentryInit, captureException as sentryCaptureException, type ErrorEvent, type Breadcrumb } from "@sentry/react";

/** Recursively redact sensitive values. Pure: returns a new structure, never mutates the input. */
function scrub<T>(value: T): T {
  if (Array.isArray(value)) return value.map(scrub) as unknown as T;
  if (value && typeof value === "object") {
    const out: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(value as Record<string, unknown>)) {
      out[k] = isSensitiveKey(k) ? REDACTED : scrub(v);
    }
    return out as unknown as T;
  }
  return value;
}

/**
 * Event hook. Redacts by field name AND strips URL query strings, because the two catch different
 * things: the deny-list cannot reach a query string, and URL stripping cannot reach a named field.
 */
export function beforeSend(event: ErrorEvent): ErrorEvent | null {
  const cleaned = scrub(event);
  if (cleaned.request?.url) cleaned.request.url = stripQueryString(cleaned.request.url);
  return cleaned;
}

/**
 * Breadcrumb hook.
 *
 * Two distinct jobs. First, fetch/xhr breadcrumbs record the request URL, which for the SignalR hub
 * carries `?access_token=<JWT>` - so every breadcrumb URL is stripped.
 *
 * Second, console breadcrumbs capture whatever was logged, which a key-name deny-list cannot vet
 * because the content arrives as a formatted message rather than a named field. Rather than guess at
 * what every console call in the app might contain, low-level console breadcrumbs are dropped
 * entirely; warn and error are kept, since those are the ones with diagnostic value and are written
 * deliberately.
 */
export function beforeBreadcrumb(crumb: Breadcrumb): Breadcrumb | null {
  if (crumb.category === "console" && !["warning", "error", "fatal"].includes(crumb.level ?? "")) {
    return null;
  }
  const cleaned = scrub(crumb);
  if (typeof cleaned.data?.url === "string") cleaned.data.url = stripQueryString(cleaned.data.url);
  if (typeof cleaned.message === "string") cleaned.message = scrubUrlsIn(cleaned.message);
  return cleaned;
}

/** Injected so tests can assert the options without loading the real SDK. */
interface SentryLike {
  init: (options: Record<string, unknown>) => void;
}

let enabled = false;

/**
 * Fetch the browser DSN from the API and start reporting. Resolves to whether reporting is on.
 *
 * Uses raw fetch rather than the axios client in lib/api: this runs before the app boots, and the
 * axios instance carries auth interceptors (including a 401 -> /login redirect) that have no business
 * firing for a config read.
 *
 * Never throws and never blocks the app: if the config request fails, the SPA boots with telemetry
 * off. The cost of fetching rather than baking the DSN is that errors thrown in the first few
 * milliseconds of boot are missed - accepted, because a baked DSN would force dev and production to
 * share one error-tracking project.
 */
export async function initTelemetry(sdk: SentryLike = { init: sentryInit }): Promise<boolean> {
  try {
    const res = await fetch("/api/config");
    const cfg = (await res.json()) as { sentryDsn?: string; sentryEnvironment?: string };
    const dsn = (cfg.sentryDsn ?? "").trim();
    if (!dsn) return false;

    sdk.init({
      dsn,
      environment: cfg.sentryEnvironment || "development",
      release: __APP_VERSION__,
      // Never attach request bodies, headers or user identifiers automatically.
      sendDefaultPii: false,
      // GlitchTip does not support sessions.
      autoSessionTracking: false,
      beforeSend,
      beforeBreadcrumb,
    });
    enabled = true;
    return true;
  } catch {
    // Telemetry must never stop the app booting.
    return false;
  }
}

/** Report an error the app caught and handled. A no-op when reporting is off. */
export function captureException(error: unknown, context?: Record<string, unknown>): void {
  if (!enabled) return;
  sentryCaptureException(error, context ? { extra: scrub(context) } : undefined);
}
