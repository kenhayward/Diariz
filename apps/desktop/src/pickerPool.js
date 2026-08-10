"use strict";

// Pure bookkeeping for the pre-warmed capture-area overlay windows. `main.js` owns the
// BrowserWindows; which displays the pool should hold a window for lives here so it can
// be unit-tested without Electron, like captureTarget/screenshotState alongside it.
//
// Why a pool at all: building the overlays on demand measured at 400-750ms on a
// three-display machine (each overlay is its own sandboxed renderer and must paint
// before `ready-to-show` lets us show it). For that half-second nothing is on screen and
// the app window still takes input, so "Change capture area" reads as a dead button -
// and an impatient second click either hits the re-surface no-op or lands on the overlay
// the instant it appears, where a sub-threshold press-release means "whole screen". So
// the overlays are built once, up front, while a recording is running, and every pick
// just shows them.

/// What the pool has to do to hold exactly one window per current display: `create` for
/// displays with no window yet, `destroy` for windows whose display has gone away, and
/// `keep` for the ones already in the right place. Duplicate ids on either side collapse,
/// so a caller that hands over a raw list can't produce two windows for one display.
function reconcilePool(pooledIds, displayIds) {
  const pooled = new Set(pooledIds);
  const displays = new Set(displayIds);
  return {
    create: [...displays].filter((id) => !pooled.has(id)),
    destroy: [...pooled].filter((id) => !displays.has(id)),
    keep: [...displays].filter((id) => pooled.has(id)),
  };
}

module.exports = { reconcilePool };
