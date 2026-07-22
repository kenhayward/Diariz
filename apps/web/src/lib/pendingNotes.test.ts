import { describe, expect, it } from "vitest";
import {
  savePendingNotes,
  loadPendingNotes,
  clearPendingNotes,
  type PendingNotes,
} from "./pendingNotes";

const stash = (userId: string): PendingNotes => ({
  userId,
  recordingId: null,
  updatedAt: 1,
  lines: [{ text: "hello", capturedAtMs: 5_000 }],
});

describe("pendingNotes", () => {
  it("returns null when nothing is stashed for the user", async () => {
    expect(await loadPendingNotes("nobody")).toBeNull();
  });

  it("round-trips a stash", async () => {
    await savePendingNotes(stash("u1"));

    const loaded = await loadPendingNotes("u1");

    expect(loaded?.lines).toHaveLength(1);
    expect(loaded?.lines[0].text).toBe("hello");
    expect(loaded?.lines[0].capturedAtMs).toBe(5_000);
    expect(loaded?.updatedAt).toBe(1);
  });

  it("preserves a null capturedAtMs on a note line", async () => {
    await savePendingNotes({
      userId: "u-null-ts",
      recordingId: null,
      updatedAt: 2,
      lines: [{ text: "typed without a timestamp", capturedAtMs: null }],
    });

    const loaded = await loadPendingNotes("u-null-ts");

    expect(loaded?.lines[0].capturedAtMs).toBeNull();
  });

  it("keeps each user's stash separate", async () => {
    await savePendingNotes({ ...stash("u2"), recordingId: "rec-2" });

    expect((await loadPendingNotes("u2"))?.recordingId).toBe("rec-2");
    expect(await loadPendingNotes("u3")).toBeNull();
  });

  it("overwrites a user's existing stash on save", async () => {
    await savePendingNotes(stash("u-overwrite"));
    await savePendingNotes({
      userId: "u-overwrite",
      recordingId: null,
      updatedAt: 2,
      lines: [{ text: "second draft", capturedAtMs: null }],
    });

    const loaded = await loadPendingNotes("u-overwrite");

    expect(loaded?.lines).toHaveLength(1);
    expect(loaded?.lines[0].text).toBe("second draft");
  });

  it("clears a stash", async () => {
    await savePendingNotes(stash("u4"));

    await clearPendingNotes("u4");

    expect(await loadPendingNotes("u4")).toBeNull();
  });

  it("resolves without throwing when IndexedDB is unavailable", async () => {
    const original = globalThis.indexedDB;
    // @ts-expect-error simulating an environment without IndexedDB
    delete globalThis.indexedDB;

    try {
      await expect(savePendingNotes(stash("u-no-idb"))).resolves.toBeUndefined();
      await expect(loadPendingNotes("u-no-idb")).resolves.toBeNull();
      await expect(clearPendingNotes("u-no-idb")).resolves.toBeUndefined();
    } finally {
      globalThis.indexedDB = original;
    }
  });
});
