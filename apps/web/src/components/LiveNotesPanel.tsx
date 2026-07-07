import { useTranslation } from "react-i18next";
import NotesSection from "./NotesSection";
import type { MeetingNote } from "../lib/types";

/// Floating notes panel shown while recording. Lines are local (client-side) until the upload attaches
/// them; the Recorder owns state, stamping, durability, and attach. Renders below the TopBar on the right.
export default function LiveNotesPanel({
  lines,
  onAdd,
  onEdit,
  onDelete,
  onClose,
}: {
  lines: MeetingNote[];
  onAdd: (text: string) => void;
  onEdit: (id: string, text: string) => void;
  onDelete: (id: string) => void;
  onClose: () => void;
}) {
  const { t } = useTranslation("workspace");
  return (
    <div className="fixed right-4 top-14 z-40 w-80 rounded-lg border bg-white p-3 shadow-xl dark:border-gray-700 dark:bg-gray-900">
      <div className="mb-1 flex items-center justify-between">
        <span className="text-sm font-medium text-gray-700 dark:text-gray-200">{t("liveNotesTitle")}</span>
        <button
          type="button"
          aria-label={t("liveNotesClose")}
          onClick={onClose}
          className="rounded px-1 text-gray-400 hover:text-gray-600 dark:hover:text-gray-200"
        >
          ✕
        </button>
      </div>
      <p className="mb-2 text-xs text-gray-400 dark:text-gray-500">{t("liveNotesHint")}</p>
      <div className="max-h-72 overflow-y-auto">
        <NotesSection notes={lines} onAdd={onAdd} onEdit={onEdit} onDelete={onDelete} />
      </div>
    </div>
  );
}
