import { describe, it, expect, beforeEach } from "vitest";
import { record, snapshot, clearTrail, TRAIL_CAPACITY } from "./trail";
import { REDACTED } from "./scrub";

beforeEach(() => clearTrail());

describe("ring buffer", () => {
  it("keeps entries in order", () => {
    record({ kind: "nav", label: "/a" });
    record({ kind: "nav", label: "/b" });
    expect(snapshot().map((e) => e.label)).toEqual(["/a", "/b"]);
  });

  it("evicts the oldest past capacity", () => {
    for (let i = 0; i < TRAIL_CAPACITY + 5; i++) record({ kind: "mark", label: `m${i}` });
    const labels = snapshot().map((e) => e.label);
    expect(labels).toHaveLength(TRAIL_CAPACITY);
    expect(labels[0]).toBe("m5");
    expect(labels[labels.length - 1]).toBe(`m${TRAIL_CAPACITY + 4}`);
  });

  it("stamps each entry with a time", () => {
    record({ kind: "mark", label: "x" });
    expect(typeof snapshot()[0].at).toBe("number");
  });

  it("returns a copy, so a caller cannot mutate the buffer", () => {
    record({ kind: "mark", label: "x" });
    snapshot().push({ at: 0, kind: "mark", label: "injected" });
    expect(snapshot()).toHaveLength(1);
  });
});

describe("scrubbing happens on the way IN", () => {
  it("strips a query string from the label", () => {
    record({ kind: "api", label: "GET /hubs/transcription?access_token=A_LIVE_JWT" });
    expect(JSON.stringify(snapshot())).not.toContain("A_LIVE_JWT");
    expect(snapshot()[0].label).toBe("GET /hubs/transcription");
  });

  it("redacts sensitive keys in detail, keeping diagnostics", () => {
    record({ kind: "api", label: "POST /api/x", detail: { status: 200, transcript: "meeting text" } });
    const [entry] = snapshot();
    expect(entry.detail!.status).toBe(200);
    expect(entry.detail!.transcript).toBe(REDACTED);
  });

  it("redacts nested values in detail", () => {
    record({ kind: "api", label: "POST /api/x", detail: { body: { summary: "secret" } } });
    expect(JSON.stringify(snapshot())).not.toContain("secret");
  });

  it("does not throw on a cyclic detail object", () => {
    const cyclic: Record<string, unknown> = { url: "/x" };
    cyclic.self = cyclic;
    expect(() => record({ kind: "api", label: "GET /x", detail: cyclic })).not.toThrow();
  });
});

describe("independence from the error-tracking SDK", () => {
  it("records with no SDK initialised", () => {
    // No initTelemetry() has run in this suite. If trail.ts imported telemetry.ts, or needed the SDK,
    // this file could not have loaded at all.
    record({ kind: "mark", label: "works" });
    expect(snapshot()).toHaveLength(1);
  });
});
