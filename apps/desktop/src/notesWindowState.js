"use strict";

// Pure model for the pop-out notes window's geometry. main.js owns the BrowserWindow; the decisions
// live here so they can be unit-tested without Electron, matching recorderState.js / updateState.js.

// The size the handoff asks for. It is the DEFAULT, not the minimum: 740px does not fit a 768-tall
// laptop display once the taskbar has taken its share, and a window that cannot be made to fit is worse
// than a small one.
const DEFAULT_SIZE = { width: 420, height: 740 };
const MIN_NOTES_SIZE = { width: 360, height: 480 };

// Compact mode: only the status line and the composer, for when a call has the rest of the screen. The
// stream is hidden by the RENDERER - the shell only resizes the window, because what "just the composer
// band" measures is a layout question and the renderer is the only side that can answer it.
const COMPACT_HEIGHT = 132;

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

/// The window shrunk to its composer band: same place, same width, only the height goes.
///
/// Position and width are kept because compact is used mid-call, when the window has been put somewhere
/// deliberately out of the way - moving or narrowing it at the same moment would lose that placement.
function compactBounds(current) {
  return { x: current.x, y: current.y, width: current.width, height: COMPACT_HEIGHT };
}

/// The window put back to its full height after compact.
///
/// Takes the CURRENT bounds plus the height to restore, rather than a whole saved rectangle, because the
/// window can be dragged while compact - restoring a remembered rectangle would teleport it away from
/// wherever the user has just put it. `previousHeight` falls back to the default when there is nothing
/// to restore, and is raised to the minimum, so this can never hand back an unusable window.
function restoredBounds(current, previousHeight) {
  const height = Number.isFinite(previousHeight) ? previousHeight : DEFAULT_SIZE.height;
  return {
    x: current.x,
    y: current.y,
    width: current.width,
    height: Math.max(height, MIN_NOTES_SIZE.height),
  };
}

module.exports = {
  notesWindowBounds,
  compactBounds,
  restoredBounds,
  COMPACT_HEIGHT,
  DEFAULT_SIZE,
  MIN_NOTES_SIZE,
};
