import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { apiErrorMessage } from "../lib/api";
import { renderMarkdown } from "../lib/markdown";
import MeetingMinutesEditModal from "./MeetingMinutesEditModal";

/// View/edit a formula result's generated Markdown. Mirrors MarkdownAttachmentEditModal: fetches the
/// current content, then either opens the TipTap editor (the `editable` default) or renders it read-only
/// via renderMarkdown. RecordingDetail/SectionDetail compute `editable` per result - the creator OR (per
/// side) the recording's owner / a room member with ManageContents - mirroring the server's own edit gate
/// (FormulaResultsController.CanEdit / SectionFormulaResultsController.CanEditAsync), so Save is never shown
/// to a caller whose PUT would 403.
export default function FormulaResultEditModal({
  name,
  load,
  save,
  onClose,
  onSaved,
  editable = true,
}: {
  name: string;
  load: () => Promise<string>;
  save: (markdown: string) => Promise<void>;
  onClose: () => void;
  onSaved?: () => void;
  editable?: boolean;
}) {
  const { t } = useTranslation(["workspace", "common"]);
  const [initial, setInitial] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    load().then(
      (text) => !cancelled && setInitial(text),
      (e) => !cancelled && setError(apiErrorMessage(e, t("workspace:formulaResultLoadFailed"))),
    );
    return () => {
      cancelled = true;
    };
    // Runs once for this open; `load` is a fresh closure captured on mount.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  if (error || initial === null) {
    return (
      <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" role="dialog" aria-modal="true">
        <div className="w-full max-w-md rounded-lg bg-white p-5 shadow-xl dark:bg-gray-900">
          <h2 className="mb-3 text-base font-semibold dark:text-gray-100">{name}</h2>
          {error ? (
            <p className="text-sm text-red-600 dark:text-red-400">{error}</p>
          ) : (
            <p className="text-sm text-gray-500 dark:text-gray-400">{t("common:loading")}</p>
          )}
          <div className="mt-4 flex justify-end">
            <button
              onClick={onClose}
              className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
            >
              {t("common:close")}
            </button>
          </div>
        </div>
      </div>
    );
  }

  if (!editable) {
    return (
      <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" role="dialog" aria-modal="true">
        <div className="flex max-h-[85vh] w-full max-w-3xl flex-col rounded-lg bg-white p-5 shadow-xl dark:bg-gray-900">
          <h2 className="mb-3 text-base font-semibold dark:text-gray-100">{name}</h2>
          <div
            className="chat-md min-h-0 flex-1 overflow-auto rounded border p-3 text-sm dark:border-gray-700 dark:text-gray-100"
            dangerouslySetInnerHTML={{ __html: renderMarkdown(initial) }}
          />
          <div className="mt-4 flex justify-end">
            <button
              onClick={onClose}
              className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
            >
              {t("common:close")}
            </button>
          </div>
        </div>
      </div>
    );
  }

  return (
    <MeetingMinutesEditModal
      title={name}
      initial={initial}
      onClose={onClose}
      onSave={async (markdown) => {
        await save(markdown);
        onSaved?.();
        onClose();
      }}
    />
  );
}
