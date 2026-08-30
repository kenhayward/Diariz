/// The client half of chunked capture: assigns sequence numbers, keeps every chunk durable until the
/// server has accepted it, and uploads them strictly in order.
///
/// Two rules carry the weight, and both exist because of what the server does with them:
///
///   - **Store before upload.** A crash between the two must leave the chunk recoverable. The reverse
///     order would lose whatever was in flight, which is the loss this whole feature exists to stop.
///   - **Strictly in order.** `POST .../live/finalize` refuses a gap rather than splicing unrelated
///     audio together, so a later chunk overtaking a failing earlier one turns a transient network
///     error into a recording the user has to repair.
///
/// Persistence is best-effort, exactly as `pendingRecording` treats it: a store that throws degrades
/// to memory-only and recording continues. Losing durability is bad; refusing to record is worse.

export interface StoredChunk {
  sequence: number;
  blob: Blob;
  startMs: number;
  endMs: number;
}

/// The durable side, kept behind an interface so the queue's ordering rules are testable without a
/// real IndexedDB (and so a browser without one degrades rather than branching at every call site).
export interface ChunkStore {
  put(chunk: StoredChunk): Promise<void>;
  remove(sequence: number): Promise<void>;
  all(): Promise<StoredChunk[]>;
}

export interface ChunkQueue {
  /// Durably record a chunk and add it to the upload queue. Resolves once it is stored, not once it
  /// is uploaded - the caller is a recorder, and must not be made to wait on the network.
  enqueue(blob: Blob, startMs: number, endMs: number): Promise<void>;
  /// Upload everything outstanding, oldest first, stopping at the first failure.
  drain(): Promise<void>;
  /// Adopt chunks a previous session left behind, and continue their numbering.
  restore(): Promise<void>;
  /// Sequences still held locally - what a finalise that reported a gap is asking about.
  pendingSequences(): Promise<number[]>;
  highestSequence(): number;
}

/// Which sequences never arrived, given what the server has and the highest it saw. Pure, and shared
/// with the finalise-retry path so both sides agree on what "a gap" means.
export function missingSequences(received: number[], highest: number): number[] {
  if (highest < 0) return [];
  const have = new Set(received);
  const gaps: number[] = [];
  for (let s = 0; s <= highest; s++) if (!have.has(s)) gaps.push(s);
  return gaps;
}

export function createChunkQueue(deps: {
  upload: (chunk: StoredChunk) => Promise<void>;
  store: ChunkStore;
}): ChunkQueue {
  const { upload, store } = deps;
  // The in-memory mirror is the source of truth for ordering; the store is the crash survivor. Keeping
  // both means a store that throws costs durability and nothing else.
  const pending = new Map<number, StoredChunk>();
  let next = 0;
  let draining = false;

  const safe = async (fn: () => Promise<void>) => {
    try {
      await fn();
    } catch {
      // Best-effort persistence: never let a storage hiccup break recording.
    }
  };

  return {
    highestSequence: () => next - 1,

    async enqueue(blob, startMs, endMs) {
      const chunk: StoredChunk = { sequence: next++, blob, startMs, endMs };
      pending.set(chunk.sequence, chunk);
      await safe(() => store.put(chunk));
    },

    async restore() {
      let rows: StoredChunk[] = [];
      try {
        rows = await store.all();
      } catch {
        return; // no store, nothing to recover
      }
      for (const row of rows) pending.set(row.sequence, row);
      // Continue past whatever the previous session reached, so a recovered chunk can never collide
      // with a new one.
      for (const row of rows) next = Math.max(next, row.sequence + 1);
    },

    async pendingSequences() {
      return [...pending.keys()].sort((a, b) => a - b);
    },

    async drain() {
      if (draining) return;
      draining = true;
      try {
        const queued = [...pending.values()].sort((a, b) => a.sequence - b.sequence);
        for (const chunk of queued) {
          try {
            await upload(chunk);
          } catch {
            // Stop at the first failure rather than skipping ahead: uploading a later chunk now would
            // leave a hole the server refuses to finalise around.
            return;
          }
          pending.delete(chunk.sequence);
          await safe(() => store.remove(chunk.sequence));
        }
      } finally {
        draining = false;
      }
    },
  };
}

// ---- IndexedDB-backed store ----

const DB_NAME = "diariz";
const STORE = "live-chunks";

function openDb(): Promise<IDBDatabase> | null {
  if (typeof indexedDB === "undefined") return null;
  return new Promise((resolve, reject) => {
    // Version 2: the database already exists at version 1 for pending-recordings, so this store is
    // added in an upgrade rather than a fresh create.
    const req = indexedDB.open(DB_NAME, 2);
    req.onupgradeneeded = () => {
      if (!req.result.objectStoreNames.contains("pending-recordings")) {
        req.result.createObjectStore("pending-recordings", { keyPath: "userId" });
      }
      if (!req.result.objectStoreNames.contains(STORE)) {
        req.result.createObjectStore(STORE, { keyPath: "sequence" });
      }
    };
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
}

async function withStore<T>(
  mode: IDBTransactionMode,
  fn: (store: IDBObjectStore) => IDBRequest,
): Promise<T | null> {
  const dbp = openDb();
  if (!dbp) return null;
  const db = await dbp;
  return new Promise<T | null>((resolve, reject) => {
    const tx = db.transaction(STORE, mode);
    const req = fn(tx.objectStore(STORE));
    req.onsuccess = () => resolve((req.result as T) ?? null);
    req.onerror = () => reject(req.error);
  });
}

/// The real store. Every method may throw; `createChunkQueue` treats that as "no durability".
export const indexedDbChunkStore: ChunkStore = {
  async put(chunk) {
    await withStore("readwrite", (s) => s.put(chunk));
  },
  async remove(sequence) {
    await withStore("readwrite", (s) => s.delete(sequence));
  },
  async all() {
    return (await withStore<StoredChunk[]>("readonly", (s) => s.getAll())) ?? [];
  },
};
