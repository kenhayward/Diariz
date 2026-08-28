import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import type { RecordingAction } from "../lib/types";

type Patch = { text?: string; actor?: string; deadline?: string };

/// The extracted action items as an inline-editable table (Done / Action / Actor / Deadline / Completed —
/// all free text bar the Done toggle), with a "+ Add action" button below. Chrome-less: it renders just the
/// body, so the recording detail page can host it inside the Actions tab (the Extract button lives in that
/// tab's toolbar). The parent owns persistence: each edit commits on blur via `onUpdate`, the Done checkbox
/// toggles via `onToggleComplete`, and add/remove go through `onAdd`/`onDelete`.
export default function ActionsTable({
  actions,
  onAdd,
  onUpdate,
  onToggleComplete,
  onTogglePin,
  onDelete,
}: {
  actions: RecordingAction[];
  onAdd: () => void;
  onUpdate: (id: string, patch: Patch) => void;
  onToggleComplete: (id: string, completed: boolean) => void;
  /// Promote an action into the cross-meeting Actions views, or take it back out. This table always shows
  /// every action regardless - pinning only governs where else it appears.
  onTogglePin: (id: string, pinned: boolean) => void;
  onDelete: (id: string) => void;
}) {
  const { t } = useTranslation("workspace");
  return (
    <div className="px-4 pb-4">
      {actions.length === 0 ? (
        <p className="mb-3 text-sm text-gray-500 dark:text-gray-400">{t("noActions")}</p>
      ) : (
        <table className="mb-3 w-full table-fixed text-sm">
          <thead>
            <tr className="text-left text-xs font-medium text-gray-400 dark:text-gray-500">
              <th className="w-[5%] pb-1 pr-1 font-medium text-center">{t("colPin")}</th>
              <th className="w-[6%] pb-1 pr-1 font-medium text-center">{t("colDone")}</th>
              <th className="w-[36%] pb-1 pr-2 font-medium">{t("colAction")}</th>
              <th className="w-[15%] pb-1 pr-2 font-medium">{t("colActor")}</th>
              <th className="w-[18%] pb-1 pr-2 font-medium">{t("colDeadline")}</th>
              <th className="w-[15%] pb-1 pr-2 font-medium">{t("colCompletedDate")}</th>
              <th className="w-[5%] pb-1" aria-hidden />
            </tr>
          </thead>
          <tbody>
            {actions.map((a, i) => (
              <ActionRow
                key={a.id}
                action={a}
                row={i + 1}
                onUpdate={onUpdate}
                onToggleComplete={onToggleComplete}
                onTogglePin={onTogglePin}
                onDelete={onDelete}
              />
            ))}
          </tbody>
        </table>
      )}

      <button
        type="button"
        onClick={onAdd}
        className="rounded border px-3 py-1.5 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
      >
        {t("addAction")}
      </button>
    </div>
  );
}

function ActionRow({
  action,
  row,
  onUpdate,
  onToggleComplete,
  onTogglePin,
  onDelete,
}: {
  action: RecordingAction;
  row: number;
  onUpdate: (id: string, patch: Patch) => void;
  onToggleComplete: (id: string, completed: boolean) => void;
  onTogglePin: (id: string, pinned: boolean) => void;
  onDelete: (id: string) => void;
}) {
  const { t, i18n } = useTranslation("workspace");
  const completedDate = action.completedAt
    ? new Date(action.completedAt).toLocaleDateString(i18n.language)
    : "";
  return (
    <tr className="align-top">
      {/* Pin leads the row: this is where the user decides what to track, so it is the first thing on it. */}
      <td className="py-1 pr-1 text-center">
        <button
          type="button"
          aria-label={action.pinned ? t("unpinActionAria", { row }) : t("pinActionAria", { row })}
          aria-pressed={action.pinned}
          onClick={() => onTogglePin(action.id, !action.pinned)}
          className={`mt-1 rounded px-1 ${
            action.pinned
              ? "text-blue-600 dark:text-blue-400"
              : "text-gray-300 hover:text-gray-500 dark:text-gray-600 dark:hover:text-gray-400"
          }`}
        >
          <PinIcon filled={action.pinned} />
        </button>
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
        <Cell
          label={t("actionCellAria", { row })}
          value={action.text}
          strike={action.completed}
          commit={(v) => onUpdate(action.id, { text: v })}
        />
      </td>
      <td className="py-1 pr-2">
        <Cell label={t("actorCellAria", { row })} value={action.actor} commit={(v) => onUpdate(action.id, { actor: v })} />
      </td>
      <td className="py-1 pr-2">
        <Cell label={t("deadlineCellAria", { row })} value={action.deadline} commit={(v) => onUpdate(action.id, { deadline: v })} />
      </td>
      <td className="py-1 pr-2 text-xs text-gray-500 dark:text-gray-400">{completedDate}</td>
      <td className="py-1 text-right">
        <button
          type="button"
          aria-label={t("removeActionAria", { row })}
          title={t("common:remove")}
          onClick={() => onDelete(action.id)}
          className="rounded px-1 text-gray-400 hover:bg-gray-100 hover:text-red-600 dark:hover:bg-gray-800 dark:hover:text-red-400"
        >
          ✕
        </button>
      </td>
    </tr>
  );
}

/// A pin glyph, solid when pinned and outline when not - so the two states differ by shape as well as
/// colour, which colour alone would not carry.
function PinIcon({ filled }: { filled: boolean }) {
  return (
    <svg
      width="14"
      height="14"
      viewBox="0 0 24 24"
      fill={filled ? "currentColor" : "none"}
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden
    >
      <path d="M12 17v5" />
      <path d="M9 10.76a2 2 0 0 1-1.11 1.79l-1.78.9A2 2 0 0 0 5 15.24V16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-.76a2 2 0 0 0-1.11-1.79l-1.78-.9A2 2 0 0 1 15 10.76V7a1 1 0 0 1 1-1 2 2 0 0 0 0-4H8a2 2 0 0 0 0 4 1 1 0 0 1 1 1Z" />
    </svg>
  );
}

/// A single editable cell: an input seeded from props that commits on blur only when changed.
function Cell({
  label,
  value,
  commit,
  strike = false,
}: {
  label: string;
  value: string;
  commit: (v: string) => void;
  strike?: boolean;
}) {
  const [draft, setDraft] = useState(value);
  // Keep the field in sync if the value changes underneath us (e.g. a re-extract).
  useEffect(() => setDraft(value), [value]);

  return (
    <input
      value={draft}
      aria-label={label}
      onChange={(e) => setDraft(e.target.value)}
      onBlur={() => {
        if (draft !== value) commit(draft.trim());
      }}
      className={`w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100 ${
        strike ? "text-gray-400 line-through dark:text-gray-500" : ""
      }`}
    />
  );
}
