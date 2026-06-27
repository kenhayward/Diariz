import { useRef, useState } from "react";

/// A drag-resizable width (px), persisted to localStorage and clamped to [min, max].
/// `startResize` is a mousedown handler for a vertical separator; `edge` is which side of
/// the viewport the panel is anchored to ("left" → width grows with the cursor's x).
export function useResizableWidth(key: string, opts: { min: number; max: number; initial: number }) {
  const [width, setWidth] = useState<number>(() => {
    const stored = Number(localStorage.getItem(key));
    return stored >= opts.min && stored <= opts.max ? stored : opts.initial;
  });
  const ref = useRef(width);
  ref.current = width;

  function startResize(e: React.MouseEvent, edge: "left" | "right" = "left") {
    e.preventDefault();
    const onMove = (ev: MouseEvent) => {
      const w = edge === "left" ? ev.clientX : window.innerWidth - ev.clientX;
      setWidth(Math.min(opts.max, Math.max(opts.min, w)));
    };
    const onUp = () => {
      document.removeEventListener("mousemove", onMove);
      document.removeEventListener("mouseup", onUp);
      document.body.style.userSelect = "";
      localStorage.setItem(key, String(ref.current));
    };
    document.body.style.userSelect = "none"; // don't select text while dragging
    document.addEventListener("mousemove", onMove);
    document.addEventListener("mouseup", onUp);
  }

  return { width, startResize };
}
