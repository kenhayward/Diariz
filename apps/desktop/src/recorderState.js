"use strict";

// Pure model for the tray's recording controls. `main.js` owns the actual
// recorder state machine and IPC, but the *labels, tooltip, and notifications*
// are derived here so they can be unit-tested without Electron.
//
// State shape: { phase: "idle" | "recording" | "uploading" | "error",
//                source?: "mic" | "system", ready?: boolean, error?: string }
// `ready` means the web app is loaded and signed in (a recorder exists to drive).

function formatElapsed(ms) {
  const secs = Math.max(0, Math.floor((ms || 0) / 1000));
  const mm = String(Math.floor(secs / 60)).padStart(2, "0");
  const ss = String(secs % 60).padStart(2, "0");
  return `${mm}:${ss}`;
}

function sourceLabel(source) {
  return source === "system" ? "system audio" : "microphone";
}

/// The dynamic record/stop menu items for the current phase, as plain descriptors
/// ({ id, label, enabled }). `main.js` maps each `id` to a click handler.
function trayRecorderItems(state, elapsedMs) {
  switch (state.phase) {
    case "recording":
      return [{ id: "stop", label: `Stop Recording (${formatElapsed(elapsedMs)})`, enabled: true }];
    case "uploading":
      return [{ id: "uploading", label: "Uploading…", enabled: false }];
    default: {
      const enabled = state.ready === true;
      return [
        { id: "record-mic", label: "Record Microphone", enabled },
        { id: "record-system", label: "Record System Audio", enabled },
      ];
    }
  }
}

function trayTooltip(state) {
  switch (state.phase) {
    case "recording":
      return `Diariz — recording ${sourceLabel(state.source)}`;
    case "uploading":
      return "Diariz — uploading…";
    default:
      return "Diariz";
  }
}

/// What native notification (if any) a phase transition should raise.
/// Returns { title, body } or null.
function notificationFor(prev, next) {
  if (next.phase === "recording" && prev.phase !== "recording") {
    return { title: "Diariz", body: `Recording ${sourceLabel(next.source)}…` };
  }
  if (next.phase === "idle" && prev.phase === "uploading") {
    return { title: "Diariz", body: "Recording uploaded" };
  }
  if (next.phase === "error" && prev.phase !== "error") {
    return { title: "Diariz", body: next.error || "Recording failed" };
  }
  return null;
}

module.exports = { formatElapsed, trayRecorderItems, trayTooltip, notificationFor };
