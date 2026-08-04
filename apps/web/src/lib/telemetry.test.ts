import { describe, it, expect, vi, afterEach } from "vitest";
import { REDACTED } from "./telemetry";
import { beforeSend, beforeBreadcrumb, beforeSendTransaction, initTelemetry, CONFIG_TIMEOUT_MS } from "./telemetry";

describe("beforeSend", () => {
  it("strips the access token from the page URL", () => {
    const event = { request: { url: "https://app.example/rooms/1?access_token=A_LIVE_JWT" } } as any;

    const cleaned = beforeSend(event)!;

    expect(JSON.stringify(cleaned)).not.toContain("A_LIVE_JWT");
    expect(cleaned.request.url).toBe("https://app.example/rooms/1");
  });

  it("redacts sensitive keys in extra and tags, keeping identifiers", () => {
    const event = {
      extra: { transcript: "the confidential meeting content", recordingId: "rid-1" },
      tags: { authorization: "Bearer abc", model: "large-v3" },
    } as any;

    const cleaned = beforeSend(event)!;

    expect(cleaned.extra.transcript).toBe(REDACTED);
    expect(cleaned.extra.recordingId).toBe("rid-1");
    expect(cleaned.tags.authorization).toBe(REDACTED);
    expect(cleaned.tags.model).toBe("large-v3");
  });

  // httpContextIntegration (a DEFAULT integration - @sentry/browser's integrations/httpcontext.js)
  // copies the browser's request data onto event.request, including a `Referer` header holding the
  // FULL previous URL. On this app that is the page the user came from, which for /setup carries the
  // account-activation token and for /connect/authorize carries the whole OAuth query.
  it("strips the query string from the Referer header, keeping the other headers", () => {
    const event = {
      request: {
        url: "https://app.example/",
        headers: {
          Referer: "https://app.example/setup?email=a@b.c&token=SETUP-TOKEN-123",
          "User-Agent": "Mozilla/5.0",
        },
      },
    } as any;

    const cleaned = beforeSend(event)!;

    expect(JSON.stringify(cleaned)).not.toContain("SETUP-TOKEN-123");
    expect(cleaned.request.headers.Referer).toBe("https://app.example/setup");
    expect(cleaned.request.headers["User-Agent"]).toBe("Mozilla/5.0");
  });

  it("strips a URL query string from an exception value", () => {
    const event = {
      exception: {
        values: [
          { type: "Error", value: "Request failed: GET /hubs/transcription?access_token=A_LIVE_JWT" },
        ],
      },
    } as any;

    const cleaned = beforeSend(event)!;

    expect(JSON.stringify(cleaned)).not.toContain("A_LIVE_JWT");
    expect(cleaned.exception.values[0].value).toBe("Request failed: GET /hubs/transcription");
    expect(cleaned.exception.values[0].type).toBe("Error");
  });

  it("strips a URL query string from the event message", () => {
    const event = { message: "Navigation to /setup?token=SETUP-TOKEN-123 failed" } as any;

    const cleaned = beforeSend(event)!;

    expect(JSON.stringify(cleaned)).not.toContain("SETUP-TOKEN-123");
    expect(cleaned.message).toBe("Navigation to /setup failed");
  });

  it("does not throw on a bare event", () => {
    expect(() => beforeSend({} as any)).not.toThrow();
  });

  it("does not throw when request has no headers and exception has no values", () => {
    expect(() => beforeSend({ request: { url: "/x" }, exception: {} } as any)).not.toThrow();
  });
});

describe("beforeBreadcrumb", () => {
  it("strips the access token from a fetch breadcrumb URL", () => {
    const crumb = {
      category: "fetch",
      data: { url: "/hubs/transcription?access_token=A_LIVE_JWT", method: "POST" },
    } as any;

    const cleaned = beforeBreadcrumb(crumb)!;

    expect(JSON.stringify(cleaned)).not.toContain("A_LIVE_JWT");
    expect(cleaned.data.url).toBe("/hubs/transcription");
    expect(cleaned.data.method).toBe("POST");
  });

  it("strips the access token from an xhr breadcrumb URL", () => {
    const crumb = { category: "xhr", data: { url: "/api/x?access_token=A_LIVE_JWT" } } as any;

    expect(JSON.stringify(beforeBreadcrumb(crumb))).not.toContain("A_LIVE_JWT");
  });

  it("drops low-level console breadcrumbs, which can carry arbitrary logged content", () => {
    expect(beforeBreadcrumb({ category: "console", level: "log" } as any)).toBeNull();
    expect(beforeBreadcrumb({ category: "console", level: "info" } as any)).toBeNull();
    expect(beforeBreadcrumb({ category: "console", level: "debug" } as any)).toBeNull();
  });

  it("keeps console breadcrumbs at warn and error, which are the diagnostic ones", () => {
    expect(beforeBreadcrumb({ category: "console", level: "error" } as any)).not.toBeNull();
    expect(beforeBreadcrumb({ category: "console", level: "warning" } as any)).not.toBeNull();
  });

  it("redacts sensitive keys in breadcrumb data", () => {
    const crumb = { category: "custom", data: { summary: "meeting summary", recordingId: "rid-1" } } as any;

    const cleaned = beforeBreadcrumb(crumb)!;

    expect(cleaned.data.summary).toBe(REDACTED);
    expect(cleaned.data.recordingId).toBe("rid-1");
  });

  // breadcrumbsIntegration is a DEFAULT integration and its history handler
  // (@sentry/browser's integrations/breadcrumbs.js `_getHistoryBreadcrumbHandler`) emits
  // `{ category: "navigation", data: { from, to } }` where each value is `parseUrl(...).relative` -
  // path PLUS query PLUS hash. Neither key is named "url", so a `data.url`-only rule misses both.
  it("strips the activation token from a navigation breadcrumb's from/to", () => {
    const crumb = {
      category: "navigation",
      data: { from: "/setup?email=a@b.c&token=SETUP-TOKEN-123", to: "/" },
    } as any;

    const cleaned = beforeBreadcrumb(crumb)!;

    expect(JSON.stringify(cleaned)).not.toContain("SETUP-TOKEN-123");
    expect(cleaned.data.from).toBe("/setup");
    expect(cleaned.data.to).toBe("/");
  });

  it("strips the OAuth authorize query, and the returnTo carrying it, from a navigation breadcrumb", () => {
    const crumb = {
      category: "navigation",
      data: {
        from: "/login?returnTo=%2Fconnect%2Fauthorize%3Fclient_id%3Dx%26code_challenge%3DPKCE-CHALLENGE",
        to: "/connect/authorize?client_id=x&code_challenge=PKCE-CHALLENGE&state=STATE-123",
      },
    } as any;

    const cleaned = beforeBreadcrumb(crumb)!;

    expect(JSON.stringify(cleaned)).not.toContain("PKCE-CHALLENGE");
    expect(JSON.stringify(cleaned)).not.toContain("STATE-123");
    expect(cleaned.data.from).toBe("/login");
    expect(cleaned.data.to).toBe("/connect/authorize");
  });

  // The rule is "every string value", not a list of key names, so a URL-valued key this SDK has not
  // invented yet is covered on the day it appears.
  it("strips the query string from an unknown URL-valued breadcrumb data key", () => {
    const crumb = { category: "custom", data: { destination: "/hubs/x?access_token=A_LIVE_JWT" } } as any;

    expect(JSON.stringify(beforeBreadcrumb(crumb))).not.toContain("A_LIVE_JWT");
  });

  // `data.arguments` is a REAL default shape, not a hypothetical: breadcrumbs.js's
  // `_getConsoleBreadcrumbHandler` sets `data: { arguments: handlerData.args, logger: "console" }` -
  // the raw console call arguments. warn/error console breadcrumbs are deliberately KEPT, so a
  // top-level-only scrub leaves any URL passed to console.error fully intact.
  it("strips a URL inside the console breadcrumb's arguments array", () => {
    const crumb = {
      category: "console",
      level: "error",
      message: "redirect failed /setup?token=SETUP-TOKEN-123",
      data: { arguments: ["redirect failed", "/setup?token=SETUP-TOKEN-123"], logger: "console" },
    } as any;

    const cleaned = beforeBreadcrumb(crumb)!;

    expect(JSON.stringify(cleaned)).not.toContain("SETUP-TOKEN-123");
    expect(cleaned.data.arguments).toEqual(["redirect failed", "/setup"]);
    expect(cleaned.data.logger).toBe("console");
  });

  it("strips a URL nested one object deep in breadcrumb data", () => {
    const crumb = { category: "custom", data: { req: { url: "/x?token=SECRET" } } } as any;

    const cleaned = beforeBreadcrumb(crumb)!;

    expect(JSON.stringify(cleaned)).not.toContain("SECRET");
    expect(cleaned.data.req.url).toBe("/x");
  });

  it("strips URLs inside an array of objects in breadcrumb data", () => {
    const crumb = {
      category: "console",
      level: "warning",
      data: {
        arguments: [{ tries: [{ href: "/hubs/transcription?access_token=A_LIVE_JWT" }] }, "retrying"],
      },
    } as any;

    const cleaned = beforeBreadcrumb(crumb)!;

    expect(JSON.stringify(cleaned)).not.toContain("A_LIVE_JWT");
    expect(cleaned.data.arguments[0].tries[0].href).toBe("/hubs/transcription");
    expect(cleaned.data.arguments[1]).toBe("retrying");
  });

  it("redacts a nested *.query key, not just a top-level one", () => {
    const crumb = { category: "custom", data: { req: { "http.query": "?access_token=A_LIVE_JWT" } } } as any;

    const cleaned = beforeBreadcrumb(crumb)!;

    expect(JSON.stringify(cleaned)).not.toContain("A_LIVE_JWT");
    expect(cleaned.data.req["http.query"]).toBe(REDACTED);
  });

  // Breadcrumb data is arbitrary user-supplied structure, and this runs inside an error handler: a
  // hang here takes the page with it, which is strictly worse than the leak being fixed.
  it("does not hang or throw on a self-referential breadcrumb data object", () => {
    const cycle: Record<string, unknown> = { url: "/x?token=SECRET" };
    cycle.self = cycle;
    const crumb = { category: "custom", data: { cycle } } as any;

    let cleaned: any;
    expect(() => {
      cleaned = beforeBreadcrumb(crumb);
    }).not.toThrow();
    expect(JSON.stringify(cleaned)).not.toContain("SECRET");
  });

  it("does not hang on a cycle reached through an array", () => {
    const arr: unknown[] = ["/x?token=SECRET"];
    arr.push(arr);
    const crumb = { category: "custom", data: { arr } } as any;

    expect(() => beforeBreadcrumb(crumb)).not.toThrow();
  });

  it("keeps non-URL breadcrumb data values untouched", () => {
    const crumb = { category: "fetch", data: { status_code: 200, method: "GET", url: "/api/x" } } as any;

    const cleaned = beforeBreadcrumb(crumb)!;

    expect(cleaned.data.status_code).toBe(200);
    expect(cleaned.data.method).toBe("GET");
    expect(cleaned.data.url).toBe("/api/x");
  });

  it("does not throw on a bare breadcrumb", () => {
    expect(() => beforeBreadcrumb({} as any)).not.toThrow();
  });
});

// jsdom implements neither `fetch` nor `Response`, and nothing else in this app uses them (every other
// HTTP call goes through axios). So assign a stub rather than spying on a global that does not exist,
// and return a duck-typed object rather than constructing a real Response.
function stubConfig(body: unknown) {
  const fetchStub = vi.fn().mockResolvedValue({ json: async () => body });
  (globalThis as any).fetch = fetchStub;
  return fetchStub;
}

afterEach(() => {
  delete (globalThis as any).fetch;
});

describe("initTelemetry", () => {
  it("does nothing when the API returns an empty DSN", async () => {
    const init = vi.fn();
    stubConfig({ sentryDsn: "", sentryEnvironment: "development", sentryTracesSampleRate: 1 });

    expect(await initTelemetry({ init } as any)).toBe(false);
    expect(init).not.toHaveBeenCalled();
  });

  it("does nothing when the config request fails, and does not throw", async () => {
    const init = vi.fn();
    (globalThis as any).fetch = vi.fn().mockRejectedValue(new Error("offline"));

    await expect(initTelemetry({ init } as any)).resolves.toBe(false);
    expect(init).not.toHaveBeenCalled();
  });

  it("wires both hooks and disables PII", async () => {
    const init = vi.fn();
    stubConfig({
      sentryDsn: "https://k@errors.example/2",
      sentryEnvironment: "production",
      sentryTracesSampleRate: 1,
    });

    expect(await initTelemetry({ init } as any)).toBe(true);

    const opts = init.mock.calls[0][0];
    expect(opts.dsn).toBe("https://k@errors.example/2");
    expect(opts.sendDefaultPii).toBe(false);
    // Both hooks must be wired - phase 1 shipped a leak because one of a pair was missed.
    expect(opts.beforeSend).toBe(beforeSend);
    expect(opts.beforeBreadcrumb).toBe(beforeBreadcrumb);
    // Tracing is on as of this release, driven by the configured sample rate.
    expect(opts.tracesSampleRate).toBe(1);
  });

  // GlitchTip does not support sessions. An earlier cut passed `autoSessionTracking: false`, which is
  // not an option this SDK has (zero occurrences of the name anywhere in @sentry/* 10.69.0) - the SPA
  // kept sending a session envelope on load and on every route change. The only way to stop it is to
  // remove browserSessionIntegration, which is a DEFAULT integration; and because getIntegrationsToSetup
  // MERGES a supplied array with the defaults, that means passing a function that filters them.
  it("removes the browser session integration rather than passing a dead option", async () => {
    const init = vi.fn();
    stubConfig({ sentryDsn: "https://k@errors.example/2", sentryTracesSampleRate: 1 });

    await initTelemetry({ init } as any);

    const opts = init.mock.calls[0][0];
    expect(opts.autoSessionTracking).toBeUndefined();
    expect(typeof opts.integrations).toBe("function");

    // The names are the real ones: browsersession.js declares `name: "BrowserSession"`, and
    // breadcrumbs/httpcontext are the other defaults this app relies on.
    const defaults = [{ name: "Breadcrumbs" }, { name: "BrowserSession" }, { name: "HttpContext" }];
    const resolved = opts.integrations(defaults).map((i: { name: string }) => i.name);

    expect(resolved).not.toContain("BrowserSession");
    expect(resolved).toContain("Breadcrumbs");
    expect(resolved).toContain("HttpContext");
    // Tracing must survive the filter.
    expect(resolved).toContain("BrowserTracing");
  });

  // The app previously waited on /api/config with no bound, so an API that hangs left a blank page up
  // for as long as the proxy allowed. Telemetry must never noticeably delay the app.
  it("gives up on the config request after a short timeout so the app still renders", async () => {
    vi.useFakeTimers();
    try {
      const init = vi.fn();
      (globalThis as any).fetch = vi.fn(() => new Promise(() => {}));

      const pending = initTelemetry({ init } as any);
      await vi.advanceTimersByTimeAsync(CONFIG_TIMEOUT_MS);

      await expect(pending).resolves.toBe(false);
      expect(init).not.toHaveBeenCalled();
    } finally {
      vi.useRealTimers();
    }
  });

  it("uses a config timeout short enough to be invisible", () => {
    expect(CONFIG_TIMEOUT_MS).toBeLessThanOrEqual(2000);
  });
});

describe("initTelemetry with tracing", () => {
  it("enables browser tracing at the configured sample rate", async () => {
    const init = vi.fn();
    stubConfig({
      sentryDsn: "https://k@errors.example/2",
      sentryEnvironment: "production",
      sentryTracesSampleRate: 0.25,
    });

    await initTelemetry({ init } as any);

    const opts = init.mock.calls[0][0];
    expect(opts.tracesSampleRate).toBe(0.25);
    // The function form (rather than an array) is required to drop a default integration - see
    // "removes the browser session integration rather than passing a dead option" above.
    expect(typeof opts.integrations).toBe("function");
    expect(opts.integrations([]).map((i: { name: string }) => i.name)).toContain("BrowserTracing");
  });

  it("keeps every scrubbing hook wired once tracing is on", async () => {
    const init = vi.fn();
    stubConfig({ sentryDsn: "https://k@errors.example/2", sentryTracesSampleRate: 1 });

    await initTelemetry({ init } as any);

    const opts = init.mock.calls[0][0];
    expect(opts.beforeSend).toBe(beforeSend);
    expect(opts.beforeBreadcrumb).toBe(beforeBreadcrumb);
    expect(opts.beforeSendTransaction).toBe(beforeSendTransaction);
  });
});

describe("beforeSendTransaction", () => {
  it("strips the access token from the transaction request URL", () => {
    const tx = { request: { url: "/hubs/transcription?access_token=A_LIVE_JWT" } } as any;

    expect(JSON.stringify(beforeSendTransaction(tx))).not.toContain("A_LIVE_JWT");
  });

  it("strips the access token from span descriptions", () => {
    const tx = {
      spans: [{ description: "GET /hubs/transcription?access_token=A_LIVE_JWT" }],
    } as any;

    const cleaned = beforeSendTransaction(tx)!;

    expect(JSON.stringify(cleaned)).not.toContain("A_LIVE_JWT");
    expect(cleaned.spans[0].description).toBe("GET /hubs/transcription");
  });

  it("does not throw on a bare transaction", () => {
    expect(() => beforeSendTransaction({} as any)).not.toThrow();
  });

  // Realistic shape taken from @sentry/core's getFetchSpanAttributes / @sentry/browser's xhrCallback:
  // auto-instrumented fetch/xhr spans carry the FULL unsanitized URL (with query) on `url`, `http.url`
  // and `url.full`, plus the raw query string alone on `http.query` - none of these are touched by the
  // SDK's own sanitizer, which only cleans the span's name/description.
  it("strips the access token from fetch/xhr span attributes (url, http.url, url.full, http.query)", () => {
    const tx = {
      spans: [
        {
          description: "GET /hubs/transcription",
          data: {
            url: "/hubs/transcription?access_token=A_LIVE_JWT",
            type: "fetch",
            "http.method": "GET",
            "sentry.origin": "auto.http.browser",
            "sentry.op": "http.client",
            "http.url": "https://app.example/hubs/transcription?access_token=A_LIVE_JWT",
            "url.full": "https://app.example/hubs/transcription?access_token=A_LIVE_JWT",
            "server.address": "app.example",
            "http.query": "?access_token=A_LIVE_JWT",
            "http.response.status_code": 200,
          },
        },
      ],
    } as any;

    const cleaned = beforeSendTransaction(tx)!;

    expect(JSON.stringify(cleaned)).not.toContain("A_LIVE_JWT");
  });

  it("strips the access token from contexts.trace.data on the root span", () => {
    // spanToTransactionTraceContext (@sentry/core/utils/spanUtils.js) copies the root span's own
    // `data` onto event.contexts.trace.data - when the root span IS the http.client span (e.g. a
    // parentless negotiate/connect call with no page transaction as parent), the same unsanitized
    // url/http.url/url.full/http.query attributes land there too.
    const tx = {
      contexts: {
        trace: {
          trace_id: "abc123",
          span_id: "def456",
          data: {
            url: "/hubs/transcription?access_token=A_LIVE_JWT",
            "http.url": "https://app.example/hubs/transcription?access_token=A_LIVE_JWT",
            "url.full": "https://app.example/hubs/transcription?access_token=A_LIVE_JWT",
            "http.query": "?access_token=A_LIVE_JWT",
            "http.method": "GET",
          },
        },
      },
    } as any;

    const cleaned = beforeSendTransaction(tx)!;

    expect(JSON.stringify(cleaned)).not.toContain("A_LIVE_JWT");
  });

  it("keeps diagnostic attributes and the URL path, so the trace is still useful", () => {
    const tx = {
      spans: [
        {
          description: "GET /hubs/transcription",
          data: {
            url: "/hubs/transcription?access_token=A_LIVE_JWT",
            "http.method": "GET",
            "http.response.status_code": 200,
          },
        },
      ],
    } as any;

    const cleaned = beforeSendTransaction(tx)!;
    const data = cleaned.spans[0].data;

    expect(data["http.method"]).toBe("GET");
    expect(data["http.response.status_code"]).toBe(200);
    expect(data.url).toBe("/hubs/transcription");
  });

  // The same helper serves spans, so the recursion reaches nested span attributes too. This is
  // additive - every flat-attribute assertion above still holds unchanged.
  it("strips a URL nested inside a span attribute value", () => {
    const tx = { spans: [{ data: { "http.request.body": { callback: "/x?access_token=A_LIVE_JWT" } } }] } as any;

    expect(JSON.stringify(beforeSendTransaction(tx))).not.toContain("A_LIVE_JWT");
  });

  it("does not hang on a self-referential span attribute", () => {
    const cycle: Record<string, unknown> = { url: "/x?access_token=A_LIVE_JWT" };
    cycle.self = cycle;

    expect(() => beforeSendTransaction({ spans: [{ data: { cycle } }] } as any)).not.toThrow();
  });

  it("does not throw on a span with no data", () => {
    const tx = { spans: [{ description: "GET /x" }] } as any;

    expect(() => beforeSendTransaction(tx)).not.toThrow();
  });

  it("does not throw when a span's data is null", () => {
    const tx = { spans: [{ description: "GET /x", data: null }] } as any;

    expect(() => beforeSendTransaction(tx)).not.toThrow();
  });

  it("does not throw when contexts.trace has no data", () => {
    const tx = { contexts: { trace: { trace_id: "abc" } } } as any;

    expect(() => beforeSendTransaction(tx)).not.toThrow();
  });
});
