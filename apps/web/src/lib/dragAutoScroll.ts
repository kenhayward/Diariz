/// Edge auto-scroll for native HTML5 drag-and-drop. Chromium does not scroll a container while you drag
/// near its top/bottom edge, so a drop target outside the visible viewport is unreachable in a long list.
/// `edgeScrollVelocity` is the pure timing logic (unit-tested); `useDragAutoScroll` is the thin DOM glue
/// that runs a requestAnimationFrame loop applying that velocity to a scroll container during a drag.

import { useCallback, useRef } from "react";

export interface EdgeScrollOptions {
  /** Distance (px) from an edge where auto-scroll begins. */
  threshold?: number;
  /** Scroll speed (px per animation frame) at the very edge. */
  maxSpeed?: number;
}

const DEFAULT_THRESHOLD = 48;
const DEFAULT_MAX_SPEED = 18;

/// How fast (px/frame) to scroll a container whose viewport spans `rect.top`..`rect.bottom` when a drag
/// pointer sits at `pointerY` (a clientY). Negative = scroll up, positive = down, 0 = none. Within
/// `threshold` of an edge the magnitude ramps linearly from 0 (at the threshold boundary) to `maxSpeed`
/// (at or past the edge). When the container is shorter than two thresholds the nearer edge wins.
export function edgeScrollVelocity(
  rect: Pick<DOMRect, "top" | "bottom">,
  pointerY: number,
  opts: EdgeScrollOptions = {},
): number {
  const threshold = opts.threshold ?? DEFAULT_THRESHOLD;
  const maxSpeed = opts.maxSpeed ?? DEFAULT_MAX_SPEED;

  const distFromTop = pointerY - rect.top;
  const distFromBottom = rect.bottom - pointerY;

  // Ramp 0→maxSpeed as the pointer moves from the threshold boundary to the edge (and past it).
  const ramp = (dist: number) => Math.round(maxSpeed * Math.min(1, Math.max(0, (threshold - dist) / threshold)));

  // In an overlapping (short container) zone, scroll toward whichever edge is nearer.
  const nearTop = distFromTop < threshold && distFromTop <= distFromBottom;
  if (nearTop) return -ramp(distFromTop);
  if (distFromBottom < threshold) return ramp(distFromBottom);
  return 0;
}

/// React hook: spread the returned **callback ref** onto a scrollable element (`ref={useDragAutoScroll()}`).
/// While a drag hovers near its top/bottom edge the element auto-scrolls so off-screen drop targets come
/// into view. Observes `dragover` passively (no preventDefault — the row/section handlers own the drop) and
/// stops on drop/dragend/leave. A **callback ref** is used deliberately (not useRef+useEffect): the scroll
/// container often mounts *after* the hook's first render — e.g. while the recordings query is still loading
/// the panel shows a placeholder — and a one-shot effect would never see the late-mounted node, so listeners
/// would never attach. A callback ref runs every time the node attaches/detaches, regardless of timing. Pass
/// a stable `opts` (or none) — it keys the callback's identity.
export function useDragAutoScroll<T extends HTMLElement>(opts?: EdgeScrollOptions) {
  const cleanup = useRef<(() => void) | null>(null);

  return useCallback(
    (node: T | null) => {
      // Detach from any previously-attached node first (React passes null on unmount / before re-attach).
      cleanup.current?.();
      cleanup.current = null;
      if (!node) return;

      let velocity = 0;
      let raf: number | null = null;

      const step = () => {
        if (velocity !== 0) {
          node.scrollTop += velocity;
          raf = requestAnimationFrame(step);
        } else {
          raf = null;
        }
      };
      const stop = () => {
        velocity = 0;
        if (raf !== null) {
          cancelAnimationFrame(raf);
          raf = null;
        }
      };
      const onDragOver = (e: DragEvent) => {
        velocity = edgeScrollVelocity(node.getBoundingClientRect(), e.clientY, opts);
        if (velocity !== 0 && raf === null) raf = requestAnimationFrame(step);
      };
      const onDragLeave = (e: DragEvent) => {
        // dragleave also fires when moving onto a child row (relatedTarget still inside) — only stop when
        // the cursor truly leaves the container, or leaves the window (relatedTarget null).
        if (!e.relatedTarget || !node.contains(e.relatedTarget as Node)) stop();
      };

      node.addEventListener("dragover", onDragOver);
      node.addEventListener("dragleave", onDragLeave);
      node.addEventListener("drop", stop);
      // A drop or dragend anywhere ends the gesture — clean up even when it happens outside the list.
      window.addEventListener("drop", stop);
      window.addEventListener("dragend", stop);

      cleanup.current = () => {
        node.removeEventListener("dragover", onDragOver);
        node.removeEventListener("dragleave", onDragLeave);
        node.removeEventListener("drop", stop);
        window.removeEventListener("drop", stop);
        window.removeEventListener("dragend", stop);
        stop();
      };
    },
    [opts],
  );
}
