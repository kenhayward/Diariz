import { useCallback, useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { api } from "../lib/api";
import { formatDuration } from "../lib/format";
import type { Screenshot } from "../lib/types";
import {
  MIN_SCALE,
  clampPanOffset,
  clampScale,
  computeDisplaySize,
  computeFitScale,
  computeMaxScale,
  computeNativeScale,
  initialZoomState,
  stepScale,
  zoomTowardPoint,
  type Point,
} from "../lib/imageZoom";

/// Full-size viewer for one capture, with prev/next through the recording's captures, a position counter,
/// a jump to the moment it was taken, download, delete, a full-screen (expand-to-fill) toggle, and zoom/pan.
/// Index is owned by the caller so the transcript row, the Notes section and the strip all agree on which
/// capture is open. Mirrors EditActionModal's shell (backdrop click + stop-propagation, Escape via a
/// document keydown listener cleaned up on unmount).
///
/// The image is fit-to-window (object-contain) by default; the full-screen toggle maximises that fit, and
/// zoom/pan (works in both windowed and expanded states) lets a dense capture be read at native size and
/// scrolled around - a zoom cluster in the toolbar (minus / percentage-as-reset / plus), the mouse wheel
/// zooming toward the pointer, double-click toggling fit <-> 100%, the `+`/`-`/`0` keyboard shortcuts, and
/// drag-to-pan once zoomed past fit. The fiddly maths (clamping, zoom-to-cursor, pan bounds) lives in the
/// pure `lib/imageZoom.ts`, unit-tested independently of the DOM; this component just wires DOM events to
/// it and holds the `{ scale, offset }` state, which resets to fit whenever `index` changes or the modal
/// is reopened.
export default function ScreenshotModal({
  recordingId,
  shots,
  index,
  onIndexChange,
  onClose,
  onJump,
  onDelete,
}: {
  recordingId: string;
  shots: Screenshot[];
  index: number;
  onIndexChange: (index: number) => void;
  onClose: () => void;
  onJump?: (ms: number) => void;
  onDelete?: (id: string) => void;
}) {
  const { t } = useTranslation("workspace");
  // Windowed by default; the toggle expands the dialog to fill the viewport so a full-screen capture is
  // readable without downloading it. Local state - resets to windowed each time the modal is reopened.
  const [expanded, setExpanded] = useState(false);
  // scale is relative to fit-to-window (1 = fit); offset is a CSS-pixel pan translate. Resets to fit
  // (initialZoomState) each time the modal is reopened, and via the effect below whenever `index` changes.
  const [zoom, setZoom] = useState(() => initialZoomState());
  const [dragging, setDragging] = useState(false);
  // Bumped when the image finishes loading, so the zoom-percentage badge recomputes from the real
  // naturalWidth/Height instead of the pre-load fallback (which reads as 100%).
  const [, remeasure] = useState(0);
  const viewportRef = useRef<HTMLDivElement | null>(null);
  const imgRef = useRef<HTMLImageElement | null>(null);
  const dragRef = useRef<{ startX: number; startY: number; startOffset: Point } | null>(null);
  // Guard against an out-of-range index (e.g. the last capture was deleted while open) rather than
  // let `shots[index]` come back undefined and blow up the image/alt-text below.
  const shot = index >= 0 && index < shots.length ? shots[index] : undefined;

  // A very high resolution capture may still read small even at 100% zoom if the natural image size or
  // viewport hasn't been measured yet (e.g. the image is still loading) - `computeFitScale`/`computeMaxScale`
  // fall back to sane defaults (1x / 5x) rather than dividing by zero in that case.
  function measure() {
    const rect = viewportRef.current?.getBoundingClientRect();
    return {
      vpW: rect?.width ?? 0,
      vpH: rect?.height ?? 0,
      vpLeft: rect?.left ?? 0,
      vpTop: rect?.top ?? 0,
      imgW: imgRef.current?.naturalWidth ?? 0,
      imgH: imgRef.current?.naturalHeight ?? 0,
    };
  }

  // Every zoom-state update goes through this: compute the new scale/offset, then clamp the offset to the
  // image's displayed size at that scale so a drag or zoom step can never leave the viewport uncovered.
  function applyZoom(nextScale: number, nextOffset: Point) {
    const { vpW, vpH, imgW, imgH } = measure();
    const fit = computeFitScale(imgW, imgH, vpW, vpH);
    const displayed = computeDisplaySize(imgW, imgH, fit, nextScale);
    setZoom({ scale: nextScale, offset: clampPanOffset(nextOffset, displayed.width, displayed.height, vpW, vpH) });
  }

  const zoomBy = useCallback((direction: 1 | -1) => {
    setZoom((z) => {
      const { vpW, vpH, imgW, imgH } = measure();
      const max = computeMaxScale(imgW, imgH, vpW, vpH);
      const nextScale = stepScale(z.scale, direction, MIN_SCALE, max);
      const nextOffset = zoomTowardPoint(z.offset, z.scale, nextScale, { x: 0, y: 0 });
      const fit = computeFitScale(imgW, imgH, vpW, vpH);
      const displayed = computeDisplaySize(imgW, imgH, fit, nextScale);
      return { scale: nextScale, offset: clampPanOffset(nextOffset, displayed.width, displayed.height, vpW, vpH) };
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const resetZoom = useCallback(() => setZoom(initialZoomState()), []);

  // Anchors the toggle at a screen point (the double-click position) when zooming in to native, so the
  // spot you double-clicked stays put rather than the image re-centering under you.
  const toggleFitNative = useCallback((clientX: number, clientY: number) => {
    setZoom((z) => {
      const { vpW, vpH, vpLeft, vpTop, imgW, imgH } = measure();
      const fit = computeFitScale(imgW, imgH, vpW, vpH);
      const native = computeNativeScale(imgW, imgH, vpW, vpH);
      const max = computeMaxScale(imgW, imgH, vpW, vpH);
      const atFit = z.scale <= MIN_SCALE + 1e-6;
      const nextScale = clampScale(atFit ? native : MIN_SCALE, MIN_SCALE, max);
      const cursor: Point = { x: clientX - vpLeft - vpW / 2, y: clientY - vpTop - vpH / 2 };
      const nextOffset = atFit ? zoomTowardPoint(z.offset, z.scale, nextScale, cursor) : { x: 0, y: 0 };
      const displayed = computeDisplaySize(imgW, imgH, fit, nextScale);
      return { scale: nextScale, offset: clampPanOffset(nextOffset, displayed.width, displayed.height, vpW, vpH) };
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Reset zoom/pan to fit whenever the caller moves to a different capture, so each one opens clean.
  useEffect(() => {
    setZoom(initialZoomState());
  }, [index]);

  useEffect(() => {
    // Nothing to page through - skip wiring the listener at all rather than dividing by shots.length.
    if (shots.length === 0) return;
    function onKey(e: KeyboardEvent) {
      if (e.key === "Escape") onClose();
      else if (e.key === "ArrowRight") onIndexChange((index + 1) % shots.length);
      else if (e.key === "ArrowLeft") onIndexChange((index - 1 + shots.length) % shots.length);
      else if (e.key === "+" || e.key === "=") zoomBy(1);
      else if (e.key === "-") zoomBy(-1);
      else if (e.key === "0") resetZoom();
    }
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [index, shots.length, onClose, onIndexChange, zoomBy, resetZoom]);

  // Wheel zooms toward the pointer. Attached as a native, non-passive listener (rather than React's
  // onWheel, which React attaches passively) so `preventDefault` can stop the page from scrolling/zooming
  // under the modal while the pointer is over the capture.
  useEffect(() => {
    const el = viewportRef.current;
    if (!el) return;
    function onWheel(e: WheelEvent) {
      if (e.cancelable) e.preventDefault();
      const rect = el!.getBoundingClientRect();
      const cursor: Point = { x: e.clientX - rect.left - rect.width / 2, y: e.clientY - rect.top - rect.height / 2 };
      setZoom((z) => {
        const imgW = imgRef.current?.naturalWidth ?? 0;
        const imgH = imgRef.current?.naturalHeight ?? 0;
        const max = computeMaxScale(imgW, imgH, rect.width, rect.height);
        const factor = Math.exp(-e.deltaY * 0.0015);
        const nextScale = clampScale(z.scale * factor, MIN_SCALE, max);
        const nextOffset = zoomTowardPoint(z.offset, z.scale, nextScale, cursor);
        const fit = computeFitScale(imgW, imgH, rect.width, rect.height);
        const displayed = computeDisplaySize(imgW, imgH, fit, nextScale);
        return { scale: nextScale, offset: clampPanOffset(nextOffset, displayed.width, displayed.height, rect.width, rect.height) };
      });
    }
    el.addEventListener("wheel", onWheel, { passive: false });
    return () => el.removeEventListener("wheel", onWheel);
  }, [shot?.id]);

  if (!shot) return null;

  const isZoomed = zoom.scale > MIN_SCALE + 1e-6;
  const { vpW, vpH, imgW, imgH } = measure();
  const zoomPct = Math.round((zoom.scale / computeNativeScale(imgW, imgH, vpW, vpH)) * 100);

  function handlePointerDown(e: React.PointerEvent<HTMLImageElement>) {
    if (!isZoomed) return;
    dragRef.current = { startX: e.clientX, startY: e.clientY, startOffset: zoom.offset };
    setDragging(true);
    imgRef.current?.setPointerCapture?.(e.pointerId);
  }

  function handlePointerMove(e: React.PointerEvent<HTMLImageElement>) {
    const drag = dragRef.current;
    if (!drag) return;
    const nextOffset = { x: drag.startOffset.x + (e.clientX - drag.startX), y: drag.startOffset.y + (e.clientY - drag.startY) };
    applyZoom(zoom.scale, nextOffset);
  }

  function handlePointerUp(e: React.PointerEvent<HTMLImageElement>) {
    dragRef.current = null;
    setDragging(false);
    imgRef.current?.releasePointerCapture?.(e.pointerId);
  }

  function handleDoubleClick(e: React.MouseEvent<HTMLImageElement>) {
    toggleFitNative(e.clientX, e.clientY);
  }

  const btn =
    "rounded border px-2 py-1 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800";

  return (
    <div className="fixed inset-0 z-50 flex flex-col items-center justify-center bg-black/40 p-4" onClick={onClose}>
      <div
        role="dialog"
        aria-label={t("screenshotAlt", { time: formatDuration(shot.capturedAtMs) })}
        className={`flex max-h-full w-full flex-col gap-2 rounded-lg border bg-white p-3 shadow-xl dark:border-gray-700 dark:bg-gray-900 ${
          expanded ? "max-w-[98vw]" : "max-w-5xl"
        }`}
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex flex-wrap items-center gap-2">
          <button
            type="button"
            className={btn}
            aria-label={t("screenshotPrev")}
            onClick={() => onIndexChange((index - 1 + shots.length) % shots.length)}
          >
            ◀
          </button>
          <span className="text-sm tabular-nums text-gray-600 dark:text-gray-300">
            {t("screenshotPosition", { current: index + 1, total: shots.length })}
          </span>
          <button
            type="button"
            className={btn}
            aria-label={t("screenshotNext")}
            onClick={() => onIndexChange((index + 1) % shots.length)}
          >
            ▶
          </button>
          {onJump && (
            <button
              type="button"
              className={btn}
              aria-label={t("screenshotJump", { time: formatDuration(shot.capturedAtMs) })}
              onClick={() => onJump(shot.capturedAtMs)}
            >
              {formatDuration(shot.capturedAtMs)}
            </button>
          )}
          <button
            type="button"
            className={btn}
            aria-label={t("screenshotZoomOut")}
            onClick={() => zoomBy(-1)}
          >
            -
          </button>
          <button
            type="button"
            className={btn}
            aria-label={t("screenshotZoomLevel", { pct: zoomPct })}
            title={t("screenshotZoomReset")}
            onClick={resetZoom}
          >
            {zoomPct}%
          </button>
          <button
            type="button"
            className={btn}
            aria-label={t("screenshotZoomIn")}
            onClick={() => zoomBy(1)}
          >
            +
          </button>
          <span className="flex-1" />
          <button
            type="button"
            className={btn}
            aria-pressed={expanded}
            aria-label={expanded ? t("screenshotCollapse") : t("screenshotExpand")}
            onClick={() => setExpanded((v) => !v)}
          >
            ⛶
          </button>
          <a
            className={btn}
            href={api.screenshotContentUrl(recordingId, shot.id)}
            download
            aria-label={t("screenshotDownload")}
          >
            ⤓
          </a>
          {onDelete && (
            <button
              type="button"
              aria-label={t("screenshotDelete")}
              className="rounded border border-red-300 px-2 py-1 text-sm text-red-600 hover:bg-red-50 dark:border-red-800 dark:text-red-400 dark:hover:bg-red-950"
              onClick={() => onDelete(shot.id)}
            >
              ✕
            </button>
          )}
          <button type="button" autoFocus className={btn} aria-label={t("screenshotClose")} onClick={onClose}>
            ✕
          </button>
        </div>
        <div
          ref={viewportRef}
          className={`flex w-full items-center justify-center overflow-hidden ${expanded ? "max-h-[92vh]" : "max-h-[75vh]"}`}
        >
          <img
            ref={imgRef}
            src={api.screenshotContentUrl(recordingId, shot.id)}
            alt={t("screenshotAlt", { time: formatDuration(shot.capturedAtMs) })}
            draggable={false}
            onLoad={() => remeasure((n) => n + 1)}
            className={`w-auto max-w-full self-center object-contain ${expanded ? "max-h-[92vh]" : "max-h-[75vh]"} ${
              isZoomed ? (dragging ? "cursor-grabbing" : "cursor-grab") : ""
            }`}
            style={{
              transform: `translate(${zoom.offset.x}px, ${zoom.offset.y}px) scale(${zoom.scale})`,
              touchAction: "none",
            }}
            onPointerDown={handlePointerDown}
            onPointerMove={handlePointerMove}
            onPointerUp={handlePointerUp}
            onPointerCancel={handlePointerUp}
            onDoubleClick={handleDoubleClick}
          />
        </div>
      </div>
    </div>
  );
}
