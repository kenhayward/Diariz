import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { NavLink } from "react-router-dom";
import { useRoomBasePath } from "../lib/rooms";
import type { SectionNoteItem } from "../lib/types";

/// The folder Notes tab: every note across the folder's recordings (and sub-folders), with a read-only
/// **Meeting** column. Edit + delete reuse the per-note endpoints (owner-only - see `MeetingNotesController`),
/// so they're only offered on a row whose source recording belongs to `myUserId`; a room co-viewer's rows
/// (from someone else's recording) show as plain read-only text instead. Adding is not offered.
export default function FolderNotesList({
  items,
  myUserId,
  onEdit,
  onDelete,
}: {
  items: SectionNoteItem[];
  /// The signed-in user's id (`useAuth().id`), compared against each row's `recordedByUserId` - mirrors
  /// RecordingDetail's `isOwner` check, just applied per row since rows here span many recordings.
  myUserId: string | null;
  onEdit: (recordingId: string, id: string, text: string) => void;
  onDelete: (recordingId: string, id: string) => void;
}) {
  const { t } = useTranslation("workspace");
  // Keep meeting links inside the room being viewed - see FolderRecordingList for the room-prefix rationale.
  const basePath = useRoomBasePath();
  if (items.length === 0)
    return <p className="px-4 pb-4 text-sm text-gray-500 dark:text-gray-400">{t("folderNoNotes")}</p>;

  return (
    <div className="px-4 pb-4">
      <table className="w-full table-fixed text-sm">
        <thead>
          <tr className="text-left text-xs font-medium text-gray-400 dark:text-gray-500">
            <th className="w-[22%] pb-1 pr-2 font-medium">{t("colMeeting")}</th>
            <th className="w-[73%] pb-1 pr-2 font-medium">{t("colNote")}</th>
            <th className="w-[5%] pb-1" aria-hidden />
          </tr>
        </thead>
        <tbody>
          {items.map((n, i) => (
            <Row
              key={n.id}
              note={n}
              row={i + 1}
              basePath={basePath}
              isOwner={myUserId != null && n.recordedByUserId === myUserId}
              onEdit={onEdit}
              onDelete={onDelete}
            />
          ))}
        </tbody>
      </table>
    </div>
  );
}

function Row({
  note, row, basePath, isOwner, onEdit, onDelete,
}: {
  note: SectionNoteItem;
  row: number;
  basePath: string;
  isOwner: boolean;
  onEdit: (recordingId: string, id: string, text: string) => void;
  onDelete: (recordingId: string, id: string) => void;
}) {
  const { t } = useTranslation("workspace");
  const [draft, setDraft] = useState(note.text);
  useEffect(() => setDraft(note.text), [note.text]);
  return (
    <tr className="align-top">
      <td className="truncate py-1 pr-2 text-xs">
        <NavLink to={`${basePath}/recordings/${note.recordingId}`} className="text-blue-600 hover:underline dark:text-blue-400" title={note.recordingName}>
          {note.recordingName}
        </NavLink>
      </td>
      <td className="py-1 pr-2">
        {isOwner ? (
          <input
            value={draft}
            aria-label={t("noteCellAria", { row })}
            onChange={(e) => setDraft(e.target.value)}
            onBlur={() => { const v = draft.trim(); if (v && v !== note.text) onEdit(note.recordingId, note.id, v); }}
            className="w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
          />
        ) : (
          <span className="block w-full px-2 py-1 text-sm dark:text-gray-200">{note.text}</span>
        )}
      </td>
      <td className="py-1 text-right">
        {isOwner && (
          <button
            type="button"
            aria-label={t("removeNoteAria", { row })}
            title={t("common:remove")}
            onClick={() => onDelete(note.recordingId, note.id)}
            className="rounded px-1 text-gray-400 hover:bg-gray-100 hover:text-red-600 dark:hover:bg-gray-800 dark:hover:text-red-400"
          >
            ✕
          </button>
        )}
      </td>
    </tr>
  );
}
