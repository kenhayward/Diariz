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
  acceleratorKeyFromDomCode,
  acceleratorFromKeyDescriptor,
  unsupportedKeyCaptureMessage,
  hotkeyUnavailableSaveError,
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

// acceleratorKeyFromDomCode / acceleratorFromKeyDescriptor - the hotkey capture window
// reports raw DOM KeyboardEvent data (ctrlKey/metaKey/altKey/shiftKey + e.code).
//
// Fix pass 2 (shifted-key mapping): the first fix pass keyed this translation on
// `KeyboardEvent.key`, the produced character. That is wrong for the entire number row and
// most punctuation: `key` reports the SHIFTED OUTPUT, so Ctrl+Shift+3 fires `key:"#"` and
// Ctrl+Shift+9 (the app's own default hotkey shape) fires `key:"("` on a US layout. The old
// table only rewrote a handful of known names and otherwise upper-cased whatever character
// it got, so "#" sailed through as a syntactically-valid-looking "Control+Shift+#" and only
// failed later at globalShortcut.register - the exact bug class this module exists to
// prevent, reopened for a very common key shape.
//
// `KeyboardEvent.code` reports the PHYSICAL key ("Digit3", "KeyA", "Equal", "Minus", ...)
// independent of modifiers and layout, so it's the correct source of truth. Cross-checked
// against Electron's documented accelerator key codes (0-9, A-Z, F1-F24, punctuation as its
// unshifted character, Space, arrows as Up/Down/Left/Right, Home/End/PageUp/PageDown,
// Escape, Tab, Backspace, Delete, Insert, the volume/media keys, and the num0-num9 /
// numadd/numsub/nummult/numdiv/numdec numpad tokens).

test("acceleratorKeyFromDomCode maps digit-row codes to the bare digit, independent of any shift", () => {
  for (let d = 0; d <= 9; d++) {
    assert.equal(acceleratorKeyFromDomCode(`Digit${d}`), String(d));
  }
});

test("acceleratorKeyFromDomCode maps letter codes to the bare uppercase letter", () => {
  assert.equal(acceleratorKeyFromDomCode("KeyA"), "A");
  assert.equal(acceleratorKeyFromDomCode("KeyM"), "M");
  assert.equal(acceleratorKeyFromDomCode("KeyZ"), "Z");
});

test("acceleratorKeyFromDomCode passes function-key codes through unchanged", () => {
  assert.equal(acceleratorKeyFromDomCode("F1"), "F1");
  assert.equal(acceleratorKeyFromDomCode("F5"), "F5");
  assert.equal(acceleratorKeyFromDomCode("F12"), "F12");
  assert.equal(acceleratorKeyFromDomCode("F24"), "F24");
});

test("acceleratorKeyFromDomCode maps the physical space bar to Electron's Space token", () => {
  assert.equal(acceleratorKeyFromDomCode("Space"), "Space");
});

test("acceleratorKeyFromDomCode maps DOM arrow codes to Electron's short names", () => {
  assert.equal(acceleratorKeyFromDomCode("ArrowUp"), "Up");
  assert.equal(acceleratorKeyFromDomCode("ArrowDown"), "Down");
  assert.equal(acceleratorKeyFromDomCode("ArrowLeft"), "Left");
  assert.equal(acceleratorKeyFromDomCode("ArrowRight"), "Right");
});

test("acceleratorKeyFromDomCode maps the remaining named navigation/edit keys straight through", () => {
  assert.equal(acceleratorKeyFromDomCode("Escape"), "Escape");
  assert.equal(acceleratorKeyFromDomCode("Tab"), "Tab");
  assert.equal(acceleratorKeyFromDomCode("Backspace"), "Backspace");
  assert.equal(acceleratorKeyFromDomCode("Delete"), "Delete");
  assert.equal(acceleratorKeyFromDomCode("Home"), "Home");
  assert.equal(acceleratorKeyFromDomCode("End"), "End");
  assert.equal(acceleratorKeyFromDomCode("PageUp"), "PageUp");
  assert.equal(acceleratorKeyFromDomCode("PageDown"), "PageDown");
  assert.equal(acceleratorKeyFromDomCode("Insert"), "Insert");
});

test("acceleratorKeyFromDomCode maps DOM volume key codes to Electron's names", () => {
  assert.equal(acceleratorKeyFromDomCode("AudioVolumeUp"), "VolumeUp");
  assert.equal(acceleratorKeyFromDomCode("AudioVolumeDown"), "VolumeDown");
  assert.equal(acceleratorKeyFromDomCode("AudioVolumeMute"), "VolumeMute");
});

test("acceleratorKeyFromDomCode maps DOM media track codes to Electron's word order", () => {
  assert.equal(acceleratorKeyFromDomCode("MediaTrackNext"), "MediaNextTrack");
  assert.equal(acceleratorKeyFromDomCode("MediaTrackPrevious"), "MediaPreviousTrack");
});

test("acceleratorKeyFromDomCode maps numpad digits and operators to Electron's num tokens", () => {
  assert.equal(acceleratorKeyFromDomCode("Numpad0"), "num0");
  assert.equal(acceleratorKeyFromDomCode("Numpad9"), "num9");
  assert.equal(acceleratorKeyFromDomCode("NumpadAdd"), "numadd");
  assert.equal(acceleratorKeyFromDomCode("NumpadSubtract"), "numsub");
  assert.equal(acceleratorKeyFromDomCode("NumpadMultiply"), "nummult");
  assert.equal(acceleratorKeyFromDomCode("NumpadDivide"), "numdiv");
  assert.equal(acceleratorKeyFromDomCode("NumpadDecimal"), "numdec");
});

test("acceleratorKeyFromDomCode maps punctuation codes to their unshifted character", () => {
  assert.equal(acceleratorKeyFromDomCode("Minus"), "-");
  assert.equal(acceleratorKeyFromDomCode("Equal"), "=");
  assert.equal(acceleratorKeyFromDomCode("BracketLeft"), "[");
  assert.equal(acceleratorKeyFromDomCode("BracketRight"), "]");
  assert.equal(acceleratorKeyFromDomCode("Semicolon"), ";");
  assert.equal(acceleratorKeyFromDomCode("Quote"), "'");
  assert.equal(acceleratorKeyFromDomCode("Comma"), ",");
  assert.equal(acceleratorKeyFromDomCode("Period"), ".");
  assert.equal(acceleratorKeyFromDomCode("Slash"), "/");
  assert.equal(acceleratorKeyFromDomCode("Backslash"), "\\");
  assert.equal(acceleratorKeyFromDomCode("Backquote"), "`");
});

test("acceleratorKeyFromDomCode returns null for a physical key Electron has no accelerator name for", () => {
  // The context-menu key and the extra ISO key have no entry in Electron's accelerator
  // vocabulary at all - this must be a clear "unsupported", never an invented name.
  assert.equal(acceleratorKeyFromDomCode("ContextMenu"), null);
  assert.equal(acceleratorKeyFromDomCode("IntlBackslash"), null);
});

test("acceleratorKeyFromDomCode returns null for missing/empty input", () => {
  assert.equal(acceleratorKeyFromDomCode(undefined), null);
  assert.equal(acceleratorKeyFromDomCode(""), null);
});

// acceleratorFromKeyDescriptor now reads `code` (not `key`) to find the base key, and
// returns { accelerator, unsupported } rather than a bare string - `unsupported` lets the
// caller show a distinct, accurate message for "Electron has no name for this physical key"
// instead of reusing the generic "needs a modifier and a key" copy (which would be actively
// misleading: the user DID press a modifier and a key).

test("acceleratorFromKeyDescriptor derives Control+Shift+3 from a realistic Ctrl+Shift+3 press (key is the shifted '#', code is the physical Digit3)", () => {
  const result = acceleratorFromKeyDescriptor({ ctrlKey: true, shiftKey: true, key: "#", code: "Digit3" });
  assert.deepEqual(result, { accelerator: "Control+Shift+3", unsupported: false });
  assert.equal(isValidAccelerator(result.accelerator), true);
});

test("acceleratorFromKeyDescriptor derives Control+Shift+9 from a realistic Ctrl+Shift+9 press (key is the shifted '(', code is the physical Digit9) - the default hotkey's own shape", () => {
  const result = acceleratorFromKeyDescriptor({ ctrlKey: true, shiftKey: true, key: "(", code: "Digit9" });
  assert.deepEqual(result, { accelerator: "Control+Shift+9", unsupported: false });
  assert.equal(isValidAccelerator(result.accelerator), true);
});

test("acceleratorFromKeyDescriptor derives Control+Shift+A from a realistic Ctrl+Shift+A press", () => {
  const result = acceleratorFromKeyDescriptor({ ctrlKey: true, shiftKey: true, key: "A", code: "KeyA" });
  assert.deepEqual(result, { accelerator: "Control+Shift+A", unsupported: false });
  assert.equal(isValidAccelerator(result.accelerator), true);
});

test("acceleratorFromKeyDescriptor derives Control+Shift+Space from a realistic Ctrl+Shift+Space press", () => {
  const result = acceleratorFromKeyDescriptor({ ctrlKey: true, shiftKey: true, key: " ", code: "Space" });
  assert.deepEqual(result, { accelerator: "Control+Shift+Space", unsupported: false });
  assert.equal(isValidAccelerator(result.accelerator), true);
});

test("acceleratorFromKeyDescriptor derives Control+Shift+Up from a realistic Ctrl+Shift+ArrowUp press", () => {
  const result = acceleratorFromKeyDescriptor({ ctrlKey: true, shiftKey: true, key: "ArrowUp", code: "ArrowUp" });
  assert.deepEqual(result, { accelerator: "Control+Shift+Up", unsupported: false });
  assert.equal(isValidAccelerator(result.accelerator), true);
});

test("acceleratorFromKeyDescriptor derives Control+Shift+F5 from a realistic Ctrl+Shift+F5 press", () => {
  const result = acceleratorFromKeyDescriptor({ ctrlKey: true, shiftKey: true, key: "F5", code: "F5" });
  assert.deepEqual(result, { accelerator: "Control+Shift+F5", unsupported: false });
  assert.equal(isValidAccelerator(result.accelerator), true);
});

test("acceleratorFromKeyDescriptor reports a bare modifier press as modifiers-only, which does not validate", () => {
  const result = acceleratorFromKeyDescriptor({ ctrlKey: true, key: "Control", code: "ControlLeft" });
  assert.deepEqual(result, { accelerator: "Control", unsupported: false });
  assert.equal(isValidAccelerator(result.accelerator), false);
});

test("acceleratorFromKeyDescriptor reports a key with no modifier held, which does not validate", () => {
  const result = acceleratorFromKeyDescriptor({ key: "9", code: "Digit9" });
  assert.deepEqual(result, { accelerator: "9", unsupported: false });
  assert.equal(isValidAccelerator(result.accelerator), false);
});

test("acceleratorFromKeyDescriptor with nothing pressed yet produces an empty, invalid string", () => {
  const result = acceleratorFromKeyDescriptor({});
  assert.deepEqual(result, { accelerator: "", unsupported: false });
  assert.equal(isValidAccelerator(result.accelerator), false);
});

test("acceleratorFromKeyDescriptor flags a physical key with no Electron accelerator name as unsupported, distinct from an ordinary invalid combination", () => {
  const result = acceleratorFromKeyDescriptor({ ctrlKey: true, shiftKey: true, key: "ContextMenu", code: "ContextMenu" });
  assert.deepEqual(result, { accelerator: "Control+Shift", unsupported: true });
  assert.equal(isValidAccelerator(result.accelerator), false);
});

test("acceleratorFromKeyDescriptor never produces a malformed string the validator would wrongly accept", () => {
  // Regression guard, rewritten to actually exercise acceleratorFromKeyDescriptor (the
  // original version of this test just re-asserted isValidAccelerator on the same five
  // hardcoded strings the tests above it already covered, and never called
  // acceleratorFromKeyDescriptor at all - it guarded nothing about this function). This
  // fuzzes every combination of the four modifier booleans against a representative sample
  // of codes (including modifier codes, ordinary keys, and an unsupported code) and proves
  // the builder can never emit a doubled, leading, or trailing "+" separator, whatever the
  // input - the exact hole "Control++9" / "+Control+9" / "Control+9+" exploit.
  const sampleCodes = [
    undefined,
    null,
    "ControlLeft",
    "ShiftLeft",
    "AltLeft",
    "MetaLeft",
    "Digit3",
    "Digit9",
    "KeyA",
    "Space",
    "ArrowUp",
    "F5",
    "Minus",
    "ContextMenu",
  ];
  const bools = [true, false];
  for (const ctrlKey of bools) {
    for (const metaKey of bools) {
      for (const altKey of bools) {
        for (const shiftKey of bools) {
          for (const code of sampleCodes) {
            const { accelerator } = acceleratorFromKeyDescriptor({ ctrlKey, metaKey, altKey, shiftKey, code });
            const label = JSON.stringify({ ctrlKey, metaKey, altKey, shiftKey, code });
            assert.ok(!accelerator.includes("++"), `no doubled separator for ${label}: got "${accelerator}"`);
            assert.ok(!accelerator.startsWith("+"), `no leading separator for ${label}: got "${accelerator}"`);
            assert.ok(!accelerator.endsWith("+"), `no trailing separator for ${label}: got "${accelerator}"`);
          }
        }
      }
    }
  }
  // And the same five raw strings from the previous pass's tightened validation must still
  // be rejected outright (not reopened by this fix).
  assert.equal(isValidAccelerator("Control++9"), false);
  assert.equal(isValidAccelerator("+Control+9"), false);
  assert.equal(isValidAccelerator("Control+9+"), false);
  assert.equal(isValidAccelerator("Shift+Shift+9"), false);
  assert.equal(isValidAccelerator("shift+Shift+9"), false);
});

test("unsupportedKeyCaptureMessage gives a titled... a dash-free, non-empty message", () => {
  const message = unsupportedKeyCaptureMessage();
  assert.ok(message.length > 0);
  assert.ok(!/[–—]/.test(message), "no em or en dashes in user-facing text");
});

test("hotkeyUnavailableSaveError centralizes the save-time 'already in use' copy (dash-free)", () => {
  const message = hotkeyUnavailableSaveError();
  assert.ok(message.length > 0);
  assert.ok(!/[–—]/.test(message), "no em or en dashes in user-facing text");
});
