"use strict";

// When the shell should stop believing the web app is there to drive. `main.js` owns the flag
// (`setRecorderReady`); which `webContents` events clear it lives here so the rule is stated once,
// unit-tested, and hard to re-break - like recorderState/screenshotState/pickerPool alongside it.
//
// "Ready" means: a document is loaded whose Recorder has mounted and reported in. It is the gate on
// tray-driven recording AND, through `canCapture`, on every screenshot affordance - the hotkey, the
// tray items, the pre-warmed overlay pool, and "Change capture area". A flag that goes false while
// the recorder is in fact still running is therefore not a cosmetic bug: it makes those controls
// silently inert for the rest of the take, with nothing on screen to say why.
//
// Only a *committed main-frame navigation* replaces the document, and only that unmounts the
// recorder. `did-start-loading` looks like the same thing and is not: Chromium raises it for
// same-document navigation (react-router's pushState - i.e. any in-app navigation), for subframe
// loads, and for off-origin navigations `main.js` itself aborts in `will-navigate`. The document
// survives all of those, so the recorder does too. See rendererReadiness.test.js for the measured
// event-by-event breakdown this list is derived from.
const RENDERER_INVALIDATING_EVENTS = [
  // A new document has committed in the main frame (first load, reload, cross-document navigation).
  // The recorder that reported in is gone; the next one re-reports on mount.
  "did-navigate",
  // The renderer process died or was killed - nothing left to send a tray command to.
  "render-process-gone",
];

module.exports = { RENDERER_INVALIDATING_EVENTS };
