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

// Fail a test that lets a React state update escape act(...). These warnings do not merely make a passing
// run noisy: each one is async work landing outside the window the test was awaiting, which is the raw
// material a flake is made from. The count grew from 94 to 143 over a fortnight because nothing stopped it,
// so the guard is the ratchet that keeps it at zero.
//
// It hooks console.error rather than reading printed output deliberately: vitest prints nothing a test logs
// on the Windows dev machine (issue #667), so an output-scraping check would pass locally while the warnings
// carried on. Intercepting the call itself works on both platforms.
//
// The warning fires from a microtask after the test body returns, which still lands before afterEach runs -
// so the test that caused it is the test that fails, and the message carries the source line that updated.
import { afterEach } from "vitest";

/// Errors the code under test is *meant* to log, declared by the test that provokes that path. They are
/// swallowed rather than printed, so a passing run stays silent.
///
/// A test could spy on console.error to the same effect, but that would take ownership of console.error
/// away from the act guard below for the length of that test, and an update escaping there would go
/// unnoticed. Declaring the expectation keeps one owner, and says out loud which error is expected.
const expectedErrors: RegExp[] = [];
export function expectsConsoleError(pattern: RegExp): void {
  expectedErrors.push(pattern);
}

const ACT_WARNING = "not wrapped in act";
const escapedActUpdates: string[] = [];
const passThroughError = console.error;

console.error = (...args: unknown[]) => {
  if (typeof args[0] === "string" && args[0].includes(ACT_WARNING)) {
    const origin = (new Error().stack || "")
      .split("\n")
      .map((line) => line.trim())
      .filter((line) => line.includes("/src/") && !line.includes("test-setup"))
      .slice(0, 3);
    escapedActUpdates.push(origin.length > 0 ? origin.join(" <- ") : "origin unavailable");
    return;
  }
  // Checked after the act guard, never before: a broad pattern must not be able to silence an escaped
  // update by accident.
  const rendered = args.map((a) => (a instanceof Error ? a.message : String(a))).join(" ");
  if (expectedErrors.some((pattern) => pattern.test(rendered))) return;
  passThroughError(...args);
};

afterEach(() => {
  expectedErrors.length = 0;
  if (escapedActUpdates.length === 0) return;
  const escaped = escapedActUpdates.splice(0, escapedActUpdates.length);
  throw new Error(
    `${escaped.length} React state update(s) escaped act(...):\n  ${escaped.join("\n  ")}\n\n` +
      "Wrap whatever triggered the update in act(), or await the work before the test ends.",
  );
});
