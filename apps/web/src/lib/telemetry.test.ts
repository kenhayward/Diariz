import { describe, it, expect, vi, afterEach } from "vitest";
import { isSensitiveKey, stripQueryString, scrubUrlsIn, REDACTED } from "./telemetry";
import { beforeSend, beforeBreadcrumb, beforeSendTransaction, initTelemetry } from "./telemetry";

describe("isSensitiveKey", () => {
  it.each(["Authorization", "cookie", "password", "apiKey", "access_key", "accessKey", "token", "secret"])(
    "treats %s as a credential",
    (key) => expect(isSensitiveKey(key)).toBe(true),
  );

  it.each(["text", "transcript", "transcription", "segments", "words", "summary", "minutes", "note", "notes", "content"])(
    "treats %s as meeting content",
    (key) => expect(isSensitiveKey(key)).toBe(true),
  );

  it.each(["embedding", "embeddings"])("treats %s as a biometric voiceprint", (key) =>
    expect(isSensitiveKey(key)).toBe(true),
  );

  it.each(["recordingId", "transcriptionId", "blobKey", "userId", "model", "language", "status"])(
    "keeps %s, which is needed to diagnose",
    (key) => expect(isSensitiveKey(key)).toBe(false),
  );
});

describe("stripQueryString", () => {
  it("removes the SignalR access token while keeping the path", () => {
    const stripped = stripQueryString("https://app.example/hubs/transcription?access_token=A_LIVE_JWT");
    expect(stripped).toBe("https://app.example/hubs/transcription");
    expect(stripped).not.toContain("A_LIVE_JWT");
  });

  it("removes a query string from a relative URL", () => {
    expect(stripQueryString("/hubs/transcription?access_token=A_LIVE_JWT")).toBe("/hubs/transcription");
  });

  it("leaves a URL with no query string untouched", () => {
    expect(stripQueryString("https://app.example/api/recordings")).toBe("https://app.example/api/recordings");
  });

  it("leaves a value that is not a URL untouched", () => {
    expect(stripQueryString("select * from recordings where id = ?")).toBe(
      "select * from recordings where id = ?",
    );
  });

  it("returns an empty string for undefined rather than throwing", () => {
    expect(() => stripQueryString(undefined as any)).not.toThrow();
    expect(stripQueryString(undefined as any)).toBe("");
  });

  it("returns an empty string for null rather than throwing", () => {
    expect(() => stripQueryString(null as any)).not.toThrow();
    expect(stripQueryString(null as any)).toBe("");
  });
});

describe("scrubUrlsIn", () => {
  it("strips the query from a URL embedded in a longer description", () => {
    // Sentry describes fetch spans as "<METHOD> <url>".
    expect(scrubUrlsIn("GET https://app.example/hubs/transcription?access_token=A_LIVE_JWT")).toBe(
      "GET https://app.example/hubs/transcription",
    );
  });

  it("leaves free text alone", () => {
    expect(scrubUrlsIn("Detail panel crashed")).toBe("Detail panel crashed");
  });

  it("returns an empty string for undefined rather than throwing", () => {
    expect(() => scrubUrlsIn(undefined as any)).not.toThrow();
    expect(scrubUrlsIn(undefined as any)).toBe("");
  });

  it("returns an empty string for null rather than throwing", () => {
    expect(() => scrubUrlsIn(null as any)).not.toThrow();
    expect(scrubUrlsIn(null as any)).toBe("");
  });
});

describe("REDACTED", () => {
  it("matches the marker the other two runtimes use", () => {
    expect(REDACTED).toBe("[redacted]");
  });
});

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

  it("does not throw on a bare event", () => {
    expect(() => beforeSend({} as any)).not.toThrow();
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

  it("wires both hooks, disables PII and disables session tracking", async () => {
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
    // GlitchTip does not support sessions.
    expect(opts.autoSessionTracking).toBe(false);
    // Both hooks must be wired - phase 1 shipped a leak because one of a pair was missed.
    expect(opts.beforeSend).toBe(beforeSend);
    expect(opts.beforeBreadcrumb).toBe(beforeBreadcrumb);
    // Tracing is on as of this release, driven by the configured sample rate.
    expect(opts.tracesSampleRate).toBe(1);
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
    expect(Array.isArray(opts.integrations)).toBe(true);
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
