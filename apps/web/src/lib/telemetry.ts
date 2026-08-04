import { REDACTED, isSensitiveKey, stripQueryString, scrubUrlsIn, scrubDeep, isWalkable, CIRCULAR, MAX_DEPTH } from "./scrub";

// Local alias: the rest of this file calls the walker `scrub`.
const scrub = scrubDeep;

// Re-exported so any existing importer of this file still resolves; the definitions themselves now
// live in ./scrub - see the SYNC OBLIGATION header there.
export { REDACTED, isSensitiveKey, stripQueryString, scrubUrlsIn, CIRCULAR };

// Named imports (rather than `import * as Sentry`) so a bundler can tree-shake the integrations this
// app never enables (session replay, user feedback, tracing) - referencing the whole namespace object
// as a value (e.g. as a default parameter) defeats that, and pulls in ~150 kB gzipped of unused code.
import {
  init as sentryInit,
  captureException as sentryCaptureException,
  browserTracingIntegration,
  type BrowserOptions,
  type ErrorEvent,
  type Breadcrumb,
} from "@sentry/react";

// @sentry/react does not publicly export a `TransactionEvent` type - only @sentry/core does, and that
// package is a transitive dependency here, not a direct one, so importing from it would be fragile.
//
// This type exists to do a job, not just to satisfy tsc: it names every field beforeSendTransaction
// must handle (request.url, spans[].description, spans[].data, contexts.trace.data) so that adding a
// new field this hook needs to scrub means widening the type, which is a visible, reviewable change -
// instead of a silently-untyped field a hook can quietly skip. A first cut of this type omitted
// `spans[].data`, which is exactly how the span-attribute JWT leak (fixed alongside this comment)
// shipped without tsc raising a signal. scrub() below is still generic and will preserve any field not
// named here, but the point of this type is to keep that list honest.
interface TransactionEventLike {
  request?: { url?: string; headers?: Record<string, string>; [key: string]: unknown };
  spans?: Array<{ description?: string; data?: Record<string, unknown> | null; [key: string]: unknown }>;
  contexts?: { trace?: { data?: Record<string, unknown> | null; [key: string]: unknown }; [key: string]: unknown };
  [key: string]: unknown;
}

/**
 * Scrub a bag of values: span/trace-context attributes (Sentry's `data`, i.e. span attributes),
 * breadcrumb `data`, and request headers. All three are the same shape and all three carry URLs under
 * key names this code does not get to choose.
 *
 * RECURSIVE, deliberately. A top-level-only pass left real leaks in nested structure - most concretely
 * `data.arguments`, which breadcrumbs.js's `_getConsoleBreadcrumbHandler` sets to the raw console call
 * arguments (`data: { arguments: handlerData.args, logger: "console" }`). `warn`/`error` console
 * breadcrumbs are deliberately KEPT, so a URL passed to `console.error` sat one array level down and
 * sailed through untouched. Both rules below therefore apply at EVERY depth, through objects and
 * arrays alike.
 *
 * Auto-instrumented fetch/xhr spans (@sentry/core's getFetchSpanAttributes, @sentry/browser's
 * xhrCallback) put the FULL unsanitized request URL on attributes like `url`, `http.url` and
 * `url.full`, and the raw query string alone on `http.query` - none of this is touched by the SDK's
 * own sanitizer, which only cleans the span's name/description. The same attributes can also land on
 * `contexts.trace.data` when the root span is itself an http.client span.
 *
 * Two rules, chosen to survive the SDK adding a new attribute rather than tracking today's exact set:
 *   - A key that is exactly "query" or ends in ".query" (matches `http.query`, and any future
 *     `*.query` attribute) holds a raw query string as its ENTIRE value - there is nothing before the
 *     "?", so stripQueryString's "does the head look like a URL" check fails and it passes the value
 *     through unchanged (verified: stripQueryString("?access_token=x") returns "?access_token=x").
 *     So this is a value the whole key is redacted, not stripped.
 *   - Every other string value gets stripQueryString applied. That function only touches a string
 *     whose head looks like a URL (`http(s)://...` or a leading "/"), so it is a safe no-op for
 *     methods, status codes, and any other non-URL attribute - and unlike hardcoding "url"/"http.url"/
 *     "url.full", it also catches whatever URL-shaped attribute name the SDK adds next.
 *
 * `null`/non-object input passes through unchanged rather than throwing - `data` can legitimately be
 * absent or null, and this runs inside a beforeSend / beforeSendTransaction / beforeBreadcrumb hook
 * where a throw would drop the whole event from the send pipeline.
 *
 * `seen` tracks the CURRENT PATH (added on the way down, removed on the way back up), so a genuine
 * cycle becomes a `[circular]` marker while an object merely referenced twice as siblings is still
 * scrubbed both times. See the note on `scrub` for why the guard has to exist.
 */
function scrubAttributes<T>(data: T, seen: WeakSet<object> = new WeakSet(), depth = 0): T {
  if (typeof data === "string") return stripQueryString(data) as unknown as T;
  if (!isWalkable(data)) return data;
  if (seen.has(data) || depth >= MAX_DEPTH) return CIRCULAR as unknown as T;
  seen.add(data);
  try {
    if (Array.isArray(data)) {
      return data.map((item) => scrubAttributes(item, seen, depth + 1)) as unknown as T;
    }
    const out: Record<string, unknown> = {};
    for (const [key, value] of Object.entries(data as Record<string, unknown>)) {
      out[key] = /(^|\.)query$/i.test(key) ? REDACTED : scrubAttributes(value, seen, depth + 1);
    }
    return out as T;
  } finally {
    seen.delete(data);
  }
}

/**
 * Scrub an event's `request`: the URL, and the header bag.
 *
 * The headers matter as much as the URL. httpContextIntegration is a DEFAULT integration and its
 * `preprocessEvent` copies the browser's request data onto `event.request`, including a `Referer`
 * header holding the FULL previous URL - which on this app is `/setup?token=<activation credential>`
 * or the whole OAuth authorize query whenever the user has just come from one of those pages. The
 * deny-list cannot reach it (the value is a URL, not a named field) and it is not `request.url`.
 * Every string header goes through the same rule rather than naming `Referer`, so the next
 * URL-valued header the SDK adds is covered on the day it appears.
 */
function scrubRequest(request: { url?: string; headers?: Record<string, string> } | undefined): void {
  if (!request) return;
  if (request.url) request.url = stripQueryString(request.url);
  if (request.headers) request.headers = scrubAttributes(request.headers);
}

/**
 * Event hook. Redacts by field name AND strips URL query strings, because the two catch different
 * things: the deny-list cannot reach a query string, and URL stripping cannot reach a named field.
 *
 * `exception.values[].value` and `message` are free text that routinely quotes a URL ("Request failed:
 * GET /hubs/transcription?access_token=..."), so they get the same word-by-word treatment the
 * breadcrumb message and span descriptions already get.
 */
export function beforeSend(event: ErrorEvent): ErrorEvent | null {
  const cleaned = scrub(event);
  scrubRequest(cleaned.request);
  for (const value of cleaned.exception?.values ?? []) {
    if (typeof value.value === "string") value.value = scrubUrlsIn(value.value);
  }
  if (typeof cleaned.message === "string") cleaned.message = scrubUrlsIn(cleaned.message);
  return cleaned;
}

/**
 * Breadcrumb hook.
 *
 * Two distinct jobs. First, breadcrumbs record URLs under key names this code does not choose, so
 * EVERY string value in `data` is stripped, not just `data.url`. A `data.url`-only rule was a real
 * leak: breadcrumbsIntegration is a DEFAULT integration and its history handler
 * (@sentry/browser integrations/breadcrumbs.js, `_getHistoryBreadcrumbHandler`) emits
 * `{ category: "navigation", data: { from, to } }` where each value is `parseUrl(...).relative` -
 * path PLUS query PLUS hash. Neither key is named "url" and neither is a deny-listed field name, so
 * `/setup?token=<activation credential>`, the OAuth authorize query, and `/login?returnTo=<encoded
 * authorize URL>` all rode straight through. The rule is deliberately "every string value" rather
 * than "also `from` and `to`", for the same reason the span-attribute scrub is a rule and not a list:
 * the next integration will invent another URL-valued key.
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
  if (cleaned.data) cleaned.data = scrubAttributes(cleaned.data);
  if (typeof cleaned.message === "string") cleaned.message = scrubUrlsIn(cleaned.message);
  return cleaned;
}

/**
 * Transaction hook. Separate from beforeSend because in this SDK (@sentry/react 10.69.0, verified
 * against @sentry/core's client.js `processBeforeSend`) beforeSend is only invoked for error events
 * (`isErrorEvent(event) && beforeSend`) - transaction events go through beforeSendTransaction instead.
 * The .NET API shipped a JWT leak on exactly that gap, found only in a whole-branch review.
 * Transactions fire on every navigation and request, so this is the higher-volume path of the two.
 */
export function beforeSendTransaction(event: TransactionEventLike): TransactionEventLike | null {
  const cleaned = scrub(event);
  // httpContextIntegration's preprocessEvent runs for transactions too, so the Referer header lands
  // here as well - same treatment.
  scrubRequest(cleaned.request);
  for (const span of cleaned.spans ?? []) {
    if (typeof span.description === "string") span.description = scrubUrlsIn(span.description);
    if (span.data) span.data = scrubAttributes(span.data);
  }
  if (cleaned.contexts?.trace?.data) {
    cleaned.contexts.trace.data = scrubAttributes(cleaned.contexts.trace.data);
  }
  return cleaned;
}

/**
 * The exact option set this app passes to the SDK, checked against the SDK's OWN option type.
 *
 * This was `Record<string, unknown>`, which meant tsc never looked at these options at all - and that
 * is precisely how `autoSessionTracking: false` survived here for a release even though no such option
 * exists anywhere in @sentry/* 10.69.0 (zero occurrences of the name in the installed packages). It is
 * the same failure mode as the untyped transaction event that hid the span-attribute leak.
 *
 * Everything but one field is `Pick`ed from the real `BrowserOptions`, so an option that is misspelled,
 * removed by an SDK upgrade, or simply invented is a compile error (an object literal gets excess
 * property checking). `beforeSendTransaction` is declared here rather than Pick'ed because it must
 * accept `TransactionEventLike` - see the comment on that type for why this file cannot use the SDK's
 * own `TransactionEvent`.
 */
type TelemetryOptions = Pick<
  BrowserOptions,
  "dsn" | "environment" | "release" | "sendDefaultPii" | "integrations" | "tracesSampleRate" | "beforeSend" | "beforeBreadcrumb"
> & {
  beforeSendTransaction: typeof beforeSendTransaction;
};

/** Injected so tests can assert the options without loading the real SDK. */
interface SentryLike {
  init: (options: TelemetryOptions) => void;
}

/**
 * The real SDK behind the SentryLike seam.
 *
 * The cast covers exactly one deliberate, documented difference: `beforeSendTransaction` is declared
 * over `TransactionEventLike` rather than the SDK's `TransactionEvent`, which @sentry/react does not
 * export (see the comment on that type). Every OTHER option in TelemetryOptions is `Pick`ed straight
 * from `BrowserOptions`, so this cast buys back none of the checking the finding was about - a
 * misspelled or non-existent option is still a compile error at the call site below.
 */
const realSdk: SentryLike = {
  init: (options) => void sentryInit(options as unknown as BrowserOptions),
};

let enabled = false;

/**
 * How long the boot config read may take before the app gives up and renders anyway.
 *
 * `/api/config` is a same-origin read of a handful of fields, so 2 s is generous. The bound exists
 * because `main.tsx` gates the FIRST RENDER on this promise: without it an API that accepts the
 * connection and then hangs leaves a blank page up for as long as the proxy allows (nginx's default
 * is 60 s), where the app would previously have rendered its login screen immediately. Telemetry must
 * never noticeably delay the app.
 */
export const CONFIG_TIMEOUT_MS = 2000;

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
export async function initTelemetry(sdk: SentryLike = realSdk): Promise<boolean> {
  const controller = new AbortController();
  let timer: ReturnType<typeof setTimeout> | undefined;
  try {
    // Belt and braces: the signal aborts the request, and the race guarantees this function settles
    // even if the fetch implementation ignores the signal. main.tsx renders off the settled promise,
    // so "settles" is the property that has to hold, not "aborts".
    const timedOut = new Promise<null>((resolve) => {
      timer = setTimeout(() => {
        controller.abort();
        resolve(null);
      }, CONFIG_TIMEOUT_MS);
    });
    const res = await Promise.race([fetch("/api/config", { signal: controller.signal }), timedOut]);
    if (!res) return false;

    const cfg = (await res.json()) as {
      sentryDsn?: string;
      sentryEnvironment?: string;
      sentryTracesSampleRate?: number;
    };
    const dsn = (cfg.sentryDsn ?? "").trim();
    if (!dsn) return false;

    sdk.init({
      dsn,
      environment: cfg.sentryEnvironment || "development",
      release: __APP_VERSION__,
      // Never attach request bodies, headers or user identifiers automatically.
      sendDefaultPii: false,
      // GlitchTip does not support sessions, so browserSessionIntegration is removed. It is a DEFAULT
      // integration, and getIntegrationsToSetup (@sentry/core integration.js) MERGES a supplied ARRAY
      // with the defaults - only the function form gets to drop one. (`autoSessionTracking: false`,
      // which this used to pass, is not an option this SDK has at all.)
      integrations: (defaults) => [
        ...defaults.filter((integration) => integration.name !== "BrowserSession"),
        browserTracingIntegration(),
      ],
      tracesSampleRate: cfg.sentryTracesSampleRate ?? 1,
      beforeSend,
      beforeBreadcrumb,
      beforeSendTransaction,
    });
    enabled = true;
    return true;
  } catch {
    // Telemetry must never stop the app booting.
    return false;
  } finally {
    clearTimeout(timer);
  }
}

/** Report an error the app caught and handled. A no-op when reporting is off. */
export function captureException(error: unknown, context?: Record<string, unknown>): void {
  if (!enabled) return;
  sentryCaptureException(error, context ? { extra: scrub(context) } : undefined);
}
