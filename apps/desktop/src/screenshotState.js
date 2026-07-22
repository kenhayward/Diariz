"use strict";

// Pure model for the tray's screenshot controls and for hotkey validation.
// `main.js` owns the capture itself; the labels and the accelerator rules live
// here so they can be unit-tested without Electron.

const DEFAULT_ACCELERATOR = "CommandOrControl+Shift+9";

// The minimum time between two capture attempts that are allowed to both proceed. A
// held-down global hotkey auto-repeats on Windows at roughly 33ms, so this window
// absorbs any auto-repeat; the default accelerator is a deliberate three-key chord, so a
// genuine second capture is never sub-second and is never swallowed by this cooldown.
const CAPTURE_COOLDOWN_MS = 750;

const MODIFIERS = new Map(
  ["Command", "Cmd", "Control", "Ctrl", "CommandOrControl", "CmdOrCtrl", "Alt", "Option", "AltGr", "Shift", "Super", "Meta"]
    .map((m) => [m.toLowerCase(), m]),
);

/// Whether the current recorder state permits a screenshot capture at all: the tray
/// hotkey, the tray menu items, and the capture function itself all defer to this one
/// predicate so they can never disagree about the gate.
function canCapture(state) {
  return !!state && state.phase === "recording" && state.ready === true;
}

/// The dynamic screenshot menu items for the current phase, as plain descriptors
/// ({ id, label, enabled }). `main.js` maps each `id` to a click handler. Capture only
/// makes sense mid-recording with a ready renderer to receive it - not, for example,
/// during a mid-recording renderer reload, where a click would send into a void.
function trayScreenshotItems(state) {
  if (!canCapture(state)) return [];
  return [
    { id: "capture", label: "Capture Screenshot", enabled: true },
    { id: "change-area", label: "Change Capture Area...", enabled: true },
  ];
}

/// Whether a new capture attempt should proceed, given the current capture bookkeeping
/// (`{ inFlight, lastCaptureAt }`) and the current time. False while a capture is already
/// running (a held hotkey must never pile up concurrent grabs) or within the cooldown of
/// the last completed one (bounds a held hotkey's auto-repeat to a couple of captures a
/// second instead of ~30).
function shouldStartCapture(capture, now) {
  if (!capture || capture.inFlight) return false;
  const last = capture.lastCaptureAt || 0;
  return now - last >= CAPTURE_COOLDOWN_MS;
}

/// Native-notification copy for a failed screenshot capture, by reason. Pure so it's
/// unit-testable; main.js shows it via Electron's Notification, mirroring updateState's
/// notificationForUpdate / desktopAuth's notificationForAuthError.
/// reason: "unavailable" (grab returned null - the display went away or the crop
/// degenerated) | anything else (the capture attempt threw).
function notificationForCaptureFailure(reason) {
  const body =
    reason === "unavailable"
      ? "Screenshot capture failed - the display is no longer available"
      : "Screenshot capture failed";
  return { title: "Diariz", body };
}

// Raw split on "+", with no filtering - used to detect a stray double separator or a
// leading/trailing "+" (an empty segment), which `parts()` used to silently discard
// rather than reject. Now that a real IPC channel (the hotkey window) feeds this raw
// user input, "Control++9" must not quietly collapse into a valid-looking accelerator.
function rawSegments(input) {
  return String(input ?? "").split("+");
}

/// Native-notification copy for when the configured screenshot hotkey could not be held
/// (another application already owns that combination). Distinct from
/// notificationForCaptureFailure, which covers a failed capture attempt rather than a
/// failed registration; pulled out to a pure model for the same reason every other
/// user-facing notification in this shell is (notificationFor, notificationForUpdate,
/// notificationForAuthError) - so its copy gets unit coverage instead of living inline.
function notificationForHotkeyUnavailable() {
  return { title: "Diariz", body: "Screenshot hotkey unavailable - already in use by another app" };
}

function parts(input) {
  return rawSegments(input)
    .map((p) => p.trim())
    .filter((p) => p.length > 0);
}

/// A usable accelerator is at least one modifier plus exactly one non-modifier key, with
/// no empty segments (a stray/duplicate "+") and no modifier repeated. Anything else
/// would either fail to register, steal a bare keystroke globally, or silently reinterpret
/// malformed input as something the user didn't actually type.
function isValidAccelerator(input) {
  const raw = rawSegments(input);
  if (raw.some((p) => p.trim().length === 0)) return false;
  const segs = raw.map((p) => p.trim());
  if (segs.length < 2) return false;
  const mods = segs.filter((s) => MODIFIERS.has(s.toLowerCase()));
  const keys = segs.filter((s) => !MODIFIERS.has(s.toLowerCase()));
  if (mods.length < 1 || keys.length !== 1) return false;
  const modsLower = mods.map((m) => m.toLowerCase());
  return new Set(modsLower).size === modsLower.length;
}

/// Canonical form of an accelerator, or null when it isn't usable.
function normalizeAccelerator(input) {
  if (!isValidAccelerator(input)) return null;
  return parts(input)
    .map((s) => MODIFIERS.get(s.toLowerCase()) ?? (s.length === 1 ? s.toUpperCase() : s[0].toUpperCase() + s.slice(1)))
    .join("+");
}

// Fix pass 2 (shifted-key mapping): `KeyboardEvent.key` reports the SHIFTED OUTPUT
// CHARACTER for the number row and most punctuation, not the physical key - on a US
// layout Ctrl+Shift+3 fires `key:"#"` and Ctrl+Shift+9 (this app's own default hotkey
// shape) fires `key:"("`. The prior pass's lookup table was keyed on `key`, so those
// shifted characters sailed through as syntactically-valid-looking accelerators
// ("Control+Shift+#") and only failed later at globalShortcut.register with a misleading
// "already in use" message - reproducing the exact bug class this module exists to
// prevent. `KeyboardEvent.code` reports the PHYSICAL key ("Digit3", "KeyA", "Equal", ...)
// independent of modifiers and keyboard layout, so it - not `key` - is the correct source
// of truth for which key was actually pressed.

// DOM KeyboardEvent.code values that don't already equal a valid Electron accelerator
// token verbatim, or that need translating to one (see electron/shell/common/
// keyboard_util.cc's KeyboardCodeFromKeyIdentifier table, the ground truth for what
// globalShortcut.register actually accepts, and the Accelerator docs' key-code list).
// Digits, letters, and function keys are handled by pattern below rather than listed
// here one-by-one (see DIGIT_CODE/LETTER_CODE/FUNCTION_KEY_CODE).
//   - Arrow keys: DOM's "ArrowUp"/"ArrowDown"/"ArrowLeft"/"ArrowRight" vs. Electron's
//     "Up"/"Down"/"Left"/"Right".
//   - Volume keys: DOM's "AudioVolumeUp"/"AudioVolumeDown"/"AudioVolumeMute" vs.
//     Electron's "VolumeUp"/"VolumeDown"/"VolumeMute".
//   - Media track keys: DOM's "MediaTrackNext"/"MediaTrackPrevious" vs. Electron's
//     "MediaNextTrack"/"MediaPreviousTrack" (word order swapped; MediaStop and
//     MediaPlayPause already agree, so they aren't listed).
//   - Space/Tab/Backspace/Delete/Insert/Home/End/PageUp/PageDown/Escape/Enter and the
//     always-fixed media keys: `code` already equals Electron's token, listed explicitly
//     (rather than left to an implicit fallback) so "not in this map and not digit/letter/
//     F-key" can safely mean "Electron has no name for this physical key" - see the
//     capture-time-rejection requirement below.
//   - CapsLock/NumLock/ScrollLock: DOM's `code` casing ("CapsLock") differs from
//     Electron's documented token spelling ("Capslock").
//   - Punctuation: Electron's accelerator vocabulary takes the bare unshifted character
//     for these keys, so each punctuation `code` maps to that character - the same
//     character `key` would report with no modifiers held. This is the actual fix for the
//     Ctrl+Shift+3-style bug: deriving from `code` means the mapped character never
//     changes with Shift.
//   - Numpad digits/operators: Electron's dedicated num0-num9 / numadd / numsub /
//     nummult / numdiv / numdec tokens.
// Deliberately NOT mapped (return null - see acceleratorKeyFromDomCode): "ContextMenu"
// (the Menu key), "IntlBackslash"/"IntlRo"/"IntlYen" (extra ISO/JIS keys), dead keys, IME
// composition keys, "Unidentified" - none of these have an Electron accelerator name, and
// inventing one would just move the failure from "rejected at capture" to "silently wrong".
const DOM_CODE_TO_ACCELERATOR_KEY = new Map([
  ["ArrowUp", "Up"],
  ["ArrowDown", "Down"],
  ["ArrowLeft", "Left"],
  ["ArrowRight", "Right"],
  ["AudioVolumeUp", "VolumeUp"],
  ["AudioVolumeDown", "VolumeDown"],
  ["AudioVolumeMute", "VolumeMute"],
  ["MediaTrackNext", "MediaNextTrack"],
  ["MediaTrackPrevious", "MediaPreviousTrack"],
  ["MediaPlayPause", "MediaPlayPause"],
  ["MediaStop", "MediaStop"],
  ["Space", "Space"],
  ["Tab", "Tab"],
  ["Backspace", "Backspace"],
  ["Delete", "Delete"],
  ["Insert", "Insert"],
  ["Home", "Home"],
  ["End", "End"],
  ["PageUp", "PageUp"],
  ["PageDown", "PageDown"],
  ["Escape", "Escape"],
  ["Enter", "Enter"],
  ["PrintScreen", "PrintScreen"],
  ["CapsLock", "Capslock"],
  ["NumLock", "Numlock"],
  ["ScrollLock", "Scrolllock"],
  ["Minus", "-"],
  ["Equal", "="],
  ["BracketLeft", "["],
  ["BracketRight", "]"],
  ["Semicolon", ";"],
  ["Quote", "'"],
  ["Comma", ","],
  ["Period", "."],
  ["Slash", "/"],
  ["Backslash", "\\"],
  ["Backquote", "`"],
  ["NumpadAdd", "numadd"],
  ["NumpadSubtract", "numsub"],
  ["NumpadMultiply", "nummult"],
  ["NumpadDivide", "numdiv"],
  ["NumpadDecimal", "numdec"],
]);

// Digit/letter/function-key codes are derived by pattern rather than a 60-entry table:
// `code` for these is a fixed, layout-independent name ("Digit3", "KeyA", "F12") and
// Electron's token is just the bare character (or, for function keys, the same name).
const DIGIT_CODE = /^Digit([0-9])$/;
const LETTER_CODE = /^Key([A-Z])$/;
const FUNCTION_KEY_CODE = /^F([1-9]|1[0-9]|2[0-4])$/; // Electron documents F1 through F24
const NUMPAD_DIGIT_CODE = /^Numpad([0-9])$/;

/// Translate one captured DOM `KeyboardEvent.code` (the physical key, not the possibly
/// shift-produced `key` character) into the token Electron's accelerator parser expects,
/// or `null` when Electron's accelerator vocabulary has no name for that physical key at
/// all. Returning `null` (rather than guessing) is what lets the caller reject an
/// unsupported key at capture time with an accurate message instead of building a
/// plausible-looking string that only fails later at `globalShortcut.register`.
function acceleratorKeyFromDomCode(domCode) {
  if (typeof domCode !== "string" || domCode.length === 0) return null;
  const mapped = DOM_CODE_TO_ACCELERATOR_KEY.get(domCode);
  if (mapped) return mapped;
  const digit = DIGIT_CODE.exec(domCode);
  if (digit) return digit[1];
  const letter = LETTER_CODE.exec(domCode);
  if (letter) return letter[1];
  if (FUNCTION_KEY_CODE.test(domCode)) return domCode;
  const numpadDigit = NUMPAD_DIGIT_CODE.exec(domCode);
  if (numpadDigit) return `num${numpadDigit[1]}`;
  return null;
}

// DOM `code` values for the modifier keys themselves - reported when only a modifier is
// held down (each side distinctly, unlike `key`, which reports "Control"/"Shift"/"Alt"/
// "Meta" for either side). Treated as "no key pressed yet" so a still-held modifier can't
// leak into the key slot of the accelerator being built.
const MODIFIER_DOM_CODES = new Set([
  "ControlLeft",
  "ControlRight",
  "ShiftLeft",
  "ShiftRight",
  "AltLeft",
  "AltRight",
  "MetaLeft",
  "MetaRight",
]);

/// Build a not-yet-validated accelerator descriptor from the raw data a keydown handler
/// observes: the four `KeyboardEvent` modifier booleans plus `code` (the physical key -
/// see the fix-pass-2 note above for why `code`, not `key`, is the source of truth here).
/// This is the one path raw renderer input takes on its way to
/// `isValidAccelerator`/`normalizeAccelerator` - kept here (rather than inlined in the
/// sandboxed hotkey window, which cannot require this module) so the DOM-code translation
/// gets the same unit coverage as the rest of the accelerator rules.
///
/// Returns `{ accelerator, unsupported }`:
///   - `accelerator` is the "+"-joined string built so far (possibly just modifiers, or
///     empty) - always well-formed as a set of "+"-joined segments (never a stray or
///     duplicate separator), so any invalidity `isValidAccelerator` reports afterwards is a
///     genuine "not enough" rather than a parsing artifact.
///   - `unsupported` is true only when a real (non-modifier) key was pressed and
///     `acceleratorKeyFromDomCode` has no Electron name for it - callers must surface this
///     as a distinct, accurate message ("that key can't be used") rather than folding it
///     into the generic "needs a modifier and a key" copy, which would be wrong here (the
///     user did press both).
/// Pass an empty object (or omit `code`) to describe "nothing pressed yet".
function acceleratorFromKeyDescriptor({ ctrlKey, metaKey, altKey, shiftKey, code } = {}) {
  const mods = [];
  if (ctrlKey) mods.push("Control");
  if (metaKey) mods.push("Command");
  if (altKey) mods.push("Alt");
  if (shiftKey) mods.push("Shift");
  if (code == null || MODIFIER_DOM_CODES.has(code)) {
    return { accelerator: mods.join("+"), unsupported: false };
  }
  const key = acceleratorKeyFromDomCode(code);
  if (key == null) {
    return { accelerator: mods.join("+"), unsupported: true };
  }
  return { accelerator: [...mods, key].join("+"), unsupported: false };
}

/// User-facing copy for a physical key that Electron's accelerator parser has no name for
/// (e.g. the context-menu key) - shown in the hotkey capture window the instant such a key
/// is pressed, rather than only surfacing once as a raw registration failure. Pure/tested
/// like every other message in this module (notificationForCaptureFailure,
/// notificationForHotkeyUnavailable, ...).
function unsupportedKeyCaptureMessage() {
  return "That key cannot be used in a hotkey.";
}

/// Centralized copy for a save attempt that failed because the combination is already held
/// by other software - used by both `hotkey:save` branches in main.js (recording and idle)
/// so the message can't drift between them, the same way `notificationForHotkeyUnavailable`
/// centralizes the tray-notification copy for the same underlying situation.
function hotkeyUnavailableSaveError() {
  return "That combination is already in use by another application.";
}

module.exports = {
  DEFAULT_ACCELERATOR,
  CAPTURE_COOLDOWN_MS,
  canCapture,
  trayScreenshotItems,
  isValidAccelerator,
  normalizeAccelerator,
  shouldStartCapture,
  notificationForCaptureFailure,
  notificationForHotkeyUnavailable,
  acceleratorKeyFromDomCode,
  acceleratorFromKeyDescriptor,
  unsupportedKeyCaptureMessage,
  hotkeyUnavailableSaveError,
};
