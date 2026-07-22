import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { NavLink } from "react-router-dom";
import { useRoomBasePath } from "../lib/rooms";
import type { ActionListItem } from "../lib/types";

type Patch = { text?: string; actor?: string; deadline?: string };

/// The folder Actions tab: every action across the folder's recordings (and sub-folders), with a read-only
/// **Meeting** column linking back to the source recording. Edit/complete/delete reuse the per-item endpoints
/// (owner-only - see `RecordingActionsController`/`ActionsController.Complete`), so they're only offered on a
/// row whose source recording belongs to `myUserId`; a room co-viewer's rows (from someone else's recording)
/// show as plain read-only text and a disabled checkbox. Adding is not offered (actions belong to a recording,
/// not a folder).
export default function FolderActionsTable({
  items,
  myUserId,
  onUpdate,
  onToggleComplete,
  onDelete,
}: {
  items: ActionListItem[];
  /// The signed-in user's id (`useAuth().id`), compared against each row's `recordedByUserId` - mirrors
  /// RecordingDetail's `isOwner` check, just applied per row since rows here span many recordings.
  myUserId: string | null;
  onUpdate: (recordingId: string, id: string, patch: Patch) => void;
  onToggleComplete: (id: string, completed: boolean) => void;
  onDelete: (recordingId: string, id: string) => void;
}) {
  const { t } = useTranslation("workspace");
  // Keep meeting links inside the room being viewed - see FolderRecordingList for the room-prefix rationale.
  const basePath = useRoomBasePath();
  if (items.length === 0)
    return <p className="px-4 pb-4 text-sm text-gray-500 dark:text-gray-400">{t("folderNoActions")}</p>;

  return (
    <div className="px-4 pb-4">
      <table className="w-full table-fixed text-sm">
        <thead>
          <tr className="text-left text-xs font-medium text-gray-400 dark:text-gray-500">
            <th className="w-[18%] pb-1 pr-2 font-medium">{t("colMeeting")}</th>
            <th className="w-[6%] pb-1 pr-1 text-center font-medium">{t("colDone")}</th>
            <th className="w-[34%] pb-1 pr-2 font-medium">{t("colAction")}</th>
            <th className="w-[14%] pb-1 pr-2 font-medium">{t("colActor")}</th>
            <th className="w-[16%] pb-1 pr-2 font-medium">{t("colDeadline")}</th>
            <th className="w-[7%] pb-1 pr-2 font-medium">{t("colCompletedDate")}</th>
            <th className="w-[5%] pb-1" aria-hidden />
          </tr>
        </thead>
        <tbody>
          {items.map((a, i) => (
            <Row
              key={a.id}
              action={a}
              row={i + 1}
              basePath={basePath}
              isOwner={myUserId != null && a.recordedByUserId === myUserId}
              onUpdate={onUpdate}
              onToggleComplete={onToggleComplete}
              onDelete={onDelete}
            />
          ))}
        </tbody>
      </table>
    </div>
  );
}

function Row({
  action, row, basePath, isOwner, onUpdate, onToggleComplete, onDelete,
}: {
  action: ActionListItem;
  row: number;
  basePath: string;
  isOwner: boolean;
  onUpdate: (recordingId: string, id: string, patch: Patch) => void;
  onToggleComplete: (id: string, completed: boolean) => void;
  onDelete: (recordingId: string, id: string) => void;
}) {
  const { t, i18n } = useTranslation("workspace");
  const completedDate = action.completedAt ? new Date(action.completedAt).toLocaleDateString(i18n.language) : "";
  return (
    <tr className="align-top">
      <td className="truncate py-1 pr-2 text-xs">
        <NavLink to={`${basePath}/recordings/${action.recordingId}`} className="text-blue-600 hover:underline dark:text-blue-400" title={action.recordingName}>
          {action.recordingName}
        </NavLink>
      </td>
      <td className="py-1 pr-1 text-center">
        <input
          type="checkbox"
          checked={action.completed}
          disabled={!isOwner}
          onChange={() => isOwner && onToggleComplete(action.id, !action.completed)}
          aria-label={t("markCompleteAria", { row })}
          className="mt-1.5"
        />
      </td>
      <td className="py-1 pr-2">
        {isOwner ? (
          <Cell label={t("actionCellAria", { row })} value={action.text} strike={action.completed} commit={(v) => onUpdate(action.recordingId, action.id, { text: v })} />
        ) : (
          <span className={`block w-full px-2 py-1 text-sm dark:text-gray-200 ${action.completed ? "text-gray-400 line-through dark:text-gray-500" : ""}`}>{action.text}</span>
        )}
      </td>
      <td className="py-1 pr-2">
        {isOwner ? (
          <Cell label={t("actorCellAria", { row })} value={action.actor} commit={(v) => onUpdate(action.recordingId, action.id, { actor: v })} />
        ) : (
          <span className="block w-full px-2 py-1 text-sm dark:text-gray-200">{action.actor}</span>
        )}
      </td>
      <td className="py-1 pr-2">
        {isOwner ? (
          <Cell label={t("deadlineCellAria", { row })} value={action.deadline} commit={(v) => onUpdate(action.recordingId, action.id, { deadline: v })} />
        ) : (
          <span className="block w-full px-2 py-1 text-sm dark:text-gray-200">{action.deadline}</span>
        )}
      </td>
      <td className="py-1 pr-2 text-xs text-gray-500 dark:text-gray-400">{completedDate}</td>
      <td className="py-1 text-right">
        {isOwner && (
          <button
            type="button"
            aria-label={t("removeActionAria", { row })}
            title={t("common:remove")}
            onClick={() => onDelete(action.recordingId, action.id)}
            className="rounded px-1 text-gray-400 hover:bg-gray-100 hover:text-red-600 dark:hover:bg-gray-800 dark:hover:text-red-400"
          >
            ✕
          </button>
        )}
      </td>
    </tr>
  );
}

/// An editable cell seeded from props that commits on blur only when changed.
function Cell({ label, value, commit, strike = false }: { label: string; value: string; commit: (v: string) => void; strike?: boolean }) {
  const [draft, setDraft] = useState(value);
  useEffect(() => setDraft(value), [value]);
  return (
    <input
      value={draft}
      aria-label={label}
      onChange={(e) => setDraft(e.target.value)}
      onBlur={() => { if (draft !== value) commit(draft.trim()); }}
      className={`w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100 ${
        strike ? "text-gray-400 line-through dark:text-gray-500" : ""
      }`}
    />
  );
}
