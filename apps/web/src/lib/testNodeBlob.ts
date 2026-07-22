/// Opt-in test helper: makes IndexedDB-backed Blob round-trips survive under vitest/jsdom.
///
/// fake-indexeddb clones stored values with the platform's structuredClone, which V8 only knows how to
/// serialize for objects it recognizes as "host objects" - jsdom's own Blob class isn't one, so a Blob
/// stashed via IndexedDB comes back as an empty plain object (no .type, no .arrayBuffer()). Swapping in
/// Node's native Blob (structurally/behaviourally identical for our uses) makes it survive the clone.
/// See https://github.com/jsdom/jsdom/issues/3363 and the fake-indexeddb README's "structuredClone" note.
///
/// This is deliberately scoped per test file (vi.stubGlobal + afterEach unstub) rather than wired into
/// test-setup.ts: jsdom's own Blob is required everywhere else - e.g. jsdom's FormData rejects a
/// cross-realm (Node) Blob under its WebIDL brand check, which api.ts's upload paths rely on - so
/// swapping it globally for all ~150 test files would silently break real Blob/FormData usage in tests
/// that never asked for this workaround. Call `useNodeBlobForIndexedDb()` once at the top of a test file
/// that round-trips Blobs through keyedStash/pendingNotes/pendingScreenshots. See clipboard.test.ts for
/// the same vi.stubGlobal("Blob", ...) pattern applied inline.
import { afterEach, beforeEach, vi } from "vitest";
import { Blob as NodeBlob } from "node:buffer";

export function useNodeBlobForIndexedDb(): void {
  beforeEach(() => {
    vi.stubGlobal("Blob", NodeBlob);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });
}
