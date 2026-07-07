/// Durable stash for live-recording note lines, so a crash/session lapse never loses them. Mirrors the
/// pendingRecording pattern but in its own database (`diariz-notes`) - adding a store to the existing
/// `diariz` DB would force a version bump across modules. Keyed by user id. `recordingId` is null while
/// the recording is still in progress; it is set when the audio uploaded but the notes attach failed, so
/// the retry banner knows where the lines belong. All operations degrade to no-ops without IndexedDB.

export interface PendingNoteLine {
  text: string;
  capturedAtMs: number | null;
}

export interface PendingNotes {
  userId: string;
  lines: PendingNoteLine[];
  /// Null while recording; the created recording's id once audio uploaded but the notes attach failed.
  recordingId: string | null;
  updatedAt: number;
}

const DB_NAME = "diariz-notes";
const STORE = "pending-notes";

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
    return null; // best-effort: never let a storage hiccup break note-taking
  }
}

export async function savePendingNotes(notes: PendingNotes): Promise<void> {
  await withStore("readwrite", (s) => s.put(notes));
}

export async function loadPendingNotes(userId: string): Promise<PendingNotes | null> {
  return withStore<PendingNotes>("readonly", (s) => s.get(userId));
}

export async function clearPendingNotes(userId: string): Promise<void> {
  await withStore("readwrite", (s) => s.delete(userId));
}
