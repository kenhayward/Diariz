import { describe, expect, it, vi } from "vitest";
import {
  addPendingScreenshot,
  loadPendingScreenshots,
  removePendingScreenshot,
  setPendingScreenshotsRecordingId,
  clearPendingScreenshots,
  type PendingShot,
} from "./pendingScreenshots";
import { useNodeBlobForIndexedDb } from "./testNodeBlob";

useNodeBlobForIndexedDb();

let nextId = 0;
const shot = (capturedAtMs: number): PendingShot => ({
  id: `shot-${nextId++}`,
  capturedAtMs,
  width: 1920,
  height: 1080,
  full: new Blob([new Uint8Array([1, 2, 3])], { type: "image/png" }),
  thumb: new Blob([new Uint8Array([4])], { type: "image/jpeg" }),
});

describe("pendingScreenshots", () => {
  it("returns null when nothing is stashed for the user", async () => {
    expect(await loadPendingScreenshots("nobody")).toBeNull();
  });

  it("round-trips a single capture including its image blobs", async () => {
    const s = shot(5_000);
    await addPendingScreenshot("u1", s);

    const loaded = await loadPendingScreenshots("u1");

    expect(loaded?.recordingId).toBeNull();
    expect(loaded?.shots).toHaveLength(1);
    expect(loaded?.shots[0].id).toBe(s.id);
    expect(loaded?.shots[0].capturedAtMs).toBe(5_000);
    expect(loaded?.shots[0].width).toBe(1920);
    expect(loaded?.shots[0].height).toBe(1080);
    expect(loaded?.shots[0].full.type).toBe("image/png");
    expect(await loaded!.shots[0].full.arrayBuffer()).toEqual(new Uint8Array([1, 2, 3]).buffer);
    expect(loaded?.shots[0].thumb.type).toBe("image/jpeg");
    expect(await loaded!.shots[0].thumb.arrayBuffer()).toEqual(new Uint8Array([4]).buffer);
  });

  it("accumulates captures one at a time, in capture order", async () => {
    await addPendingScreenshot("u-multi", shot(1_000));
    await addPendingScreenshot("u-multi", shot(2_000));
    await addPendingScreenshot("u-multi", shot(3_000));

    const loaded = await loadPendingScreenshots("u-multi");

    expect(loaded?.shots.map((s) => s.capturedAtMs)).toEqual([1_000, 2_000, 3_000]);
    const ids = loaded?.shots.map((s) => s.id) ?? [];
    expect(new Set(ids).size).toBe(3); // all distinct
  });

  it("keeps each user's stash separate", async () => {
    await addPendingScreenshot("u2", shot(1_000));
    await setPendingScreenshotsRecordingId("u2", "rec-2");

    expect((await loadPendingScreenshots("u2"))?.recordingId).toBe("rec-2");
    expect(await loadPendingScreenshots("u3")).toBeNull();
  });

  it("removes exactly one capture by id, leaving the rest", async () => {
    const first = shot(1_000);
    const second = shot(2_000);
    await addPendingScreenshot("u-del", first);
    await addPendingScreenshot("u-del", second);

    await removePendingScreenshot("u-del", first.id);

    const loaded = await loadPendingScreenshots("u-del");
    expect(loaded?.shots).toHaveLength(1);
    expect(loaded?.shots[0].id).toBe(second.id);
  });

  it("updates only the recordingId without touching the stashed captures", async () => {
    await addPendingScreenshot("u-meta", shot(1_000));

    await setPendingScreenshotsRecordingId("u-meta", "rec-9");

    const loaded = await loadPendingScreenshots("u-meta");
    expect(loaded?.recordingId).toBe("rec-9");
    expect(loaded?.shots).toHaveLength(1);
  });

  it("clears both the captures and the recordingId for a user", async () => {
    await addPendingScreenshot("u4", shot(1_000));
    await setPendingScreenshotsRecordingId("u4", "rec-4");

    await clearPendingScreenshots("u4");

    expect(await loadPendingScreenshots("u4")).toBeNull();
  });

  it("clearing one user's stash never touches another user's captures", async () => {
    await addPendingScreenshot("u-keep", shot(1_000));
    await addPendingScreenshot("u-wipe", shot(2_000));

    await clearPendingScreenshots("u-wipe");

    expect(await loadPendingScreenshots("u-wipe")).toBeNull();
    expect((await loadPendingScreenshots("u-keep"))?.shots).toHaveLength(1);
  });

  it("resolves without throwing when IndexedDB is unavailable", async () => {
    const original = globalThis.indexedDB;
    // @ts-expect-error simulating an environment without IndexedDB
    delete globalThis.indexedDB;

    try {
      await expect(addPendingScreenshot("u-no-idb", shot(1_000))).resolves.toBeUndefined();
      await expect(loadPendingScreenshots("u-no-idb")).resolves.toBeNull();
      await expect(removePendingScreenshot("u-no-idb", "whatever")).resolves.toBeUndefined();
      await expect(setPendingScreenshotsRecordingId("u-no-idb", "rec-x")).resolves.toBeUndefined();
      await expect(clearPendingScreenshots("u-no-idb")).resolves.toBeUndefined();
    } finally {
      globalThis.indexedDB = original;
    }
  });

  describe("write growth as captures accumulate (the defect this refactor fixes)", () => {
    // The bug this whole task exists to fix: the old mirrorShots wrote the *entire* growing shots array
    // to IndexedDB on every single capture, so N captures cost O(N^2) cumulative bytes/Blobs written (a
    // representative ~3 MB/capture put 200 captures at ~60 GB of churn). A call-count assertion alone
    // would NOT catch this regression - a whole-array rewrite still only calls put() once per capture,
    // same as the fixed version; the blowup is in how much each of those calls carries. So this spies on
    // IDBObjectStore.put and sums the number of per-capture image payloads (full+thumb Blob pairs) carried
    // across *all* put() calls combined: the fixed shape (one record per capture) sums to exactly N;
    // a regressed whole-array-per-write shape would sum to N*(N+1)/2 - quadratic - because put #i would
    // once again carry all i captures made so far, not just the new one.
    it("writes exactly one capture's worth of image data per put, so N captures sum to N - not N*(N+1)/2", async () => {
      const putSpy = vi.spyOn(IDBObjectStore.prototype, "put");
      const userId = "u-growth";
      const N = 40;

      for (let i = 0; i < N; i++) {
        await addPendingScreenshot(userId, shot(i));
      }

      // Every put() call this test caused (there may be more than N if other stores/tests share process
      // state, so filter to ones that look like a capture record - i.e. carry `full`/`thumb` Blobs -
      // rather than assuming every call belongs to this store).
      const captureCalls = putSpy.mock.calls.filter(([value]) => {
        const v = value as { full?: unknown; thumb?: unknown; shots?: unknown[] };
        return v && (("full" in v && "thumb" in v) || Array.isArray(v.shots));
      });

      // Each call carries either one shot directly (the fixed, linear shape) or a `shots` array (the
      // regressed, quadratic shape) - sum whichever applies per call.
      const totalImagePayloadsWritten = captureCalls.reduce((sum, [value]) => {
        const v = value as { shots?: unknown[] };
        return sum + (Array.isArray(v.shots) ? v.shots.length : 1);
      }, 0);

      putSpy.mockRestore();

      expect(totalImagePayloadsWritten).toBe(N); // linear: one payload written per capture, not the whole set each time
    });
  });
});
