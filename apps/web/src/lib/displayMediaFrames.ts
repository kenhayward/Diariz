/// The real frame source behind `slideCapture`: one `getDisplayMedia` stream, held open for as long as
/// auto-capture runs, sampled onto canvases.
///
/// Deliberately thin, and deliberately the only part of auto-capture that touches a browser API. There
/// is no meaningful way to unit-test `getDisplayMedia` and canvas encoding in jsdom, so everything with
/// a decision in it lives in `slideCapture.ts` and `slideDetector.ts` instead. Keep it that way: if this
/// file starts growing logic, that logic belongs next door.
///
/// Why one held-open stream rather than a grab per sample: a `desktopCapturer.getSources()` call costs
/// ~430ms whatever thumbnail size it is asked for, because it captures every screen each time. Sampling
/// a warm stream costs ~12ms, and holding one open measured at 0.1% of a core (spec §14).

import { sourceRectFor, type CaptureArea } from "./captureGeometry";
import { HASH_SIZE, dhash } from "./slideDetector";
import type { CommittedFrame, SlideCaptureFrames } from "./slideCapture";

/// Long edge of the stored image, matching what the desktop shell's own capture path produces.
const MAX_LONG_EDGE = 2560;
/// Long edge of the JPEG thumbnail rendered in the live strip and the transcript.
const THUMB_LONG_EDGE = 320;
const THUMB_QUALITY = 0.8;

const SAMPLE_W = HASH_SIZE + 1;
const SAMPLE_H = HASH_SIZE;

/// Frames per second requested from the compositor. We sample at 1Hz, so anything higher is paid for and
/// thrown away - a 30fps stream measured six times the CPU of a 1fps one. Asking for 2 leaves headroom
/// for a sample landing between frames without doubling the cost.
const STREAM_FPS = 2;

function fit(width: number, height: number, maxLongEdge: number) {
  const long = Math.max(width, height);
  if (long <= maxLongEdge) return { width, height };
  const ratio = maxLongEdge / long;
  return { width: Math.round(width * ratio), height: Math.round(height * ratio) };
}

/**
 * Open a screen-capture stream and return a frame source for `createSlideCapture`.
 *
 * The desktop shell has already granted the chosen display (its `setDisplayMediaRequestHandler` answers
 * without showing a picker), so this never prompts. `area` describes the capture rectangle in that
 * display's physical pixels; the stream's own size is only ever a hint, so the rectangle is rescaled per
 * frame rather than once up front.
 *
 * Throws if the stream cannot be opened - the caller surfaces that; there is nothing useful to do here.
 */
export async function openDisplayMediaFrames(area: CaptureArea): Promise<SlideCaptureFrames> {
  const stream = await navigator.mediaDevices.getDisplayMedia({
    video: {
      width: { ideal: area.displayWidth },
      height: { ideal: area.displayHeight },
      frameRate: { ideal: STREAM_FPS, max: STREAM_FPS },
    },
    audio: false,
  });

  const video = document.createElement("video");
  video.srcObject = stream;
  video.muted = true;
  // Keeps the element off any layout path; the stream still decodes.
  video.style.display = "none";
  await video.play();

  const sampleCanvas = document.createElement("canvas");
  sampleCanvas.width = SAMPLE_W;
  sampleCanvas.height = SAMPLE_H;
  const sampleCtx = sampleCanvas.getContext("2d", { willReadFrequently: true });

  const fullCanvas = document.createElement("canvas");
  const thumbCanvas = document.createElement("canvas");

  let closed = false;

  /// The area of the current frame to draw from. Recomputed per frame because a display mode change
  /// mid-meeting resizes the stream under us.
  const sourceRect = () => sourceRectFor({ width: video.videoWidth, height: video.videoHeight }, area);

  return {
    sample() {
      // videoWidth stays 0 until the first frame arrives. Not a failure - the stream is still coming up.
      if (closed || !sampleCtx || !video.videoWidth) return null;
      const src = sourceRect();
      sampleCtx.drawImage(video, src.x, src.y, src.width, src.height, 0, 0, SAMPLE_W, SAMPLE_H);
      return sampleCtx.getImageData(0, 0, SAMPLE_W, SAMPLE_H).data;
    },

    async commit(): Promise<CommittedFrame | null> {
      if (closed || !video.videoWidth) return null;
      const src = sourceRect();
      const size = fit(src.width, src.height, MAX_LONG_EDGE);

      fullCanvas.width = size.width;
      fullCanvas.height = size.height;
      const fullCtx = fullCanvas.getContext("2d");
      if (!fullCtx) return null;
      fullCtx.drawImage(video, src.x, src.y, src.width, src.height, 0, 0, size.width, size.height);

      const thumbSize = fit(size.width, size.height, THUMB_LONG_EDGE);
      thumbCanvas.width = thumbSize.width;
      thumbCanvas.height = thumbSize.height;
      const thumbCtx = thumbCanvas.getContext("2d");
      if (!thumbCtx) return null;
      thumbCtx.drawImage(fullCanvas, 0, 0, thumbSize.width, thumbSize.height);

      const [full, thumb] = await Promise.all([
        toBlob(fullCanvas, "image/png"),
        toBlob(thumbCanvas, "image/jpeg", THUMB_QUALITY),
      ]);
      if (!full || !thumb) return null;

      // Hashed from the SAME frame the images came from, through the same downsample path as `sample`,
      // so the detector's confirmation compares like with like. Hashing the full-resolution canvas
      // directly would go through a different chain and never match its own candidate.
      const hashCtx = sampleCtx;
      if (!hashCtx) return null;
      hashCtx.drawImage(video, src.x, src.y, src.width, src.height, 0, 0, SAMPLE_W, SAMPLE_H);
      const hash = dhash(hashCtx.getImageData(0, 0, SAMPLE_W, SAMPLE_H).data, SAMPLE_W, SAMPLE_H, {
        pixelOrder: "rgba",
      });

      return { full, thumb, width: size.width, height: size.height, hash };
    },

    close() {
      if (closed) return;
      closed = true;
      stream.getTracks().forEach((track) => track.stop());
      video.srcObject = null;
    },
  };
}

function toBlob(canvas: HTMLCanvasElement, type: string, quality?: number): Promise<Blob | null> {
  return new Promise((resolve) => canvas.toBlob(resolve, type, quality));
}
