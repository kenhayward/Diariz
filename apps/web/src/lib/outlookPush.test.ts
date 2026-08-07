import { describe, it, expect, vi } from "vitest";
import { pushWindow, PAGE_SIZE, type OutlookPushClient } from "./outlookPush";
import type { OutlookPushPayload } from "./outlookSync";
import type { OutlookEventInput, OutlookSyncRequest, OutlookSyncResult } from "./types";

const ev = (i: number): OutlookEventInput => ({
  uid: `uid-${i}`,
  subject: `Meeting ${i}`,
  start: "2026-07-02T09:00:00Z",
  end: "2026-07-02T10:00:00Z",
});

const payload = (count: number, complete = true): OutlookPushPayload => ({
  syncId: "sync-1",
  device: { deviceId: "dev-1", deviceName: "WORK-PC" },
  windowStart: "2026-07-01T00:00:00Z",
  windowEnd: "2026-08-01T00:00:00Z",
  complete,
  events: Array.from({ length: count }, (_, i) => ev(i)),
});

function fakeClient(result?: Partial<OutlookSyncResult>): OutlookPushClient & { calls: OutlookSyncRequest[] } {
  const calls: OutlookSyncRequest[] = [];
  return {
    calls,
    pushOutlookEvents: vi.fn(async (req: OutlookSyncRequest) => {
      calls.push(req);
      return {
        sourceId: "src-1", syncId: req.syncId, created: 1, updated: 0, unchanged: 0, deleted: 0,
        skipped: 0, eventCount: 42, syncedAt: "2026-07-02T10:00:00Z", ...result,
      } satisfies OutlookSyncResult;
    }),
  };
}

describe("pushWindow", () => {
  it("pages a large window and marks only the last page final", async () => {
    const client = fakeClient();
    const result = await pushWindow(payload(PAGE_SIZE * 2 + 10), client);

    expect(client.calls).toHaveLength(3);
    expect(client.calls.map((c) => c.events.length)).toEqual([PAGE_SIZE, PAGE_SIZE, 10]);
    expect(client.calls.map((c) => c.final)).toEqual([false, false, true]);
    expect(client.calls.map((c) => c.pageIndex)).toEqual([0, 1, 2]);
    expect(result.ok).toBe(true);
  });

  it("carries the same sync id and window on every page", async () => {
    const client = fakeClient();
    await pushWindow(payload(PAGE_SIZE + 1), client);

    expect(new Set(client.calls.map((c) => c.syncId))).toEqual(new Set(["sync-1"]));
    expect(new Set(client.calls.map((c) => c.windowStart))).toEqual(new Set(["2026-07-01T00:00:00Z"]));
  });

  /// The sweep licence. A partial read must reach the server as `complete: false` on every page, or the
  /// server would delete the meetings the failed read never got to.
  it("passes the incomplete flag through untouched", async () => {
    const client = fakeClient();
    await pushWindow(payload(3, false), client);

    expect(client.calls.every((c) => c.complete === false)).toBe(true);
  });

  it("sends one final empty page for an empty window, so cancellations are swept", async () => {
    const client = fakeClient();
    await pushWindow(payload(0), client);

    expect(client.calls).toHaveLength(1);
    expect(client.calls[0].events).toEqual([]);
    expect(client.calls[0].final).toBe(true);
  });

  /// Stopping matters more than reporting: carrying on after a gap would let the server reach a final page
  /// having never seen the events the failed page carried, and sweep them as cancellations.
  it("aborts on the first failure and sends no later pages", async () => {
    const calls: OutlookSyncRequest[] = [];
    const client: OutlookPushClient = {
      pushOutlookEvents: vi.fn(async (req: OutlookSyncRequest) => {
        calls.push(req);
        if (calls.length === 2) throw new Error("network died");
        return {
          sourceId: "src-1", syncId: req.syncId, created: 0, updated: 0, unchanged: 0, deleted: 0,
          skipped: 0, eventCount: 0, syncedAt: "2026-07-02T10:00:00Z",
        } satisfies OutlookSyncResult;
      }),
    };

    const result = await pushWindow(payload(PAGE_SIZE * 3), client);

    expect(calls).toHaveLength(2); // stopped, did not attempt the third
    expect(result.ok).toBe(false);
    expect(result.error).toContain("network died");
  });

  it("sums the per-page counters", async () => {
    const client = fakeClient({ created: 2, updated: 3, deleted: 1 });
    const result = await pushWindow(payload(PAGE_SIZE + 1), client);

    expect(result.created).toBe(4); // 2 pages x 2
    expect(result.updated).toBe(6);
    expect(result.deleted).toBe(2);
  });
});
