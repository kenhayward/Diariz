"use strict";

// Pure model for the pop-out notes window's geometry. main.js owns the BrowserWindow; the decisions
// live here so they can be unit-tested without Electron, matching recorderState.js / updateState.js.

const DEFAULT_SIZE = { width: 380, height: 520 };
const MIN_NOTES_SIZE = { width: 300, height: 360 };

function isFinitePoint(saved) {
  return Boolean(
    saved &&
      Number.isFinite(saved.x) &&
      Number.isFinite(saved.y) &&
      Number.isFinite(saved.width) &&
      Number.isFinite(saved.height),
  );
}

/// Whether the window's top-left corner still lands on an attached display. Checked against the corner
/// rather than the whole rectangle so a window hanging slightly off an edge is still restored - the
/// case that actually matters is the monitor being gone entirely, which would otherwise put the notes
/// window somewhere the user can neither see nor reach.
function onSomeDisplay(saved, displays) {
  return (displays || []).some((d) => {
    const b = d && d.bounds;
    if (!b) return false;
    return saved.x >= b.x && saved.x < b.x + b.width && saved.y >= b.y && saved.y < b.y + b.height;
  });
}

/// Bounds for the notes window: the remembered ones when they still make sense, otherwise a default
/// size with placement left to the OS (hence no x/y in that result).
function notesWindowBounds(saved, displays) {
  if (!isFinitePoint(saved) || !onSomeDisplay(saved, displays)) return { ...DEFAULT_SIZE };
  return {
    x: saved.x,
    y: saved.y,
    width: Math.max(saved.width, MIN_NOTES_SIZE.width),
    height: Math.max(saved.height, MIN_NOTES_SIZE.height),
  };
}

module.exports = { notesWindowBounds, DEFAULT_SIZE, MIN_NOTES_SIZE };
