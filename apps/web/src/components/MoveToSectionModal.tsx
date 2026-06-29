import { useEffect, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";

/// Move a recording into an existing section, ungroup it, or create a new section and move into it.
export default function MoveToSectionModal({
  recordingId,
  currentSectionId,
  onClose,
}: {
  recordingId: string;
  currentSectionId?: string | null; // undefined = unknown (mark nothing)
  onClose: () => void;
}) {
  const { t } = useTranslation("workspace");
  const qc = useQueryClient();
  const { data: sections = [] } = useQuery({ queryKey: ["sections"], queryFn: api.listSections });
  const [newName, setNewName] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  async function move(sectionId: string | null) {
    setBusy(true);
    setError(null);
    try {
      await api.moveRecording(recordingId, sectionId);
      qc.invalidateQueries({ queryKey: ["recordings"] });
      onClose();
    } catch (e) {
      setError(apiErrorMessage(e));
      setBusy(false);
    }
  }

  async function createAndMove() {
    const name = newName.trim();
    if (!name) return;
    setBusy(true);
    setError(null);
    try {
      const section = await api.createSection(name);
      await api.moveRecording(recordingId, section.id);
      qc.invalidateQueries({ queryKey: ["recordings"] });
      qc.invalidateQueries({ queryKey: ["sections"] });
      onClose();
    } catch (e) {
      setError(apiErrorMessage(e));
      setBusy(false);
    }
  }

  const itemClass = (active: boolean) =>
    `block w-full rounded px-3 py-1.5 text-left text-sm hover:bg-gray-50 disabled:opacity-50 dark:text-gray-200 dark:hover:bg-gray-800 ${
      active ? "font-medium" : ""
    }`;

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" onClick={onClose}>
      <div
        role="dialog"
        aria-label={t("moveToSectionTitle")}
        className="w-full max-w-sm rounded-lg border bg-white p-4 shadow-xl dark:border-gray-700 dark:bg-gray-900"
        onClick={(e) => e.stopPropagation()}
      >
        <h2 className="mb-2 text-base font-semibold dark:text-gray-100">{t("moveToSectionTitle")}</h2>

        <div className="max-h-64 overflow-y-auto">
          <button
            type="button"
            disabled={busy || currentSectionId === null}
            onClick={() => move(null)}
            className={itemClass(currentSectionId === null)}
          >
            {t("ungrouped")} {currentSectionId === null && <span aria-hidden>✓</span>}
          </button>
          {sections.map((s) => (
            <button
              key={s.id}
              type="button"
              disabled={busy || currentSectionId === s.id}
              onClick={() => move(s.id)}
              className={itemClass(currentSectionId === s.id)}
            >
              {s.name} {currentSectionId === s.id && <span aria-hidden>✓</span>}
            </button>
          ))}
        </div>

        <form
          className="mt-3 flex items-center gap-2 border-t pt-3 dark:border-gray-700"
          onSubmit={(e) => {
            e.preventDefault();
            createAndMove();
          }}
        >
          <input
            value={newName}
            onChange={(e) => setNewName(e.target.value)}
            placeholder={t("newSectionPlaceholder")}
            aria-label={t("newSectionPlaceholder")}
            className="min-w-0 flex-1 rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
          />
          <button
            type="submit"
            disabled={busy || !newName.trim()}
            className="shrink-0 rounded border px-2 py-1 text-sm hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("createAndMove")}
          </button>
        </form>

        {error && <p className="mt-2 text-sm text-red-600 dark:text-red-400">{error}</p>}
      </div>
    </div>
  );
}
