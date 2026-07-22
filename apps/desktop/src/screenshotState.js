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
};
