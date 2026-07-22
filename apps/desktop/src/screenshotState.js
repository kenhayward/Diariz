"use strict";

// Pure model for the tray's screenshot controls and for hotkey validation.
// `main.js` owns the capture itself; the labels and the accelerator rules live
// here so they can be unit-tested without Electron.

const DEFAULT_ACCELERATOR = "CommandOrControl+Shift+9";

const MODIFIERS = new Map(
  ["Command", "Cmd", "Control", "Ctrl", "CommandOrControl", "CmdOrCtrl", "Alt", "Option", "AltGr", "Shift", "Super", "Meta"]
    .map((m) => [m.toLowerCase(), m]),
);

/// The dynamic screenshot menu items for the current phase, as plain descriptors
/// ({ id, label, enabled }). `main.js` maps each `id` to a click handler. Capture only
/// makes sense mid-recording, so no items are offered in any other phase.
function trayScreenshotItems(state) {
  if (!state || state.phase !== "recording") return [];
  return [
    { id: "capture", label: "Capture Screenshot", enabled: true },
    { id: "change-area", label: "Change Capture Area...", enabled: true },
  ];
}

function parts(input) {
  return String(input ?? "")
    .split("+")
    .map((p) => p.trim())
    .filter((p) => p.length > 0);
}

/// A usable accelerator is at least one modifier plus exactly one non-modifier key.
/// Anything else would either fail to register or steal a bare keystroke globally.
function isValidAccelerator(input) {
  const segs = parts(input);
  if (segs.length < 2) return false;
  const mods = segs.filter((s) => MODIFIERS.has(s.toLowerCase()));
  const keys = segs.filter((s) => !MODIFIERS.has(s.toLowerCase()));
  return mods.length >= 1 && keys.length === 1;
}

/// Canonical form of an accelerator, or null when it isn't usable.
function normalizeAccelerator(input) {
  if (!isValidAccelerator(input)) return null;
  return parts(input)
    .map((s) => MODIFIERS.get(s.toLowerCase()) ?? (s.length === 1 ? s.toUpperCase() : s[0].toUpperCase() + s.slice(1)))
    .join("+");
}

module.exports = { DEFAULT_ACCELERATOR, trayScreenshotItems, isValidAccelerator, normalizeAccelerator };
