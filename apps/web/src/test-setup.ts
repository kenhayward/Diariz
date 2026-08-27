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

// Give testing-library's async utilities the same budget vitest.config.ts already gives a test.
//
// `testTimeout` was raised to 20s there because the default flaked under contention - but it does not
// govern `findBy*` or `waitFor`, which have their own `asyncUtilTimeout` and were left on 1000ms. Almost
// every wait in this suite goes through those, so the fix did not reach the assertions it was written for.
//
// Deliberately well below `testTimeout`: exceeding this reports "unable to find an element" and names what
// it looked for, which is a far better failure than an opaque test timeout.
// From @testing-library/dom, which owns this setting, rather than the react wrapper that re-exports
// it: setup runs for every test file, and pulling React and react-dom/test-utils into the 122 that
// never render cost ~30s of the suite's CPU when this was first written that way.
import { configure } from "@testing-library/dom";

configure({ asyncUtilTimeout: 5000 });

// jsdom doesn't implement Element.scrollIntoView; components that scroll to a segment (RecordingDetail's
// transcript deep-link) call it in an effect, which otherwise throws an unhandled error and fails the run.
if (!Element.prototype.scrollIntoView) {
  Element.prototype.scrollIntoView = () => {};
}
