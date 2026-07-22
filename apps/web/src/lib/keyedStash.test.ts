import { describe, expect, it } from "vitest";
import { createKeyedStash } from "./keyedStash";

interface Widget {
  userId: string;
  count: number;
  blob: Blob;
}

describe("keyedStash", () => {
  it("returns null when nothing is stashed for the key", async () => {
    const stash = createKeyedStash<Widget>("test-db-1", "widgets", "userId");

    expect(await stash.load("nobody")).toBeNull();
  });

  it("round-trips a value including a Blob field, byte for byte", async () => {
    const stash = createKeyedStash<Widget>("test-db-2", "widgets", "userId");
    const blob = new Blob([new Uint8Array([1, 2, 3])], { type: "image/png" });

    await stash.save({ userId: "u1", count: 7, blob });
    const loaded = await stash.load("u1");

    expect(loaded?.count).toBe(7);
    expect(loaded?.blob.type).toBe("image/png");
    expect(await loaded!.blob.arrayBuffer()).toEqual(new Uint8Array([1, 2, 3]).buffer);
  });

  it("keeps values under different keys separate", async () => {
    const stash = createKeyedStash<Widget>("test-db-3", "widgets", "userId");

    await stash.save({ userId: "u2", count: 1, blob: new Blob([]) });

    expect((await stash.load("u2"))?.count).toBe(1);
    expect(await stash.load("u3")).toBeNull();
  });

  it("clears a stashed value", async () => {
    const stash = createKeyedStash<Widget>("test-db-4", "widgets", "userId");

    await stash.save({ userId: "u4", count: 3, blob: new Blob([]) });
    await stash.clear("u4");

    expect(await stash.load("u4")).toBeNull();
  });

  it("keeps separate database/store instances from colliding on the same key", async () => {
    const stashA = createKeyedStash<Widget>("test-db-5a", "widgets", "userId");
    const stashB = createKeyedStash<Widget>("test-db-5b", "widgets", "userId");

    await stashA.save({ userId: "shared", count: 100, blob: new Blob([]) });

    expect((await stashA.load("shared"))?.count).toBe(100);
    expect(await stashB.load("shared")).toBeNull();
  });

  it("degrades to no-ops when IndexedDB is unavailable", async () => {
    const original = globalThis.indexedDB;
    // @ts-expect-error simulating an environment without IndexedDB
    delete globalThis.indexedDB;

    try {
      const stash = createKeyedStash<Widget>("test-db-6", "widgets", "userId");

      await expect(stash.save({ userId: "u5", count: 1, blob: new Blob([]) })).resolves.toBeUndefined();
      await expect(stash.load("u5")).resolves.toBeNull();
      await expect(stash.clear("u5")).resolves.toBeUndefined();
    } finally {
      globalThis.indexedDB = original;
    }
  });
});
