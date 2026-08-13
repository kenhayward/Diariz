import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";

/// Asked for when the user re-transcribes: optional diarization speaker-count hints (the exception, not
/// the norm — used mainly to split two people the diarizer merged into one).
export default function RetranscribeModal({
  initialMin,
  initialMax,
  hasRevisions,
  busy,
  onCancel,
  onConfirm,
}: {
  initialMin: number | null;
  initialMax: number | null;
  hasRevisions: boolean;
  busy: boolean;
  onCancel: () => void;
  onConfirm: (min: number | null, max: number | null) => void;
}) {
  const { t } = useTranslation("workspace");
  const [min, setMin] = useState(initialMin != null ? String(initialMin) : "");
  const [max, setMax] = useState(initialMax != null ? String(initialMax) : "");

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onCancel();
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onCancel]);

  function confirm() {
    onConfirm(min.trim() ? Number(min) : null, max.trim() ? Number(max) : null);
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" onClick={onCancel}>
      <div
        role="dialog"
        aria-label={t("retranscribeTitle")}
        className="w-full max-w-md rounded-lg border bg-white p-5 shadow-xl dark:border-gray-700 dark:bg-gray-900"
        onClick={(e) => e.stopPropagation()}
      >
        <h2 className="mb-1 text-base font-semibold dark:text-gray-100">{t("retranscribeTitle")}</h2>
        <p className="mb-4 text-xs text-gray-500 dark:text-gray-400">{t("retranscribeHelp")}</p>
        {hasRevisions && (
          <p className="mb-4 rounded border border-amber-300 bg-amber-50 px-3 py-2 text-xs text-amber-800 dark:border-amber-700/60 dark:bg-amber-900/30 dark:text-amber-300">
            {t("retranscribeRevisionsWarning")}
          </p>
        )}
        <div className="flex items-center gap-4 text-sm">
          <label className="flex items-center gap-2 text-gray-600 dark:text-gray-300">
            {t("minSpeakers")}
            <input
              type="number"
              min={1}
              value={min}
              onChange={(e) => setMin(e.target.value)}
              aria-label={t("minSpeakersAria")}
              className="w-20 rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
            />
          </label>
          <label className="flex items-center gap-2 text-gray-600 dark:text-gray-300">
            {t("maxSpeakers")}
            <input
              type="number"
              min={1}
              value={max}
              onChange={(e) => setMax(e.target.value)}
              aria-label={t("maxSpeakersAria")}
              className="w-20 rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
            />
          </label>
        </div>
        <div className="mt-5 flex justify-end gap-2">
          <button
            type="button"
            onClick={onCancel}
            className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("common:cancel")}
          </button>
          <button
            type="button"
            onClick={confirm}
            disabled={busy}
            className="rounded bg-gray-900 px-3 py-1.5 text-sm text-white disabled:opacity-50 dark:bg-gray-100 dark:text-gray-900"
          >
            {busy ? t("starting") : t("retranscribeTitle")}
          </button>
        </div>
      </div>
    </div>
  );
}
