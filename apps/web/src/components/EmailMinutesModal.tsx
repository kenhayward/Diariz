import { useState } from "react";
import { useTranslation } from "react-i18next";

/// Shown before emailing the meeting minutes when the recording has attachments: asks whether to include
/// them. (When there are no attachments the caller emails directly and this modal never appears.)
export default function EmailMinutesModal({
  count,
  onCancel,
  onChoose,
}: {
  count: number;
  onCancel: () => void;
  onChoose: (includeAttachments: boolean) => Promise<void> | void;
}) {
  const { t } = useTranslation(["workspace", "common"]);
  const [busy, setBusy] = useState(false);

  async function choose(include: boolean) {
    setBusy(true);
    try {
      await onChoose(include);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" role="dialog" aria-modal="true">
      <div className="w-full max-w-sm rounded-lg bg-white p-5 shadow-xl dark:bg-gray-900">
        <h2 className="mb-2 text-base font-semibold dark:text-gray-100">{t("workspace:emailMinutes")}</h2>
        <p className="mb-4 text-sm text-gray-600 dark:text-gray-300">
          {t("workspace:includeAttachmentsBody", { count })}
        </p>
        <div className="flex justify-end gap-2">
          <button
            onClick={onCancel}
            disabled={busy}
            className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("common:cancel")}
          </button>
          <button
            onClick={() => choose(false)}
            disabled={busy}
            className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("workspace:minutesOnly")}
          </button>
          <button
            onClick={() => choose(true)}
            disabled={busy}
            className="rounded bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
          >
            {t("workspace:includeAttachments")}
          </button>
        </div>
      </div>
    </div>
  );
}
