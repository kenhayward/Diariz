"use strict";

// Regression guard for "Change capture area does nothing": the shell gates every screenshot
// affordance on `canCapture(recorder)`, which requires `recorder.ready`. Readiness used to be
// dropped on the main window's `did-start-loading`, which Chromium raises for SAME-DOCUMENT
// navigations too - so one react-router route change mid-recording (clicking a recording, opening
// the calendar) marked the still-mounted recorder as gone. Nothing set it back, because the web app
// only reports readiness when it mounts, so for the rest of that take the hotkey was unregistered,
// the overlay pool torn down, the tray items gone, and the popover's button a silent no-op that
// still highlighted on hover.
//
// Measured on Electron 43 (a probe subscribing to every candidate event), for a window that keeps
// its document:
//   pushState / hash change ...... did-start-loading, did-start-navigation(sameDocument), did-navigate-in-page
//   subframe (iframe) load ....... did-start-loading, did-start-navigation(isMainFrame=false)
//   aborted off-origin link ...... did-start-loading, did-start-navigation, will-navigate
//   download via <a download> .... (none)
// and only a real document commit (first load, reload, cross-document navigation) adds `did-navigate`.
// Hence the list below, and hence the guard that `did-start-loading` never comes back.

const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const { RENDERER_INVALIDATING_EVENTS } = require("./rendererReadiness");

const KEEPS_THE_DOCUMENT = [
  "did-start-loading",
  "did-stop-loading",
  "did-start-navigation",
  "did-navigate-in-page",
  "will-navigate",
];

test("a committed main-frame navigation invalidates the renderer", () => {
  assert.ok(
    RENDERER_INVALIDATING_EVENTS.includes("did-navigate"),
    "a reload/cross-document navigation replaces the document, so the recorder that reported in is gone",
  );
});

test("a dead renderer is not ready either", () => {
  assert.ok(RENDERER_INVALIDATING_EVENTS.includes("render-process-gone"));
});

test("events that leave the document (and the recorder) in place do not invalidate it", () => {
  for (const event of KEEPS_THE_DOCUMENT) {
    assert.ok(
      !RENDERER_INVALIDATING_EVENTS.includes(event),
      `${event} fires for in-app navigation/subframes/aborted loads - the recorder is still mounted`,
    );
  }
});

test("main.js drops readiness on exactly these events, and never on the loading state", () => {
  const main = fs.readFileSync(path.join(__dirname, "main.js"), "utf8");
  assert.ok(
    /RENDERER_INVALIDATING_EVENTS[\s\S]{0,200}?setRecorderReady\(false\)/.test(main),
    "main.js must subscribe the listed events to setRecorderReady(false)",
  );
  // Those events may legitimately be listened for (`will-navigate` sends off-origin links to the
  // browser) - what they must never do is clear readiness.
  for (const event of KEEPS_THE_DOCUMENT) {
    const listener = new RegExp(`on\\("${event}"[\\s\\S]{0,200}?setRecorderReady`);
    assert.ok(
      !listener.test(main),
      `${event} must not clear readiness - see this file's header for what it actually fires on`,
    );
  }
  assert.ok(
    !main.includes('"did-start-loading"'),
    "nothing in the shell should key off the loading state - it is true for in-app navigation too",
  );
});
