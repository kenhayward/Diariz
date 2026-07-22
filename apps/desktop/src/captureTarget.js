"use strict";

// Pure geometry for screen capture. `main.js` owns desktopCapturer and nativeImage;
// the maths lives here so it can be unit-tested without Electron.
//
// Coordinate systems: a picker selection is in display-relative DIP (what the overlay
// window sees); a crop rect is in physical pixels (what a full-resolution grab uses).

/// Clamp a rect to `{width,height}` bounds, pulling the origin in and shrinking the
/// size rather than letting either escape the display.
function clampRect(rect, bounds) {
  const x = Math.max(0, Math.min(Math.round(rect.x), bounds.width));
  const y = Math.max(0, Math.min(Math.round(rect.y), bounds.height));
  const right = Math.min(bounds.width, Math.round(rect.x) + Math.round(rect.width));
  const bottom = Math.min(bounds.height, Math.round(rect.y) + Math.round(rect.height));
  return { x, y, width: Math.max(0, right - x), height: Math.max(0, bottom - y) };
}

/// The physical-pixel crop for a selection, or null when the whole screen is wanted
/// (the grab is already exactly that image, so cropping would be wasted work).
function cropRectFor(display, selection) {
  if (!selection || selection.kind === "screen") return null;
  const scale = display.scaleFactor || 1;
  const clamped = clampRect(selection.rect, display.bounds);
  return {
    x: Math.round(clamped.x * scale),
    y: Math.round(clamped.y * scale),
    width: Math.round(clamped.width * scale),
    height: Math.round(clamped.height * scale),
  };
}

/// Dimensions scaled so the long edge is at most `maxLongEdge`. Never upscales.
function resizeDims(width, height, maxLongEdge) {
  const long = Math.max(width, height);
  if (long <= maxLongEdge) return { width, height };
  const ratio = maxLongEdge / long;
  return { width: Math.round(width * ratio), height: Math.round(height * ratio) };
}

module.exports = { clampRect, cropRectFor, resizeDims };
