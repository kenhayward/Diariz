/// Generic IndexedDB plumbing shared by the client-side "pending" stashes (pendingNotes,
/// pendingScreenshots): a single object store, keyed by one string field (typically a user id),
/// holding one record per key. Each caller gets its own database/store name (never a shared DB - adding
/// a store to an existing DB would force a version bump across every module using it) but the
/// open/transact/error-handling logic is identical, so it lives here once. Every operation degrades to a
/// no-op when IndexedDB is unavailable (private browsing, old browsers, non-browser test runners that
/// don't register a fake) or when the underlying request fails - callers depend on this: a storage
/// hiccup must never break the feature (note-taking, screenshot capture) built on top of it.

export interface KeyedStash<T> {
  save(value: T): Promise<void>;
  load(key: string): Promise<T | null>;
  clear(key: string): Promise<void>;
}

function openDb<T>(
  dbName: string,
  storeName: string,
  keyPath: keyof T & string,
): Promise<IDBDatabase> | null {
  if (typeof indexedDB === "undefined") return null;
  return new Promise((resolve, reject) => {
    const req = indexedDB.open(dbName, 1);
    req.onupgradeneeded = () => {
      if (!req.result.objectStoreNames.contains(storeName)) {
        req.result.createObjectStore(storeName, { keyPath });
      }
    };
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
}

/// Builds a stash backed by its own IndexedDB database. `keyPath` is the field on `T` that identifies a
/// record (each save fully replaces any existing record under that key's value) - constrained to an
/// actual key of `T` so a typo'd field name is a compile error rather than a silent no-op at runtime.
export function createKeyedStash<T>(
  dbName: string,
  storeName: string,
  keyPath: keyof T & string,
): KeyedStash<T> {
  async function withStore<R>(
    mode: IDBTransactionMode,
    fn: (store: IDBObjectStore) => IDBRequest,
  ): Promise<R | null> {
    try {
      // openDb() itself is called inside this try (not just its result awaited): merely touching the
      // `indexedDB` global can throw synchronously in some locked-down contexts (e.g. a sandboxed iframe
      // whose accessor raises a SecurityError), before openDb ever reaches its own Promise wrapping. That
      // throw must degrade to a no-op exactly like every other storage hiccup here - callers depend on
      // this stash never surfacing a rejection.
      const dbp = openDb(dbName, storeName, keyPath);
      if (!dbp) return null;
      const db = await dbp;
      return await new Promise<R | null>((resolve, reject) => {
        const tx = db.transaction(storeName, mode);
        const req = fn(tx.objectStore(storeName));
        req.onsuccess = () => resolve((req.result as R) ?? null);
        req.onerror = () => reject(req.error);
      });
    } catch {
      return null; // best-effort: never let a storage hiccup break the caller's feature
    }
  }

  return {
    async save(value: T): Promise<void> {
      await withStore("readwrite", (s) => s.put(value));
    },
    async load(key: string): Promise<T | null> {
      return withStore<T>("readonly", (s) => s.get(key));
    },
    async clear(key: string): Promise<void> {
      await withStore("readwrite", (s) => s.delete(key));
    },
  };
}
