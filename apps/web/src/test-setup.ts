// jsdom has no IndexedDB implementation; the pendingNotes/pendingScreenshots stashes need one to run
// under vitest. Registered globally as a side effect, same as i18n below.
import "fake-indexeddb/auto";

// fake-indexeddb clones stored values with the platform's structuredClone, which V8 only knows how to
// serialize for objects it recognizes as "host objects" - jsdom's own Blob class isn't one, so a Blob
// stashed via IndexedDB comes back as an empty plain object (no .type, no .arrayBuffer()). Swapping in
// Node's native Blob (structurally/behaviourally identical for our uses) makes it survive the clone.
// See https://github.com/jsdom/jsdom/issues/3363 and the fake-indexeddb README's "structuredClone" note.
import { Blob as NodeBlob } from "node:buffer";
globalThis.Blob = NodeBlob as unknown as typeof Blob;

// Initialise i18next (side-effect) before any test renders a component that calls useTranslation, and
// pin the language to English so assertions against English UI text are stable.
import i18n from "./lib/i18n";

i18n.changeLanguage("en");

// jsdom doesn't implement Element.scrollIntoView; components that scroll to a segment (RecordingDetail's
// transcript deep-link) call it in an effect, which otherwise throws an unhandled error and fails the run.
if (!Element.prototype.scrollIntoView) {
  Element.prototype.scrollIntoView = () => {};
}
