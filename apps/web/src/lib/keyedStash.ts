/// Generic IndexedDB plumbing shared by the client-side "pending" stashes (pendingNotes,
/// pendingScreenshots): the open/transact/error-handling logic is identical across them, so it lives
/// once here. Every operation degrades to a no-op when IndexedDB is unavailable (private browsing, old
/// browsers, non-browser test runners that don't register a fake) or when the underlying request fails -
/// callers depend on this: a storage hiccup must never break the feature (note-taking, screenshot
/// capture) built on top of it.
///
/// Two shapes are exported:
///  - `createKeyedStash`: one record per key, each save fully replacing the prior record under that key
///    (pendingNotes - a whole meeting's worth of note lines is small, so rewriting it wholesale is fine).
///  - `createMultiKeyedStash`: many records per key, added/removed one at a time (pendingScreenshots -
///    each capture carries full-resolution image Blobs, so a whole-set rewrite on every capture is the
///    O(n^2) write-churn bug this shape exists to avoid). Records are looked up by key via a non-unique
///    index rather than folded into the primary key, so a single record can still be deleted by its own
///    primary key without knowing which user it belongs to.
/// Each caller gets its own database/store name (never a shared DB - adding a store to an existing DB
/// would force a version bump across every module using it).

function openDb(dbName: string, upgrade: (db: IDBDatabase) => void): Promise<IDBDatabase> | null {
  if (typeof indexedDB === "undefined") return null;
  return new Promise((resolve, reject) => {
    const req = indexedDB.open(dbName, 1);
    req.onupgradeneeded = () => upgrade(req.result);
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
}

async function withStore<R>(
  dbName: string,
  storeName: string,
  upgrade: (db: IDBDatabase) => void,
  mode: IDBTransactionMode,
  fn: (store: IDBObjectStore) => IDBRequest,
): Promise<R | null> {
  try {
    // openDb() itself is called inside this try (not just its result awaited): merely touching the
    // `indexedDB` global can throw synchronously in some locked-down contexts (e.g. a sandboxed iframe
    // whose accessor raises a SecurityError), before openDb ever reaches its own Promise wrapping. That
    // throw must degrade to a no-op exactly like every other storage hiccup here - callers depend on
    // this stash never surfacing a rejection.
    const dbp = openDb(dbName, upgrade);
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

/// Deletes every record whose `indexName` value equals `key`, via a cursor (IDBObjectStore.delete only
/// takes a primary key or a primary-key range, not an index range, so a bulk delete-by-index needs a
/// cursor walk rather than a single request).
async function clearByIndex(
  dbName: string,
  storeName: string,
  upgrade: (db: IDBDatabase) => void,
  indexName: string,
  key: string,
): Promise<void> {
  try {
    const dbp = openDb(dbName, upgrade);
    if (!dbp) return;
    const db = await dbp;
    await new Promise<void>((resolve, reject) => {
      const tx = db.transaction(storeName, "readwrite");
      const req = tx.objectStore(storeName).index(indexName).openCursor(IDBKeyRange.only(key));
      req.onsuccess = () => {
        const cursor = req.result;
        if (cursor) {
          cursor.delete();
          cursor.continue();
        } else {
          resolve();
        }
      };
      req.onerror = () => reject(req.error);
    });
  } catch {
    /* best-effort: never let a storage hiccup break the caller's feature */
  }
}

export interface KeyedStash<T> {
  save(value: T): Promise<void>;
  load(key: string): Promise<T | null>;
  clear(key: string): Promise<void>;
}

/// Builds a stash backed by its own IndexedDB database. `keyPath` is the field on `T` that identifies a
/// record (each save fully replaces any existing record under that key's value) - constrained to an
/// actual key of `T` so a typo'd field name is a compile error rather than a silent no-op at runtime.
export function createKeyedStash<T>(
  dbName: string,
  storeName: string,
  keyPath: keyof T & string,
): KeyedStash<T> {
  const upgrade = (db: IDBDatabase) => {
    if (!db.objectStoreNames.contains(storeName)) {
      db.createObjectStore(storeName, { keyPath });
    }
  };
  return {
    async save(value: T): Promise<void> {
      await withStore(dbName, storeName, upgrade, "readwrite", (s) => s.put(value));
    },
    async load(key: string): Promise<T | null> {
      return withStore<T>(dbName, storeName, upgrade, "readonly", (s) => s.get(key));
    },
    async clear(key: string): Promise<void> {
      await withStore(dbName, storeName, upgrade, "readwrite", (s) => s.delete(key));
    },
  };
}

export interface MultiKeyedStash<T> {
  /// Insert or replace one record. `value` must carry both the primary key field and the index field.
  add(value: T): Promise<void>;
  /// Every record whose index field equals `key`. Order is unspecified (a non-unique index ties on
  /// insertion order of the *primary* key, not capture order) - callers that care about order should
  /// sort by their own timestamp field after listing.
  list(key: string): Promise<T[]>;
  /// Delete exactly one record by its primary key.
  remove(primaryKey: string): Promise<void>;
  /// Delete every record whose index field equals `key`.
  clear(key: string): Promise<void>;
}

/// Builds a stash of many-records-per-key backed by its own IndexedDB database: `primaryKeyPath` uniquely
/// identifies one record (e.g. a per-capture id) so a single item can be added/removed without rewriting
/// its neighbours, and `indexKeyPath` (e.g. a user id) is used to list/clear every record for a key via a
/// non-unique index rather than folding both fields into a compound primary key.
export function createMultiKeyedStash<T>(
  dbName: string,
  storeName: string,
  primaryKeyPath: keyof T & string,
  indexKeyPath: keyof T & string,
): MultiKeyedStash<T> {
  const indexName = `by_${indexKeyPath}`;
  const upgrade = (db: IDBDatabase) => {
    if (!db.objectStoreNames.contains(storeName)) {
      db.createObjectStore(storeName, { keyPath: primaryKeyPath }).createIndex(indexName, indexKeyPath);
    }
  };
  return {
    async add(value: T): Promise<void> {
      await withStore(dbName, storeName, upgrade, "readwrite", (s) => s.put(value));
    },
    async list(key: string): Promise<T[]> {
      return (
        (await withStore<T[]>(dbName, storeName, upgrade, "readonly", (s) =>
          s.index(indexName).getAll(IDBKeyRange.only(key)),
        )) ?? []
      );
    },
    async remove(primaryKey: string): Promise<void> {
      await withStore(dbName, storeName, upgrade, "readwrite", (s) => s.delete(primaryKey));
    },
    async clear(key: string): Promise<void> {
      await clearByIndex(dbName, storeName, upgrade, indexName, key);
    },
  };
}
