import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { apiErrorMessage } from "../../lib/api";
import type { SegmentDto } from "../../lib/types";

export default function SegmentEditModal({
  seg,
  onClose,
  onSave,
}: {
  seg: SegmentDto;
  onClose: () => void;
  onSave: (text: string | null) => Promise<void>;
}) {
  const { t } = useTranslation("workspace");
  const [text, setText] = useState(seg.text);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const taRef = useRef<HTMLTextAreaElement>(null);
  const revised = seg.revised != null;

  // Grow the textarea to fit its content; CSS max-height keeps the modal on-screen (it scrolls past that).
  function autosize() {
    const el = taRef.current;
    if (!el) return;
    el.style.height = "auto";
    el.style.height = `${el.scrollHeight}px`;
  }
  // Size to the initial text once mounted.
  useEffect(() => autosize(), []);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  async function save(value: string | null) {
    setBusy(true);
    setError(null);
    try {
      await onSave(value);
    } catch (e) {
      setError(apiErrorMessage(e, t("errSaveSegment")));
      setBusy(false);
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" onClick={onClose}>
      <div
        role="dialog"
        aria-label={t("editSegment")}
        className="w-full max-w-3xl rounded-lg border bg-white p-5 shadow-xl dark:border-gray-700 dark:bg-gray-900"
        onClick={(e) => e.stopPropagation()}
      >
        <h2 className="mb-3 text-base font-semibold dark:text-gray-100">{t("editSegment")}</h2>
        <textarea
          ref={taRef}
          autoFocus
          value={text}
          onChange={(e) => {
            setText(e.target.value);
            autosize();
          }}
          aria-label={t("segmentTextAria")}
          className="block max-h-[60vh] min-h-[8rem] w-full resize-none overflow-y-auto rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
        />
        {revised && (
          <p className="mt-2 text-xs text-gray-500 dark:text-gray-400">
            <span className="font-medium">{t("originalLabel")}</span> {seg.original}
          </p>
        )}
        {error && <p className="mt-2 text-sm text-red-600 dark:text-red-400">{error}</p>}
        <div className="mt-3 flex items-center justify-end gap-2">
          {/* Clearing a revision restores the model's original words (sends null to the server). */}
          {revised && (
            <button
              type="button"
              onClick={() => save(null)}
              disabled={busy}
              className="mr-auto rounded border px-3 py-1.5 text-sm hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
            >
              {t("resetToOriginal")}
            </button>
          )}
          <button
            type="button"
            onClick={onClose}
            className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("common:cancel")}
          </button>
          <button
            type="button"
            onClick={() => save(text)}
            disabled={busy}
            className="rounded bg-gray-900 px-3 py-1.5 text-sm text-white disabled:opacity-50 dark:bg-gray-100 dark:text-gray-900"
          >
            {busy ? t("common:saving") : t("common:save")}
          </button>
        </div>
      </div>
    </div>
  );
}
