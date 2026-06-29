import { useState } from "react";
import { useTranslation } from "react-i18next";

/// Modal for manually writing or editing a transcript's summary. Seeded with the current text;
/// Save sends the new text to the caller (which persists it and flags it user-edited).
export default function SummaryEditModal({
  initial,
  onClose,
  onSave,
}: {
  initial: string;
  onClose: () => void;
  onSave: (text: string) => Promise<void> | void;
}) {
  const { t } = useTranslation("workspace");
  const [value, setValue] = useState(initial);
  const [saving, setSaving] = useState(false);

  async function save() {
    setSaving(true);
    try {
      await onSave(value);
    } finally {
      setSaving(false);
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" role="dialog" aria-modal="true">
      <div className="w-full max-w-2xl rounded-lg bg-white p-5 shadow-xl dark:bg-gray-900">
        <h2 className="mb-3 text-base font-semibold dark:text-gray-100">{t("summaryEditTitle")}</h2>
        <textarea
          value={value}
          onChange={(e) => setValue(e.target.value)}
          rows={12}
          aria-label={t("summaryEditTitle")}
          placeholder={t("summaryEditPlaceholder")}
          className="w-full resize-y rounded border px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
        />
        <div className="mt-4 flex justify-end gap-2">
          <button
            onClick={onClose}
            disabled={saving}
            className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("common:cancel")}
          </button>
          <button
            onClick={save}
            disabled={saving}
            className="rounded bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
          >
            {t("common:save")}
          </button>
        </div>
      </div>
    </div>
  );
}
