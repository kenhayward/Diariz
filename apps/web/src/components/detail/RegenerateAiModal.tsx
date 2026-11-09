import { useState } from "react";
import { useTranslation } from "react-i18next";

export type AiOperation = "summary" | "minutes" | "actions" | "tags" | "embeddings";
export type AiSelection = Record<AiOperation, boolean>;

// Catalogue keys (workspace namespace), not text: the labels are resolved per render so they follow the UI language.
const OPTIONS: { key: AiOperation; label: string; description: string }[] = [
  { key: "summary", label: "regenAiSummaryLabel", description: "regenAiSummaryDesc" },
  { key: "minutes", label: "regenAiMinutesLabel", description: "regenAiMinutesDesc" },
  { key: "actions", label: "regenAiActionsLabel", description: "regenAiActionsDesc" },
  { key: "tags", label: "regenAiTagsLabel", description: "regenAiTagsDesc" },
  { key: "embeddings", label: "regenAiEmbeddingsLabel", description: "regenAiEmbeddingsDesc" },
];

export default function RegenerateAiModal({
  busy, summaryEdited, minutesEdited, onCancel, onConfirm,
}: {
  busy: boolean;
  summaryEdited: boolean;
  minutesEdited: boolean;
  onCancel: () => void;
  onConfirm: (selection: AiSelection, overwriteManualEdits: boolean) => Promise<void>;
}) {
  const { t } = useTranslation(["workspace", "common"]);
  const [selection, setSelection] = useState<AiSelection>({
    summary: false, minutes: false, actions: false, tags: false, embeddings: false,
  });
  const [overwrite, setOverwrite] = useState(false);
  const anySelected = Object.values(selection).some(Boolean);
  const editedSelected = (selection.summary && summaryEdited) || (selection.minutes && minutesEdited);

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4" role="presentation" onMouseDown={(e) => { if (e.target === e.currentTarget && !busy) onCancel(); }}>
      <div role="dialog" aria-modal="true" aria-labelledby="regenerate-ai-title" className="w-full max-w-lg rounded-xl bg-white p-5 shadow-xl dark:bg-gray-800 dark:text-gray-100">
        <h2 id="regenerate-ai-title" className="text-lg font-semibold">{t("regenAiTitle")}</h2>
        <p className="mt-1 text-sm text-gray-500 dark:text-gray-400">{t("regenAiIntro")}</p>
        <div className="mt-4 space-y-2">
          {OPTIONS.map(({ key, label, description }) => (
            <label key={key} className="flex cursor-pointer items-start gap-3 rounded-lg border border-gray-200 p-3 dark:border-gray-600">
              <input type="checkbox" className="mt-1" checked={selection[key]} disabled={busy} onChange={(e) => setSelection((prev) => ({ ...prev, [key]: e.target.checked }))} />
              <span><span className="block text-sm font-medium">{t(label)}</span><span className="block text-xs text-gray-500 dark:text-gray-400">{t(description)}</span></span>
            </label>
          ))}
        </div>
        {editedSelected && (
          <label className="mt-4 flex items-start gap-2 rounded-lg border border-amber-300 bg-amber-50 p-3 text-sm text-amber-900 dark:border-amber-700 dark:bg-amber-950/40 dark:text-amber-200">
            <input type="checkbox" className="mt-1" checked={overwrite} disabled={busy} onChange={(e) => setOverwrite(e.target.checked)} />
            <span>{t("regenAiOverwriteConsent")}</span>
          </label>
        )}
        <div className="mt-5 flex justify-end gap-2">
          <button type="button" disabled={busy} onClick={onCancel} className="rounded-lg border px-4 py-2 text-sm dark:border-gray-600">{t("common:cancel")}</button>
          <button type="button" disabled={busy || !anySelected || (editedSelected && !overwrite)} onClick={() => void onConfirm(selection, editedSelected && overwrite)} className="rounded-lg bg-blue-600 px-4 py-2 text-sm font-semibold text-white disabled:opacity-40">{busy ? t("regenAiQueueing") : t("regenAiStart")}</button>
        </div>
      </div>
    </div>
  );
}
