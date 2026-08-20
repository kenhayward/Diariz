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
  if (source === "system") return "system audio";
  if (source === "both") return "microphone + system audio";
  return "microphone";
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
        { id: "record-both", label: "Record Both", enabled },
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

/// What to ask before quitting, or null to quit straight away.
///
/// Quitting is the one action that can lose a recording outright. The window's own close hides to the tray
/// rather than unloading, so nothing else tears the renderer down - and the renderer cannot guard this for
/// itself, because Electron cancels a close as soon as `beforeunload` is handled but shows no dialog, which
/// would make Quit appear to do nothing at all. So the ask lives here, in the main process.
///
/// `uploading` counts: the audio is written to local storage just before the request goes out, so most of
/// that phase survives - but not the moment before it, and interrupting it still costs the user a recovery
/// they never asked for.
function quitConfirmation(state) {
  const phase = state && state.phase;
  if (phase !== "recording" && phase !== "uploading") return null;
  const recording = phase === "recording";
  return {
    type: "warning",
    title: "Diariz",
    message: recording ? "A recording is still running." : "A recording is still uploading.",
    detail: recording
      ? "Quitting now ends it and the audio is lost. Stop the recording first to keep it."
      : "Quitting now interrupts the upload. It will be offered again next time you open Diariz.",
    buttons: ["Quit anyway", "Cancel"],
    // Enter and Escape both cancel: the destructive choice has to be taken deliberately.
    defaultId: 1,
    cancelId: 1,
  };
}

module.exports = { formatElapsed, trayRecorderItems, trayTooltip, notificationFor, quitConfirmation };
