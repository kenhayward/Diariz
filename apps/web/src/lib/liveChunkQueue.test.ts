import { describe, expect, it, vi } from "vitest";
import { createChunkQueue, missingSequences, type ChunkStore, type StoredChunk } from "./liveChunkQueue";

/// An in-memory stand-in for the IndexedDB store, so the queue's ordering and durability rules can be
/// exercised without a real database. The IndexedDB-backed one is a thin adapter over this shape.
function memoryStore(): ChunkStore & { rows: Map<number, StoredChunk> } {
  const rows = new Map<number, StoredChunk>();
  return {
    rows,
    async put(chunk) {
      rows.set(chunk.sequence, chunk);
    },
    async remove(sequence) {
      rows.delete(sequence);
    },
    async all() {
      return [...rows.values()].sort((a, b) => a.sequence - b.sequence);
    },
  };
}

const blob = (body = "audio") => new Blob([body], { type: "audio/webm" });

describe("missingSequences", () => {
  it("finds the holes below the highest received", () => {
    expect(missingSequences([0, 1, 3, 4], 4)).toEqual([2]);
    expect(missingSequences([0, 1, 2], 2)).toEqual([]);
    expect(missingSequences([2], 2)).toEqual([0, 1]);
  });

  it("reports nothing when nothing was received", () => {
    expect(missingSequences([], -1)).toEqual([]);
  });
});

describe("liveChunkQueue", () => {
  it("assigns sequences from zero, monotonically, and never reuses one", async () => {
    const store = memoryStore();
    const sent: number[] = [];
    const q = createChunkQueue({ upload: async (c) => void sent.push(c.sequence), store });

    await q.enqueue(blob("a"), 0, 30_000);
    await q.enqueue(blob("b"), 30_000, 60_000);
    await q.enqueue(blob("c"), 60_000, 90_000);
    await q.drain();

    expect(sent).toEqual([0, 1, 2]);
    expect(q.highestSequence()).toBe(2);
  });

  it("writes to the store before attempting the upload", async () => {
    // The durability guarantee is the ordering. A crash between the two must leave the chunk
    // recoverable, so the write cannot wait for the network to answer.
    const store = memoryStore();
    let rowsAtUploadTime = -1;
    const q = createChunkQueue({
      upload: async () => {
        rowsAtUploadTime = store.rows.size;
      },
      store,
    });

    await q.enqueue(blob(), 0, 30_000);
    await q.drain();

    expect(rowsAtUploadTime).toBe(1);
  });

  it("clears a chunk from the store only once it has been accepted", async () => {
    const store = memoryStore();
    const q = createChunkQueue({ upload: async () => {}, store });

    await q.enqueue(blob(), 0, 30_000);
    await q.drain();

    expect(store.rows.size).toBe(0);
  });

  it("keeps a failed chunk and retries it", async () => {
    const store = memoryStore();
    const upload = vi.fn()
      .mockRejectedValueOnce(new Error("network"))
      .mockResolvedValue(undefined);
    const q = createChunkQueue({ upload, store });

    await q.enqueue(blob(), 0, 30_000);
    await q.drain();
    expect(store.rows.size).toBe(1);   // still pending after the failure

    await q.drain();
    expect(upload).toHaveBeenCalledTimes(2);
    expect(store.rows.size).toBe(0);
  });

  it("does not let a later chunk overtake an earlier one that is still failing", async () => {
    // Out-of-order arrival would leave a hole that finalise refuses, and the recording is only
    // contiguous because the queue keeps it so.
    const store = memoryStore();
    const attempts: number[] = [];
    let failFirst = true;
    const q = createChunkQueue({
      upload: async (c) => {
        attempts.push(c.sequence);
        if (c.sequence === 0 && failFirst) throw new Error("network");
      },
      store,
    });

    await q.enqueue(blob("a"), 0, 30_000);
    await q.enqueue(blob("b"), 30_000, 60_000);
    await q.drain();

    // Chunk 1 must not have been sent while 0 was outstanding.
    expect(attempts).toEqual([0]);

    failFirst = false;
    await q.drain();
    expect(attempts).toEqual([0, 0, 1]);
  });

  it("recovers chunks left in the store by a previous session", async () => {
    const store = memoryStore();
    await store.put({ sequence: 0, blob: blob("stale"), startMs: 0, endMs: 30_000 });
    await store.put({ sequence: 1, blob: blob("stale"), startMs: 30_000, endMs: 60_000 });

    const sent: number[] = [];
    const q = createChunkQueue({ upload: async (c) => void sent.push(c.sequence), store });
    await q.restore();
    await q.drain();

    expect(sent).toEqual([0, 1]);
    // And the next chunk continues the sequence rather than colliding with a recovered one.
    await q.enqueue(blob("new"), 60_000, 90_000);
    await q.drain();
    expect(sent).toEqual([0, 1, 2]);
  });

  it("keeps recording when the store is unavailable", async () => {
    // jsdom, a private window, or a locked-down browser. Persistence is best-effort and must never
    // be the reason a meeting is not captured - the same rule pendingRecording already follows.
    const broken: ChunkStore = {
      put: async () => {
        throw new Error("no indexeddb");
      },
      remove: async () => {
        throw new Error("no indexeddb");
      },
      all: async () => {
        throw new Error("no indexeddb");
      },
    };
    const sent: number[] = [];
    const q = createChunkQueue({ upload: async (c) => void sent.push(c.sequence), store: broken });

    await q.restore();
    await q.enqueue(blob(), 0, 30_000);
    await q.drain();

    expect(sent).toEqual([0]);
  });

  it("reports the sequences it still holds, for a finalise that found a gap", async () => {
    const store = memoryStore();
    const q = createChunkQueue({
      upload: async (c) => {
        if (c.sequence === 1) throw new Error("network");
      },
      store,
    });

    await q.enqueue(blob(), 0, 30_000);
    await q.enqueue(blob(), 30_000, 60_000);
    await q.drain();

    expect(await q.pendingSequences()).toEqual([1]);
  });
});
