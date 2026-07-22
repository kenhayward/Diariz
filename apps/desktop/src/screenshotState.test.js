"use strict";

const test = require("node:test");
const assert = require("node:assert");
const {
  DEFAULT_ACCELERATOR,
  CAPTURE_COOLDOWN_MS,
  trayScreenshotItems,
  isValidAccelerator,
  normalizeAccelerator,
  canCapture,
  shouldStartCapture,
  notificationForCaptureFailure,
  notificationForHotkeyUnavailable,
} = require("./screenshotState");

test("the default accelerator is a valid one", () => {
  assert.equal(isValidAccelerator(DEFAULT_ACCELERATOR), true);
});

test("no screenshot items exist while idle", () => {
  assert.deepEqual(trayScreenshotItems({ phase: "idle", ready: true }), []);
});

test("no screenshot items exist while uploading", () => {
  assert.deepEqual(trayScreenshotItems({ phase: "uploading" }), []);
});

test("capture and change-area items appear while recording and ready", () => {
  const items = trayScreenshotItems({ phase: "recording", source: "mic", ready: true });
  assert.deepEqual(items.map((i) => i.id), ["capture", "change-area"]);
  assert.ok(items.every((i) => i.enabled));
  assert.equal(items[0].label, "Capture Screenshot");
  assert.equal(items[1].label, "Change Capture Area...");
});

test("no screenshot items exist while recording but not ready (e.g. a mid-recording renderer reload)", () => {
  assert.deepEqual(trayScreenshotItems({ phase: "recording", source: "mic", ready: false }), []);
});

test("canCapture requires both the recording phase and a ready renderer", () => {
  assert.equal(canCapture({ phase: "recording", ready: true }), true);
  assert.equal(canCapture({ phase: "recording", ready: false }), false);
  assert.equal(canCapture({ phase: "idle", ready: true }), false);
  assert.equal(canCapture(null), false);
  assert.equal(canCapture(undefined), false);
});

test("shouldStartCapture allows a first capture when nothing is in flight and none has run yet", () => {
  assert.equal(shouldStartCapture({ inFlight: false, lastCaptureAt: 0 }, 1_000), true);
});

test("shouldStartCapture blocks while a capture is already in flight", () => {
  assert.equal(shouldStartCapture({ inFlight: true, lastCaptureAt: 0 }, 1_000), false);
});

test("shouldStartCapture blocks a repeat inside the cooldown window (defeats hotkey auto-repeat)", () => {
  const now = 10_000;
  assert.equal(shouldStartCapture({ inFlight: false, lastCaptureAt: now }, now + CAPTURE_COOLDOWN_MS - 1), false);
});

test("shouldStartCapture allows a capture once the cooldown has fully elapsed", () => {
  const now = 10_000;
  assert.equal(shouldStartCapture({ inFlight: false, lastCaptureAt: now }, now + CAPTURE_COOLDOWN_MS), true);
});

test("shouldStartCapture allows two deliberate captures a full second apart", () => {
  const now = 10_000;
  assert.ok(CAPTURE_COOLDOWN_MS < 1_000, "cooldown must be under a second so deliberate presses are never swallowed");
  assert.equal(shouldStartCapture({ inFlight: false, lastCaptureAt: now }, now + 1_000), true);
});

test("notificationForCaptureFailure gives a titled, dash-free message for a thrown error", () => {
  const note = notificationForCaptureFailure("error");
  assert.equal(note.title, "Diariz");
  assert.ok(note.body.length > 0);
  assert.ok(!/[–—]/.test(note.body), "no em or en dashes in user-facing text");
});

test("notificationForCaptureFailure gives a distinct message when the display is unavailable", () => {
  const note = notificationForCaptureFailure("unavailable");
  assert.ok(!/[–—]/.test(note.body), "no em or en dashes in user-facing text");
  assert.notEqual(note.body, notificationForCaptureFailure("error").body);
});

test("an accelerator with no modifier is rejected", () => {
  assert.equal(isValidAccelerator("S"), false);
});

test("an accelerator with no key is rejected", () => {
  assert.equal(isValidAccelerator("Control+Shift"), false);
});

test("an empty accelerator is rejected", () => {
  assert.equal(isValidAccelerator("   "), false);
});

test("normalizeAccelerator tidies casing and spacing", () => {
  assert.equal(normalizeAccelerator(" control + shift + p "), "Control+Shift+P");
});

test("normalizeAccelerator returns null for an invalid combination", () => {
  assert.equal(normalizeAccelerator("Shift"), null);
});

// This window is the first place raw user input (over IPC from the renderer) reaches
// normalizeAccelerator/isValidAccelerator - every prior caller passed an internal
// constant or a value normalizeAccelerator had already produced. A stray double
// separator must not silently collapse into a valid-looking accelerator, and a
// repeated modifier must not validate just because it's present at least once.

test("a stray double separator is rejected rather than silently collapsed", () => {
  assert.equal(isValidAccelerator("Control++9"), false);
  assert.equal(normalizeAccelerator("Control++9"), null);
});

test("a leading separator is rejected", () => {
  assert.equal(isValidAccelerator("+Control+9"), false);
  assert.equal(normalizeAccelerator("+Control+9"), null);
});

test("a trailing separator is rejected", () => {
  assert.equal(isValidAccelerator("Control+9+"), false);
  assert.equal(normalizeAccelerator("Control+9+"), null);
});

test("a duplicated modifier is rejected rather than counted by membership", () => {
  assert.equal(isValidAccelerator("Shift+Shift+9"), false);
  assert.equal(normalizeAccelerator("Shift+Shift+9"), null);
});

test("a duplicated modifier is rejected case-insensitively", () => {
  assert.equal(isValidAccelerator("shift+Shift+9"), false);
});

test("notificationForHotkeyUnavailable gives a titled, dash-free message", () => {
  const note = notificationForHotkeyUnavailable();
  assert.equal(note.title, "Diariz");
  assert.ok(note.body.length > 0);
  assert.ok(!/[–—]/.test(note.body), "no em or en dashes in user-facing text");
});
