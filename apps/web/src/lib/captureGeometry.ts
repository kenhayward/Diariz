/// Geometry for sampling a capture area out of a screen-share frame.
///
/// The shell chooses the capture area against a *display* and hands the crop over in that display's
/// physical pixels. The renderer then gets its frames from a `getDisplayMedia` stream whose size is only
/// ever a hint - Chromium routinely returns something other than what was asked for - so the crop has to
/// be rescaled into the frame's own coordinates before it can be used as a `drawImage` source rect.

export interface Rect {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface Size {
  width: number;
  height: number;
}

export interface CaptureArea {
  /// The target display's physical pixel size, which `crop` is expressed in.
  displayWidth: number;
  displayHeight: number;
  /// The chosen rectangle, or null for the whole screen.
  crop: Rect | null;
}

const whole = (frame: Size): Rect => ({ x: 0, y: 0, width: frame.width, height: frame.height });

/**
 * The `drawImage` source rectangle for one frame: the capture area, rescaled from the display's pixels
 * into the frame's.
 *
 * Falls back to the whole frame whenever the crop cannot be honoured - an unknown display size, or a
 * rectangle that lands outside the frame. Capturing the whole screen is wrong-but-visible; a degenerate
 * source rect draws nothing at all, which would look exactly like auto-capture having silently stopped.
 */
export function sourceRectFor(frame: Size, area: CaptureArea): Rect {
  if (!area.crop) return whole(frame);
  if (area.displayWidth <= 0 || area.displayHeight <= 0) return whole(frame);

  const scaleX = frame.width / area.displayWidth;
  const scaleY = frame.height / area.displayHeight;

  const x = Math.round(area.crop.x * scaleX);
  const y = Math.round(area.crop.y * scaleY);
  const width = Math.min(Math.round(area.crop.width * scaleX), frame.width - x);
  const height = Math.min(Math.round(area.crop.height * scaleY), frame.height - y);

  if (x < 0 || y < 0 || width <= 0 || height <= 0) return whole(frame);
  return { x, y, width, height };
}
