import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { NavLink } from "react-router-dom";
import type { ActionListItem } from "../lib/types";

type Patch = { text?: string; actor?: string; deadline?: string };

/// The folder Actions tab: every action across the folder's recordings (and sub-folders), with a read-only
/// **Meeting** column linking back to the source recording. Edit + delete are allowed (reusing the per-item
/// endpoints); adding is not (actions belong to a recording, not a folder).
export default function FolderActionsTable({
  items,
  onUpdate,
  onToggleComplete,
  onDelete,
}: {
  items: ActionListItem[];
  onUpdate: (recordingId: string, id: string, patch: Patch) => void;
  onToggleComplete: (id: string, completed: boolean) => void;
  onDelete: (recordingId: string, id: string) => void;
}) {
  const { t } = useTranslation("workspace");
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
            <Row key={a.id} action={a} row={i + 1} onUpdate={onUpdate} onToggleComplete={onToggleComplete} onDelete={onDelete} />
          ))}
        </tbody>
      </table>
    </div>
  );
}

function Row({
  action, row, onUpdate, onToggleComplete, onDelete,
}: {
  action: ActionListItem;
  row: number;
  onUpdate: (recordingId: string, id: string, patch: Patch) => void;
  onToggleComplete: (id: string, completed: boolean) => void;
  onDelete: (recordingId: string, id: string) => void;
}) {
  const { t, i18n } = useTranslation("workspace");
  const completedDate = action.completedAt ? new Date(action.completedAt).toLocaleDateString(i18n.language) : "";
  return (
    <tr className="align-top">
      <td className="truncate py-1 pr-2 text-xs">
        <NavLink to={`/recordings/${action.recordingId}`} className="text-blue-600 hover:underline dark:text-blue-400" title={action.recordingName}>
          {action.recordingName}
        </NavLink>
      </td>
      <td className="py-1 pr-1 text-center">
        <input
          type="checkbox"
          checked={action.completed}
          onChange={() => onToggleComplete(action.id, !action.completed)}
          aria-label={t("markCompleteAria", { row })}
          className="mt-1.5"
        />
      </td>
      <td className="py-1 pr-2">
        <Cell label={t("actionCellAria", { row })} value={action.text} strike={action.completed} commit={(v) => onUpdate(action.recordingId, action.id, { text: v })} />
      </td>
      <td className="py-1 pr-2">
        <Cell label={t("actorCellAria", { row })} value={action.actor} commit={(v) => onUpdate(action.recordingId, action.id, { actor: v })} />
      </td>
      <td className="py-1 pr-2">
        <Cell label={t("deadlineCellAria", { row })} value={action.deadline} commit={(v) => onUpdate(action.recordingId, action.id, { deadline: v })} />
      </td>
      <td className="py-1 pr-2 text-xs text-gray-500 dark:text-gray-400">{completedDate}</td>
      <td className="py-1 text-right">
        <button
          type="button"
          aria-label={t("removeActionAria", { row })}
          title={t("common:remove")}
          onClick={() => onDelete(action.recordingId, action.id)}
          className="rounded px-1 text-gray-400 hover:bg-gray-100 hover:text-red-600 dark:hover:bg-gray-800 dark:hover:text-red-400"
        >
          ✕
        </button>
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
