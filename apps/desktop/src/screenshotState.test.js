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
  acceleratorKeyFromDomKey,
  acceleratorFromKeyDescriptor,
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

// acceleratorKeyFromDomKey / acceleratorFromKeyDescriptor - the hotkey capture window
// reports raw DOM KeyboardEvent data (ctrlKey/metaKey/altKey/shiftKey + e.key). DOM key
// names don't always match Electron's accelerator vocabulary (see
// electron/shell/common/keyboard_util.cc's KeyboardCodeFromKeyIdentifier table): the
// spacebar reports as a literal " " character and the arrow keys report as
// "ArrowUp"/"ArrowDown"/"ArrowLeft"/"ArrowRight" rather than Electron's "Up"/"Down"/
// "Left"/"Right". Translating that mismatch is the fix for this review finding.

test("acceleratorKeyFromDomKey maps the space key to Electron's Space token", () => {
  assert.equal(acceleratorKeyFromDomKey(" "), "Space");
});

test("acceleratorKeyFromDomKey maps DOM arrow key names to Electron's short names", () => {
  assert.equal(acceleratorKeyFromDomKey("ArrowUp"), "Up");
  assert.equal(acceleratorKeyFromDomKey("ArrowDown"), "Down");
  assert.equal(acceleratorKeyFromDomKey("ArrowLeft"), "Left");
  assert.equal(acceleratorKeyFromDomKey("ArrowRight"), "Right");
});

test("acceleratorKeyFromDomKey maps DOM volume key names to Electron's names", () => {
  assert.equal(acceleratorKeyFromDomKey("AudioVolumeUp"), "VolumeUp");
  assert.equal(acceleratorKeyFromDomKey("AudioVolumeDown"), "VolumeDown");
  assert.equal(acceleratorKeyFromDomKey("AudioVolumeMute"), "VolumeMute");
});

test("acceleratorKeyFromDomKey maps DOM media track key names to Electron's word order", () => {
  assert.equal(acceleratorKeyFromDomKey("MediaTrackNext"), "MediaNextTrack");
  assert.equal(acceleratorKeyFromDomKey("MediaTrackPrevious"), "MediaPreviousTrack");
});

test("acceleratorKeyFromDomKey maps a literal plus character to Electron's Plus token (avoids colliding with the + separator)", () => {
  assert.equal(acceleratorKeyFromDomKey("+"), "Plus");
});

test("acceleratorKeyFromDomKey passes through a key that already matches Electron's vocabulary", () => {
  assert.equal(acceleratorKeyFromDomKey("F5"), "F5");
  assert.equal(acceleratorKeyFromDomKey("Escape"), "Escape");
  assert.equal(acceleratorKeyFromDomKey("9"), "9");
  assert.equal(acceleratorKeyFromDomKey("a"), "A");
});

test("acceleratorFromKeyDescriptor builds Control+Shift+Space from a raw Ctrl+Shift+Space press, and it validates", () => {
  const accelerator = acceleratorFromKeyDescriptor({ ctrlKey: true, shiftKey: true, key: " " });
  assert.equal(accelerator, "Control+Shift+Space");
  assert.equal(isValidAccelerator(accelerator), true);
});

test("acceleratorFromKeyDescriptor builds Control+Shift+Up from a raw Ctrl+Shift+ArrowUp press, and it validates", () => {
  const accelerator = acceleratorFromKeyDescriptor({ ctrlKey: true, shiftKey: true, key: "ArrowUp" });
  assert.equal(accelerator, "Control+Shift+Up");
  assert.equal(isValidAccelerator(accelerator), true);
});

test("acceleratorFromKeyDescriptor builds Control+Shift+9 from a raw Ctrl+Shift+9 press (the default shape), and it validates", () => {
  const accelerator = acceleratorFromKeyDescriptor({ ctrlKey: true, shiftKey: true, key: "9" });
  assert.equal(accelerator, "Control+Shift+9");
  assert.equal(isValidAccelerator(accelerator), true);
});

test("acceleratorFromKeyDescriptor builds Control+Shift+F5 from a raw Ctrl+Shift+F5 press, and it validates", () => {
  const accelerator = acceleratorFromKeyDescriptor({ ctrlKey: true, shiftKey: true, key: "F5" });
  assert.equal(accelerator, "Control+Shift+F5");
  assert.equal(isValidAccelerator(accelerator), true);
});

test("acceleratorFromKeyDescriptor reports a bare modifier press as modifiers-only, which does not validate", () => {
  const accelerator = acceleratorFromKeyDescriptor({ ctrlKey: true, key: "Control" });
  assert.equal(accelerator, "Control");
  assert.equal(isValidAccelerator(accelerator), false);
});

test("acceleratorFromKeyDescriptor reports a key with no modifier held, which does not validate", () => {
  const accelerator = acceleratorFromKeyDescriptor({ key: "9" });
  assert.equal(accelerator, "9");
  assert.equal(isValidAccelerator(accelerator), false);
});

test("acceleratorFromKeyDescriptor with nothing pressed yet produces an empty, invalid string", () => {
  const accelerator = acceleratorFromKeyDescriptor({});
  assert.equal(accelerator, "");
  assert.equal(isValidAccelerator(accelerator), false);
});

test("acceleratorFromKeyDescriptor never produces a malformed string the validator would wrongly accept", () => {
  // Regression guard: the fix for Space/arrows must not reopen the empty-segment or
  // duplicate-modifier holes the previous pass closed.
  assert.equal(isValidAccelerator("Control++9"), false);
  assert.equal(isValidAccelerator("+Control+9"), false);
  assert.equal(isValidAccelerator("Control+9+"), false);
  assert.equal(isValidAccelerator("Shift+Shift+9"), false);
  assert.equal(isValidAccelerator("shift+Shift+9"), false);
});
