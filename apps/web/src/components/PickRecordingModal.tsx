import { useState } from "react";
import { useTranslation } from "react-i18next";
import type { AttachmentDraft } from "../lib/types";

/// Shown when the chat "add as attachment" tool fires with several transcripts in context: the user picks
/// which one the prepared note is attached to. (With a single transcript in context the caller adds it
/// directly and this modal never appears.)
export default function PickRecordingModal({
  draft,
  onCancel,
  onPick,
}: {
  draft: AttachmentDraft;
  onCancel: () => void;
  onPick: (recordingId: string) => Promise<void> | void;
}) {
  const { t } = useTranslation(["chat", "common"]);
  const [selected, setSelected] = useState(draft.recordings[0]?.id ?? "");
  const [busy, setBusy] = useState(false);

  async function confirm() {
    if (!selected) return;
    setBusy(true);
    try {
      await onPick(selected);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" role="dialog" aria-modal="true">
      <div className="w-full max-w-sm rounded-lg bg-white p-5 shadow-xl dark:bg-gray-900">
        <h2 className="mb-1 text-base font-semibold dark:text-gray-100">{t("chat:attachPickTitle")}</h2>
        <p className="mb-3 text-sm text-gray-600 dark:text-gray-300">{t("chat:attachPickBody", { name: draft.name })}</p>
        <div className="mb-4 max-h-60 space-y-1 overflow-y-auto">
          {draft.recordings.map((r) => (
            <label
              key={r.id}
              className="flex cursor-pointer items-center gap-2 rounded px-2 py-1.5 text-sm hover:bg-gray-50 dark:text-gray-200 dark:hover:bg-gray-800"
            >
              <input
                type="radio"
                name="pick-recording"
                value={r.id}
                checked={selected === r.id}
                onChange={() => setSelected(r.id)}
              />
              <span className="truncate">{r.title}</span>
            </label>
          ))}
        </div>
        <div className="flex justify-end gap-2">
          <button
            onClick={onCancel}
            disabled={busy}
            className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("common:cancel")}
          </button>
          <button
            onClick={confirm}
            disabled={busy || !selected}
            className="rounded bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
          >
            {t("chat:attachAdd")}
          </button>
        </div>
      </div>
    </div>
  );
}
