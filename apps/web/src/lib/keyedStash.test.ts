import { describe, expect, it } from "vitest";
import { createKeyedStash, createMultiKeyedStash } from "./keyedStash";
import { useNodeBlobForIndexedDb } from "./testNodeBlob";

interface Widget {
  userId: string;
  count: number;
  blob: Blob;
}

interface Item {
  id: string;
  userId: string;
  value: number;
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

describe("createMultiKeyedStash", () => {
  it("lists nothing for a key with no records", async () => {
    const stash = createMultiKeyedStash<Item>("multi-db-1", "items", "id", "userId");

    expect(await stash.list("nobody")).toEqual([]);
  });

  it("adds one record at a time without touching the others under the same key", async () => {
    const stash = createMultiKeyedStash<Item>("multi-db-2", "items", "id", "userId");

    await stash.add({ id: "a", userId: "u1", value: 1 });
    await stash.add({ id: "b", userId: "u1", value: 2 });

    const listed = await stash.list("u1");
    expect(listed.map((i) => i.value).sort()).toEqual([1, 2]);
  });

  it("keeps records under different keys separate", async () => {
    const stash = createMultiKeyedStash<Item>("multi-db-3", "items", "id", "userId");

    await stash.add({ id: "a", userId: "u2", value: 1 });
    await stash.add({ id: "b", userId: "u3", value: 2 });

    expect((await stash.list("u2")).map((i) => i.id)).toEqual(["a"]);
    expect((await stash.list("u3")).map((i) => i.id)).toEqual(["b"]);
  });

  it("removes exactly one record by its primary key, leaving its neighbours", async () => {
    const stash = createMultiKeyedStash<Item>("multi-db-4", "items", "id", "userId");
    await stash.add({ id: "a", userId: "u4", value: 1 });
    await stash.add({ id: "b", userId: "u4", value: 2 });

    await stash.remove("a");

    const listed = await stash.list("u4");
    expect(listed.map((i) => i.id)).toEqual(["b"]);
  });

  it("clears every record for a key without touching other keys", async () => {
    const stash = createMultiKeyedStash<Item>("multi-db-5", "items", "id", "userId");
    await stash.add({ id: "a", userId: "u5", value: 1 });
    await stash.add({ id: "b", userId: "u5", value: 2 });
    await stash.add({ id: "c", userId: "u6", value: 3 });

    await stash.clear("u5");

    expect(await stash.list("u5")).toEqual([]);
    expect((await stash.list("u6")).map((i) => i.id)).toEqual(["c"]);
  });

  it("replaces a record with the same primary key rather than duplicating it", async () => {
    const stash = createMultiKeyedStash<Item>("multi-db-6", "items", "id", "userId");
    await stash.add({ id: "a", userId: "u7", value: 1 });

    await stash.add({ id: "a", userId: "u7", value: 99 });

    const listed = await stash.list("u7");
    expect(listed).toHaveLength(1);
    expect(listed[0].value).toBe(99);
  });

  it("degrades to no-ops when IndexedDB is unavailable", async () => {
    const original = globalThis.indexedDB;
    // @ts-expect-error simulating an environment without IndexedDB
    delete globalThis.indexedDB;

    try {
      const stash = createMultiKeyedStash<Item>("multi-db-7", "items", "id", "userId");

      await expect(stash.add({ id: "a", userId: "u8", value: 1 })).resolves.toBeUndefined();
      await expect(stash.list("u8")).resolves.toEqual([]);
      await expect(stash.remove("a")).resolves.toBeUndefined();
      await expect(stash.clear("u8")).resolves.toBeUndefined();
    } finally {
      globalThis.indexedDB = original;
    }
  });
});
