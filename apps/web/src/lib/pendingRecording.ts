/// Durable stash for a just-finished recording, so the audio is never lost if the upload fails (e.g. the
/// session expired during a long meeting and Stop lands on a 401). The Blob is written to IndexedDB the
/// moment recording stops — before the upload — and cleared on a successful upload. It's keyed by user id
/// so it's only ever offered back to the same person on this browser.
///
/// All operations degrade to a no-op / null when IndexedDB is unavailable (e.g. jsdom in tests, or a
/// locked-down browser), so persistence is strictly best-effort and never blocks recording.

import type { RecordingSource } from "./types";

export interface PendingRecording {
  userId: string;
  blob: Blob;
  title: string;
  durationMs: number;
  source: RecordingSource;
  createdAt: number;
}

const DB_NAME = "diariz";
const STORE = "pending-recordings";

function openDb(): Promise<IDBDatabase> | null {
  if (typeof indexedDB === "undefined") return null;
  return new Promise((resolve, reject) => {
    const req = indexedDB.open(DB_NAME, 1);
    req.onupgradeneeded = () => {
      if (!req.result.objectStoreNames.contains(STORE)) {
        req.result.createObjectStore(STORE, { keyPath: "userId" });
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
  try {
    const db = await dbp;
    return await new Promise<T | null>((resolve, reject) => {
      const tx = db.transaction(STORE, mode);
      const req = fn(tx.objectStore(STORE));
      req.onsuccess = () => resolve((req.result as T) ?? null);
      req.onerror = () => reject(req.error);
    });
  } catch {
    return null; // best-effort: never let a storage hiccup break recording
  }
}

export async function savePendingRecording(rec: PendingRecording): Promise<void> {
  await withStore("readwrite", (s) => s.put(rec));
}

export async function loadPendingRecording(userId: string): Promise<PendingRecording | null> {
  return withStore<PendingRecording>("readonly", (s) => s.get(userId));
}

export async function clearPendingRecording(userId: string): Promise<void> {
  await withStore("readwrite", (s) => s.delete(userId));
}
