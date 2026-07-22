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

// DOM KeyboardEvent.key values that don't match the token Electron's accelerator parser
// expects (see electron/shell/common/keyboard_util.cc's KeyboardCodeFromKeyIdentifier
// table, the ground truth for what globalShortcut.register actually accepts). Only real
// mismatches are listed here - everything else (letters, digits, punctuation, F-keys,
// Home/End/PageUp/PageDown/Escape/Enter/Tab/Backspace/Delete/Insert/...) already reaches
// Electron's parser as the same characters (it lowercases before matching), so no
// translation is needed for those.
//   - " " (Space): the spacebar's `key` is the literal space character, not a name.
//   - Arrow keys: DOM reports "ArrowUp"/"ArrowDown"/"ArrowLeft"/"ArrowRight"; Electron's
//     table only has "Up"/"Down"/"Left"/"Right".
//   - Volume keys: DOM's "AudioVolumeUp"/"AudioVolumeDown"/"AudioVolumeMute" vs.
//     Electron's "VolumeUp"/"VolumeDown"/"VolumeMute".
//   - Media track keys: DOM's "MediaTrackNext"/"MediaTrackPrevious" vs. Electron's
//     "MediaNextTrack"/"MediaPreviousTrack" (word order swapped; MediaStop and
//     MediaPlayPause already agree, so they aren't listed).
//   - "+" (Plus): a real key (Shift+= on most layouts) whose `key` is the literal "+"
//     character - Electron accepts it as-is too, but joining it with "+" (this module's
//     segment separator) would produce a string indistinguishable from a stray/duplicate
//     separator, so it's translated to Electron's own "Plus" alias instead.
// Deliberately not attempting to cover every DOM key name (e.g. dead keys, IME
// composition keys, "Unidentified") - those aren't accelerator-capable keys at all, and
// inventing a mapping Electron doesn't actually support would just move the failure from
// "rejected at capture" to "silently wrong".
const DOM_KEY_TO_ACCELERATOR_KEY = new Map([
  [" ", "Space"],
  ["ArrowUp", "Up"],
  ["ArrowDown", "Down"],
  ["ArrowLeft", "Left"],
  ["ArrowRight", "Right"],
  ["AudioVolumeUp", "VolumeUp"],
  ["AudioVolumeDown", "VolumeDown"],
  ["AudioVolumeMute", "VolumeMute"],
  ["MediaTrackNext", "MediaNextTrack"],
  ["MediaTrackPrevious", "MediaPreviousTrack"],
  ["+", "Plus"],
]);

/// Translate one captured DOM `KeyboardEvent.key` into the token Electron's accelerator
/// parser expects. Keys not in the mismatch table above already agree with Electron's
/// vocabulary, so they fall back to the same single-char-uppercase / capitalize rule
/// `normalizeAccelerator` uses for its segments.
function acceleratorKeyFromDomKey(domKey) {
  const mapped = DOM_KEY_TO_ACCELERATOR_KEY.get(domKey);
  if (mapped) return mapped;
  return domKey.length === 1 ? domKey.toUpperCase() : domKey;
}

// DOM names for the modifier keys themselves - reported as `key` when only a modifier is
// held down. Treated as "no key pressed yet" so a still-held modifier can't leak into the
// key slot of the accelerator being built.
const MODIFIER_DOM_KEYS = new Set(["Control", "Shift", "Alt", "Meta"]);

/// Build a not-yet-validated accelerator string from the raw descriptor a keydown handler
/// observes: the four `KeyboardEvent` modifier booleans plus `key`. This is the one path
/// raw renderer input takes on its way to `isValidAccelerator`/`normalizeAccelerator` -
/// kept here (rather than inlined in the sandboxed hotkey window, which cannot require
/// this module) so the DOM-key translation gets the same unit coverage as the rest of the
/// accelerator rules. Pass an empty object (or omit `key`) to describe "nothing pressed
/// yet"; the result is always well-formed as a set of "+"-joined segments (never a stray
/// or duplicate separator), so any invalidity `isValidAccelerator` reports afterwards is a
/// genuine "not enough" (no key yet, or no modifier) rather than a parsing artifact.
function acceleratorFromKeyDescriptor({ ctrlKey, metaKey, altKey, shiftKey, key } = {}) {
  const mods = [];
  if (ctrlKey) mods.push("Control");
  if (metaKey) mods.push("Command");
  if (altKey) mods.push("Alt");
  if (shiftKey) mods.push("Shift");
  if (key == null || MODIFIER_DOM_KEYS.has(key)) return mods.join("+");
  return [...mods, acceleratorKeyFromDomKey(key)].join("+");
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
  acceleratorKeyFromDomKey,
  acceleratorFromKeyDescriptor,
};
