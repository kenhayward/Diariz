import { describe, expect, it } from "vitest";
import {
  savePendingScreenshots,
  loadPendingScreenshots,
  clearPendingScreenshots,
  type PendingScreenshots,
} from "./pendingScreenshots";

const stash = (userId: string): PendingScreenshots => ({
  userId,
  recordingId: null,
  updatedAt: 1,
  shots: [
    {
      capturedAtMs: 5_000,
      width: 1920,
      height: 1080,
      full: new Blob([new Uint8Array([1, 2, 3])], { type: "image/png" }),
      thumb: new Blob([new Uint8Array([4])], { type: "image/jpeg" }),
    },
  ],
});

describe("pendingScreenshots", () => {
  it("returns null when nothing is stashed for the user", async () => {
    expect(await loadPendingScreenshots("nobody")).toBeNull();
  });

  it("round-trips a stash including its image blobs", async () => {
    await savePendingScreenshots(stash("u1"));

    const loaded = await loadPendingScreenshots("u1");

    expect(loaded?.shots).toHaveLength(1);
    expect(loaded?.shots[0].capturedAtMs).toBe(5_000);
    expect(loaded?.shots[0].width).toBe(1920);
    expect(loaded?.shots[0].height).toBe(1080);
    expect(loaded?.shots[0].full.type).toBe("image/png");
    expect(await loaded!.shots[0].full.arrayBuffer()).toEqual(new Uint8Array([1, 2, 3]).buffer);
    expect(loaded?.shots[0].thumb.type).toBe("image/jpeg");
    expect(await loaded!.shots[0].thumb.arrayBuffer()).toEqual(new Uint8Array([4]).buffer);
  });

  it("keeps each user's stash separate", async () => {
    await savePendingScreenshots({ ...stash("u2"), recordingId: "rec-2" });

    expect((await loadPendingScreenshots("u2"))?.recordingId).toBe("rec-2");
    expect(await loadPendingScreenshots("u3")).toBeNull();
  });

  it("overwrites a user's existing stash on save", async () => {
    await savePendingScreenshots(stash("u-overwrite"));
    await savePendingScreenshots({ ...stash("u-overwrite"), shots: [] });

    expect((await loadPendingScreenshots("u-overwrite"))?.shots).toHaveLength(0);
  });

  it("clears a stash", async () => {
    await savePendingScreenshots(stash("u4"));

    await clearPendingScreenshots("u4");

    expect(await loadPendingScreenshots("u4")).toBeNull();
  });

  it("resolves without throwing when IndexedDB is unavailable", async () => {
    const original = globalThis.indexedDB;
    // @ts-expect-error simulating an environment without IndexedDB
    delete globalThis.indexedDB;

    try {
      await expect(savePendingScreenshots(stash("u-no-idb"))).resolves.toBeUndefined();
      await expect(loadPendingScreenshots("u-no-idb")).resolves.toBeNull();
      await expect(clearPendingScreenshots("u-no-idb")).resolves.toBeUndefined();
    } finally {
      globalThis.indexedDB = original;
    }
  });
});
