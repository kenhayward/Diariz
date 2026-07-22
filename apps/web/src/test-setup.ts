// jsdom has no IndexedDB implementation; the pendingNotes/pendingScreenshots stashes need one to run
// under vitest. Registered globally as a side effect, same as i18n below.
//
// Note: jsdom/V8's structuredClone strips Blob payloads stashed via IndexedDB down to empty objects
// (see keyedStash.test.ts / testNodeBlob.ts for the full explanation). That workaround is deliberately
// NOT applied here - it swaps the global Blob implementation, which breaks jsdom's FormData brand check
// used by real upload code. It is opted into per-file by the handful of tests that need it.
import "fake-indexeddb/auto";

// Initialise i18next (side-effect) before any test renders a component that calls useTranslation, and
// pin the language to English so assertions against English UI text are stable.
import i18n from "./lib/i18n";

i18n.changeLanguage("en");

// jsdom doesn't implement Element.scrollIntoView; components that scroll to a segment (RecordingDetail's
// transcript deep-link) call it in an effect, which otherwise throws an unhandled error and fails the run.
if (!Element.prototype.scrollIntoView) {
  Element.prototype.scrollIntoView = () => {};
}
