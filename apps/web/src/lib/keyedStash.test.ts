import { describe, expect, it } from "vitest";
import { createKeyedStash } from "./keyedStash";
import { useNodeBlobForIndexedDb } from "./testNodeBlob";

interface Widget {
  userId: string;
  count: number;
  blob: Blob;
}

useNodeBlobForIndexedDb();

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

  it("degrades to a no-op rather than rejecting when merely accessing indexedDB throws synchronously", async () => {
    // Some environments throw synchronously just from touching the global (e.g. a locked-down iframe
    // whose `indexedDB` accessor raises a SecurityError). openDb()'s *call* happens outside withStore's
    // own try, so a throw at this point - before openDb ever reaches `new Promise(...)` - must still
    // degrade to a no-op, exactly like the "IndexedDB is unavailable" case below, rather than rejecting
    // save()/load()/clear() and surfacing as a real failure to whatever feature sits on top of the stash.
    const descriptor = Object.getOwnPropertyDescriptor(globalThis, "indexedDB");
    Object.defineProperty(globalThis, "indexedDB", {
      configurable: true,
      get(): never {
        throw new DOMException("simulated synchronous throw on access", "SecurityError");
      },
    });

    try {
      const stash = createKeyedStash<Widget>("test-db-7", "widgets", "userId");

      await expect(stash.save({ userId: "u6", count: 1, blob: new Blob([]) })).resolves.toBeUndefined();
      await expect(stash.load("u6")).resolves.toBeNull();
      await expect(stash.clear("u6")).resolves.toBeUndefined();
    } finally {
      Object.defineProperty(globalThis, "indexedDB", descriptor!);
    }
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
