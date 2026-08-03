import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";
import { snapshot } from "../lib/trail";

// Keep at least this many CSS pixels of the dialog on-screen along each axis, so a drag can never carry
// it fully off the viewport (and leave the user unable to reach it again).
const MIN_VISIBLE_PX = 40;

type Offset = { x: number; y: number };

/// Clamp a proposed drag offset against the dialog's current on-screen rect so it always keeps
/// MIN_VISIBLE_PX of itself reachable within the viewport, in both directions.
function clampOffset(offset: Offset, rect: DOMRect): Offset {
  const minX = Math.min(MIN_VISIBLE_PX - rect.width - rect.left, window.innerWidth - MIN_VISIBLE_PX - rect.left);
  const maxX = Math.max(MIN_VISIBLE_PX - rect.width - rect.left, window.innerWidth - MIN_VISIBLE_PX - rect.left);
  const minY = Math.min(MIN_VISIBLE_PX - rect.height - rect.top, window.innerHeight - MIN_VISIBLE_PX - rect.top);
  const maxY = Math.max(MIN_VISIBLE_PX - rect.height - rect.top, window.innerHeight - MIN_VISIBLE_PX - rect.top);
  return {
    x: Math.min(Math.max(offset.x, minX), maxX),
    y: Math.min(Math.max(offset.y, minY), maxY),
  };
}

/**
 * "Provide Feedback": a free-text report the user can raise from the account menu, sent together with the
 * scrubbed action trail (`lib/trail`) and the current route so a maintainer has context without a screenshot.
 *
 * Draggable by its header. This looks like decoration but is not: a later, deferred phase attaches a
 * screenshot of the screen to the report, and a dialog sitting on top of the very thing being reported
 * would make it uncapturable. Drag exists so the dialog can be moved out of the way first. It's restricted
 * to the header (data-testid="feedback-drag-handle") - the body holds the description textarea, and
 * starting a drag from there would fight normal text selection.
 */
export default function FeedbackModal({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation("account");
  const [description, setDescription] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const dialogRef = useRef<HTMLFormElement>(null);
  const dragStart = useRef<{ x: number; y: number; offset: Offset } | null>(null);
  // Not persisted: every time the modal (re)mounts, it opens at its default position.
  const [dragging, setDragging] = useState(false);
  const [offset, setOffset] = useState<Offset>({ x: 0, y: 0 });

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  // Window-level listeners (rather than only on the header) so the drag keeps tracking even once the
  // pointer leaves the handle - only live while a drag is in progress, cleaned up as soon as it ends.
  useEffect(() => {
    if (!dragging) return;
    function onMove(e: MouseEvent) {
      const start = dragStart.current;
      const dialog = dialogRef.current;
      if (!start || !dialog) return;
      const next = { x: start.offset.x + (e.clientX - start.x), y: start.offset.y + (e.clientY - start.y) };
      setOffset(clampOffset(next, dialog.getBoundingClientRect()));
    }
    function onUp() {
      setDragging(false);
    }
    window.addEventListener("mousemove", onMove);
    window.addEventListener("mouseup", onUp);
    return () => {
      window.removeEventListener("mousemove", onMove);
      window.removeEventListener("mouseup", onUp);
    };
  }, [dragging]);

  function onHeaderMouseDown(e: React.MouseEvent) {
    dragStart.current = { x: e.clientX, y: e.clientY, offset };
    setDragging(true);
  }

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    const trimmed = description.trim();
    if (!trimmed) return;
    setBusy(true);
    setError(null);
    try {
      await api.submitFeedback(trimmed, window.location.pathname, JSON.stringify(snapshot()));
      onClose();
    } catch (err) {
      setError(apiErrorMessage(err));
      setBusy(false);
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" onClick={onClose}>
      <form
        ref={dialogRef}
        role="dialog"
        aria-label={t("provideFeedback")}
        aria-modal="true"
        className="w-full max-w-md space-y-3 rounded-lg border bg-white p-4 shadow-xl dark:border-gray-700 dark:bg-gray-900"
        style={{ transform: `translate(${offset.x}px, ${offset.y}px)` }}
        onClick={(e) => e.stopPropagation()}
        onSubmit={submit}
      >
        <div
          data-testid="feedback-drag-handle"
          onMouseDown={onHeaderMouseDown}
          className="-m-1 cursor-grab select-none rounded p-1 active:cursor-grabbing"
        >
          <h2 className="text-base font-semibold dark:text-gray-100">{t("provideFeedback")}</h2>
        </div>

        <label className="block text-xs text-gray-500 dark:text-gray-400">
          {t("feedbackDescriptionLabel")}
          <textarea
            autoFocus
            rows={5}
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            placeholder={t("feedbackPlaceholder")}
            className="mt-1 w-full resize-none rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
          />
        </label>

        {error && <p className="text-sm text-red-600 dark:text-red-400">{error}</p>}

        <div className="flex justify-end gap-2">
          <button
            type="button"
            onClick={onClose}
            className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("common:cancel")}
          </button>
          <button
            type="submit"
            disabled={busy}
            className="rounded bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
          >
            {t("feedbackSend")}
          </button>
        </div>
      </form>
    </div>
  );
}
