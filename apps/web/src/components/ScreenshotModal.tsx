import { useEffect } from "react";
import { useTranslation } from "react-i18next";
import { api } from "../lib/api";
import type { Screenshot } from "../lib/types";

const fmt = (ms: number) => {
  const s = Math.floor(ms / 1000);
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
};

/// Full-size viewer for one capture, with prev/next through the recording's captures, a jump to the
/// moment it was taken, download and delete. Index is owned by the caller so the transcript row, the
/// Notes section and the strip all agree on which capture is open. Mirrors EditActionModal's shell
/// (backdrop click + stop-propagation, Escape via a document keydown listener cleaned up on unmount).
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
  // Guard against an out-of-range index (e.g. the last capture was deleted while open) rather than
  // let `shots[index]` come back undefined and blow up the image/alt-text below.
  const shot = index >= 0 && index < shots.length ? shots[index] : undefined;

  useEffect(() => {
    // Nothing to page through - skip wiring the listener at all rather than dividing by shots.length.
    if (shots.length === 0) return;
    function onKey(e: KeyboardEvent) {
      if (e.key === "Escape") onClose();
      else if (e.key === "ArrowRight") onIndexChange((index + 1) % shots.length);
      else if (e.key === "ArrowLeft") onIndexChange((index - 1 + shots.length) % shots.length);
    }
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [index, shots.length, onClose, onIndexChange]);

  if (!shot) return null;

  const btn =
    "rounded border px-2 py-1 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800";

  return (
    <div className="fixed inset-0 z-50 flex flex-col items-center justify-center bg-black/40 p-4" onClick={onClose}>
      <div
        role="dialog"
        aria-label={t("screenshotAlt", { time: fmt(shot.capturedAtMs) })}
        className="flex max-h-full w-full max-w-5xl flex-col gap-2 rounded-lg border bg-white p-3 shadow-xl dark:border-gray-700 dark:bg-gray-900"
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
              aria-label={t("screenshotJump", { time: fmt(shot.capturedAtMs) })}
              onClick={() => onJump(shot.capturedAtMs)}
            >
              {fmt(shot.capturedAtMs)}
            </button>
          )}
          <span className="flex-1" />
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
        <img
          src={api.screenshotContentUrl(recordingId, shot.id)}
          alt={t("screenshotAlt", { time: fmt(shot.capturedAtMs) })}
          className="max-h-[75vh] w-auto self-center object-contain"
        />
      </div>
    </div>
  );
}
