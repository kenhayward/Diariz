import { useState } from "react";
import { useTranslation } from "react-i18next";
import type { MeetingNote } from "../lib/types";

const fmt = (ms: number) => {
  const s = Math.floor(ms / 1000);
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
};

/// A dumb list-editor for the user's own note lines: optional mm:ss stamp badge (click -> onJump), inline
/// edit, delete, and an Enter-to-add box. Persistence is the parent's job (recording- or event-backed).
export default function NotesSection({
  notes,
  onAdd,
  onEdit,
  onDelete,
  onJump,
}: {
  notes: MeetingNote[];
  onAdd: (text: string) => void;
  onEdit: (id: string, text: string) => void;
  onDelete: (id: string) => void;
  onJump?: (ms: number) => void;
}) {
  const { t } = useTranslation("workspace");
  const [draft, setDraft] = useState("");
  const [editing, setEditing] = useState<string | null>(null);
  const [editText, setEditText] = useState("");

  function add() {
    const text = draft.trim();
    if (!text) return;
    onAdd(text);
    setDraft("");
  }

  const btn =
    "rounded border px-1.5 py-0.5 text-xs hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800";

  return (
    <div className="space-y-2">
      <div className="flex gap-2">
        <input
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter") {
              e.preventDefault();
              add();
            }
          }}
          placeholder={t("notesAddPlaceholder")}
          aria-label={t("notesAddPlaceholder")}
          className="min-w-0 flex-1 rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
        />
        <button type="button" onClick={add} className={btn}>
          {t("notesAdd")}
        </button>
      </div>

      <ul className="space-y-1">
        {notes.map((n) => (
          <li key={n.id} className="flex items-start gap-2 text-sm dark:text-gray-200">
            {n.capturedAtMs != null && onJump ? (
              <button
                type="button"
                onClick={() => onJump(n.capturedAtMs!)}
                aria-label={t("notesJump", { time: fmt(n.capturedAtMs) })}
                className="shrink-0 rounded bg-gray-100 px-1.5 py-0.5 font-mono text-[11px] text-gray-600 hover:bg-gray-200 dark:bg-gray-800 dark:text-gray-300 dark:hover:bg-gray-700"
              >
                {fmt(n.capturedAtMs)}
              </button>
            ) : n.capturedAtMs != null ? (
              <span className="shrink-0 rounded bg-gray-100 px-1.5 py-0.5 font-mono text-[11px] text-gray-500 dark:bg-gray-800 dark:text-gray-400">
                {fmt(n.capturedAtMs)}
              </span>
            ) : null}
            {editing === n.id ? (
              <span className="flex min-w-0 flex-1 items-center gap-1">
                <input
                  value={editText}
                  onChange={(e) => setEditText(e.target.value)}
                  aria-label={t("notesEdit")}
                  className="min-w-0 flex-1 rounded border px-1.5 py-0.5 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
                />
                <button
                  type="button"
                  className={btn}
                  onClick={() => {
                    onEdit(n.id, editText.trim());
                    setEditing(null);
                  }}
                >
                  {t("notesSave")}
                </button>
                <button type="button" className={btn} onClick={() => setEditing(null)}>
                  {t("notesCancel")}
                </button>
              </span>
            ) : (
              <>
                <span className="min-w-0 flex-1 break-words">{n.text}</span>
                <button
                  type="button"
                  aria-label={t("notesEdit")}
                  className={btn}
                  onClick={() => {
                    setEditing(n.id);
                    setEditText(n.text);
                  }}
                >
                  ✎
                </button>
                <button
                  type="button"
                  aria-label={t("notesDelete")}
                  className="shrink-0 text-red-600 hover:underline dark:text-red-400"
                  onClick={() => onDelete(n.id)}
                >
                  ✕
                </button>
              </>
            )}
          </li>
        ))}
        {notes.length === 0 && <li className="text-xs text-gray-400 dark:text-gray-500">{t("notesEmpty")}</li>}
      </ul>
    </div>
  );
}
