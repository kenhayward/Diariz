import { useEffect, useLayoutEffect, useState } from "react";
import { createPortal } from "react-dom";
import { useTranslation } from "react-i18next";
import { useTour } from "../lib/tour";

/// Full-screen guided-tour overlay: dims the app, spotlights the current region (a `data-tour="…"`
/// element) via a box-shadow cutout, and shows an explanatory card. Replaces nothing structural — it
/// just renders on top when the tour is active.
export default function TourOverlay() {
  const { t } = useTranslation("tour");
  const { active, index, steps, next, back, end } = useTour();
  const step = active ? steps[index] : null;
  const [rect, setRect] = useState<DOMRect | null>(null);

  // Track the spotlighted element's position (re-measure on resize + while panels collapse/expand).
  useLayoutEffect(() => {
    if (!step) return;
    const measure = () => {
      // A region may have two anchors (an expanded panel and its collapsed rail); pick whichever is
      // currently visible (non-zero size).
      let chosen: DOMRect | null = null;
      document.querySelectorAll(`[data-tour="${step.target}"]`).forEach((el) => {
        const r = el.getBoundingClientRect();
        if (!chosen && r.width > 0 && r.height > 0) chosen = r;
      });
      setRect(chosen);
    };
    measure();
    window.addEventListener("resize", measure);
    const id = window.setInterval(measure, 400);
    return () => {
      window.removeEventListener("resize", measure);
      window.clearInterval(id);
    };
  }, [step]);

  // Escape skips the tour.
  useEffect(() => {
    if (!active) return;
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && end();
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [active, end]);

  if (!step) return null;

  const title = t(`${step.target}.title`);
  const body = t(`${step.target}.body`);
  const isLast = index === steps.length - 1;
  const pad = 6;
  const spot = rect
    ? { left: rect.left - pad, top: rect.top - pad, width: rect.width + pad * 2, height: rect.height + pad * 2 }
    : null;

  // Card sits bottom-centre by default; if the spotlight is in the lower half, move it to the top so it
  // doesn't cover the highlighted region.
  const spotlightInLowerHalf = !!rect && rect.top > window.innerHeight / 2;
  const cardVertical = spotlightInLowerHalf ? "top-6" : "bottom-6";

  return createPortal(
    <div className="fixed inset-0 z-[60]">
      {/* Click-blocker so the app isn't interactable mid-tour. */}
      <div className="absolute inset-0" />
      {/* Spotlight: a box-shadow extends a dim layer across everything except this rect. */}
      {spot ? (
        <div
          aria-hidden
          className="pointer-events-none absolute rounded-lg ring-2 ring-blue-400 transition-all duration-200"
          style={{
            left: spot.left,
            top: spot.top,
            width: spot.width,
            height: spot.height,
            boxShadow: "0 0 0 9999px rgba(0,0,0,0.6)",
          }}
        />
      ) : (
        <div aria-hidden className="absolute inset-0 bg-black/60" />
      )}

      <div
        role="dialog"
        aria-label={title}
        className={`pointer-events-auto fixed left-1/2 ${cardVertical} w-[min(92vw,26rem)] -translate-x-1/2 rounded-xl border bg-white p-4 shadow-2xl dark:border-gray-700 dark:bg-gray-900`}
      >
        <div className="mb-1 text-xs font-medium text-gray-400 dark:text-gray-500">
          {t("stepOf", { index: index + 1, total: steps.length })}
        </div>
        <h3 className="text-base font-semibold dark:text-gray-100">{title}</h3>
        <p className="mt-1 text-sm text-gray-600 dark:text-gray-300">{body}</p>

        <div className="mt-4 flex items-center justify-between">
          <button
            type="button"
            onClick={end}
            className="text-xs text-gray-400 hover:text-gray-600 hover:underline dark:hover:text-gray-200"
          >
            {t("skip")}
          </button>
          <div className="flex items-center gap-2">
            {index > 0 && (
              <button
                type="button"
                onClick={back}
                className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
              >
                {t("back")}
              </button>
            )}
            <button
              type="button"
              onClick={next}
              className="rounded bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700"
            >
              {isLast ? t("done") : t("next")}
            </button>
          </div>
        </div>
      </div>
    </div>,
    document.body,
  );
}
