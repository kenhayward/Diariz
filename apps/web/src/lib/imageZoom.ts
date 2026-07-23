/// Pure zoom/pan maths for the screenshot viewer. `scale` throughout is relative to fit-to-window
/// (1 = fit, matching the pre-zoom behaviour); the component owns the `{ scale, offset }` React state
/// and calls these to compute the next value on a button click, wheel tick, drag, or navigation reset.
/// Kept side-effect-free (no DOM reads) so the fiddly bits - zoom-to-cursor and pan clamping - are
/// unit-testable without jsdom's lack of real layout.

export interface Point {
  x: number;
  y: number;
}

export interface Size {
  width: number;
  height: number;
}

/** scale=1 always means "fit to window" - the floor of the zoom range. */
export const MIN_SCALE = 1;

/** Default max-zoom cap expressed as a multiple of the fit scale, used when the capture's true native
 * scale doesn't already exceed it (see `computeMaxScale`). */
const DEFAULT_MAX_ZOOM_MULTIPLE = 5;

/** Default per-step multiplier for the +/- buttons and the `+`/`-` keyboard shortcuts. */
const DEFAULT_STEP_FACTOR = 1.25;

/** Absolute scale (CSS px per image px) that fits the image inside the viewport without upscaling past
 * its natural size - this mirrors the pre-zoom "fit to window" look (object-contain via max-w/max-h with
 * auto width/height, so a capture smaller than the viewport is shown at its own size, never stretched
 * larger). Falls back to 1 when any dimension is non-positive - e.g. jsdom, or a frame before the image
 * or viewport has been laid out - rather than dividing by zero or returning NaN. */
export function computeFitScale(
  imageWidth: number,
  imageHeight: number,
  viewportWidth: number,
  viewportHeight: number,
): number {
  if (imageWidth <= 0 || imageHeight <= 0 || viewportWidth <= 0 || viewportHeight <= 0) return 1;
  return Math.min(1, viewportWidth / imageWidth, viewportHeight / imageHeight);
}

/** The zoom-state `scale` (relative to fit = 1) that shows the image at true native size - one image
 * pixel per CSS pixel. Equal to 1 when the capture is already smaller than the viewport, since fit and
 * native are then the same size. */
export function computeNativeScale(
  imageWidth: number,
  imageHeight: number,
  viewportWidth: number,
  viewportHeight: number,
): number {
  const fit = computeFitScale(imageWidth, imageHeight, viewportWidth, viewportHeight);
  return fit > 0 ? 1 / fit : 1;
}

/** Zoom cap: whichever is larger of true 100% native size or `capMultiple`x the fit size (default 5x).
 * A flat "always Nx fit" cap would under-zoom a dense capture whose native size is well beyond that
 * multiple - a 4K screenshot shrunk hard to fit a small window can need well over 5x fit to read at
 * native resolution. Conversely a flat "native only" cap barely zooms a capture that's already smaller
 * than the viewport, since native == fit == 1x there and 1x is a useless "max zoom". Taking the max of
 * both means: a capture can always be zoomed to its true resolution, and a small capture still gets a
 * useful default 5x zoom to inspect fine detail. */
export function computeMaxScale(
  imageWidth: number,
  imageHeight: number,
  viewportWidth: number,
  viewportHeight: number,
  capMultiple = DEFAULT_MAX_ZOOM_MULTIPLE,
): number {
  return Math.max(capMultiple, computeNativeScale(imageWidth, imageHeight, viewportWidth, viewportHeight));
}

/** Displayed image size (CSS px) at a given relative `scale` (1 = fit), given the absolute `fitScale`
 * from `computeFitScale`. */
export function computeDisplaySize(imageWidth: number, imageHeight: number, fitScale: number, scale: number): Size {
  return { width: imageWidth * fitScale * scale, height: imageHeight * fitScale * scale };
}

/** Clamp a scale into [min, max]. */
export function clampScale(scale: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, scale));
}

/** The next scale for a single +/- (button or keyboard) step: multiply/divide by `factor` and clamp
 * into [min, max]. A fixed multiplicative step (rather than an additive one) keeps each press feeling
 * proportionally the same size at any zoom level. */
export function stepScale(scale: number, direction: 1 | -1, min: number, max: number, factor = DEFAULT_STEP_FACTOR): number {
  const next = direction > 0 ? scale * factor : scale / factor;
  return clampScale(next, min, max);
}

/** The new pan offset when zooming from `scaleOld` to `scaleNew`, anchored at `cursor` - both `offset`
 * and `cursor` are in the same viewport-center-relative pixel space (e.g. `clientX/Y` minus the
 * viewport's center). Keeps the image pixel under the cursor fixed on screen, which is what makes wheel
 * zoom feel like it zooms "into" the pointer rather than the image center.
 *
 * Derivation: with the image centred by default and offset/scale applied as
 * `translate(offset) scale(scale)`, the content point currently under the cursor is
 * `(cursor - offset) / scaleOld`. Solving for the new offset that keeps that same content point under
 * the same cursor position at `scaleNew` gives
 * `offsetNew = cursor - (cursor - offsetOld) * (scaleNew / scaleOld)`.
 * Passing `cursor = { x: 0, y: 0 }` (the viewport's own center) reduces this to
 * `offsetNew = offsetOld * (scaleNew / scaleOld)` - i.e. scaling an existing pan proportionally, which
 * is the right behaviour for the +/- buttons and keyboard shortcuts (no cursor position to anchor on). */
export function zoomTowardPoint(offset: Point, scaleOld: number, scaleNew: number, cursor: Point): Point {
  const ratio = scaleNew / scaleOld;
  return {
    x: cursor.x - (cursor.x - offset.x) * ratio,
    y: cursor.y - (cursor.y - offset.y) * ratio,
  };
}

function clampAxis(offset: number, displayed: number, viewport: number): number {
  if (displayed <= viewport) return 0;
  const max = (displayed - viewport) / 2;
  return Math.min(max, Math.max(-max, offset));
}

/** Clamp a pan offset so the image can never be dragged fully off the viewport: each axis is confined to
 * `[-(displayed-viewport)/2, (displayed-viewport)/2]` once the image overflows that axis (the amount the
 * image can slide before its far edge would clear the viewport edge and leave a gap), or forced to 0
 * while the image still fits that axis (nothing to pan - keep it centred instead of drifting). */
export function clampPanOffset(
  offset: Point,
  displayedWidth: number,
  displayedHeight: number,
  viewportWidth: number,
  viewportHeight: number,
): Point {
  return {
    x: clampAxis(offset.x, displayedWidth, viewportWidth),
    y: clampAxis(offset.y, displayedHeight, viewportHeight),
  };
}

/** The zoom state a capture opens with, and the state prev/next resets to: fit scale, no pan. */
export function initialZoomState(): { scale: number; offset: Point } {
  return { scale: MIN_SCALE, offset: { x: 0, y: 0 } };
}
