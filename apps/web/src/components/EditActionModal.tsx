import { useEffect, useRef, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";
import type { ActionListItem } from "../lib/types";

/// Edit a single action's text/actor/deadline from the Actions tab (the inline transcript table edits in
/// place; this modal serves the narrow left panel). Saves via the per-recording update endpoint, then
/// refreshes the cross-transcript list and the source recording's detail.
export default function EditActionModal({ action, onClose }: { action: ActionListItem; onClose: () => void }) {
  const { t } = useTranslation("workspace");
  const qc = useQueryClient();
  const [text, setText] = useState(action.text);
  const [actor, setActor] = useState(action.actor);
  const [deadline, setDeadline] = useState(action.deadline);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const textRef = useRef<HTMLTextAreaElement>(null);

  // Grow the action textarea to fit its content (a long action shouldn't sit in a one-line box).
  function autosize() {
    const el = textRef.current;
    if (!el) return;
    el.style.height = "auto";
    el.style.height = `${el.scrollHeight}px`;
  }
  useEffect(() => autosize(), []);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  async function save(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await api.updateAction(action.recordingId, action.id, {
        text: text.trim(),
        actor: actor.trim(),
        deadline: deadline.trim(),
      });
      qc.invalidateQueries({ queryKey: ["actions", "all"] });
      qc.invalidateQueries({ queryKey: ["recording", action.recordingId] });
      onClose();
    } catch (err) {
      setError(apiErrorMessage(err));
      setBusy(false);
    }
  }

  const field = "w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100";

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" onClick={onClose}>
      <form
        role="dialog"
        aria-label={t("editAction")}
        className="w-full max-w-sm space-y-3 rounded-lg border bg-white p-4 shadow-xl dark:border-gray-700 dark:bg-gray-900"
        onClick={(e) => e.stopPropagation()}
        onSubmit={save}
      >
        <h2 className="text-base font-semibold dark:text-gray-100">{t("editAction")}</h2>
        <label className="block text-xs text-gray-500 dark:text-gray-400">
          {t("colAction")}
          <textarea
            ref={textRef}
            autoFocus
            rows={1}
            value={text}
            onChange={(e) => {
              setText(e.target.value);
              autosize();
            }}
            className={`mt-1 max-h-[50vh] resize-none overflow-hidden ${field}`}
          />
        </label>
        <label className="block text-xs text-gray-500 dark:text-gray-400">
          {t("colActor")}
          <input value={actor} onChange={(e) => setActor(e.target.value)} className={`mt-1 ${field}`} />
        </label>
        <label className="block text-xs text-gray-500 dark:text-gray-400">
          {t("colDeadline")}
          <input value={deadline} onChange={(e) => setDeadline(e.target.value)} className={`mt-1 ${field}`} />
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
            {t("common:save")}
          </button>
        </div>
      </form>
    </div>
  );
}
