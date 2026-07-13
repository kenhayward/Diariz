import { useTranslation } from "react-i18next";
import { Link } from "react-router-dom";
import { useSelection } from "../lib/selection";
import { useRoomBasePath } from "../lib/rooms";
import type { ActionListItem } from "../lib/types";

/// The cross-transcript Actions list (left panel, "Actions" tab). Rows are compact (the panel is narrow):
/// the action title links to its source transcript; actor/deadline/completion sit below. Clicking a row
/// selects it (single when not in Select mode, toggle when in it) so the toolbar can act on the selection.
export default function ActionsTab({
  actions,
  persons,
  person,
  onPerson,
}: {
  /// Already filtered (person + hide-complete applied upstream).
  actions: ActionListItem[];
  persons: string[];
  person: string | null;
  onPerson: (p: string | null) => void;
}) {
  const { t, i18n } = useTranslation("workspace");
  const { selectMode, selectedIds, toggle, set } = useSelection();
  // Keep links inside the room being viewed - a shared room's detail routes carry the /rooms/:id prefix, so
  // without it a click falls back to the personal-room URL and switches the user out of the room.
  const basePath = useRoomBasePath();

  return (
    <div>
      {/* Filter by person (the free-text actor). */}
      <div className="flex items-center gap-2 border-b px-3 py-1.5 dark:border-gray-800">
        <label className="text-xs text-gray-500 dark:text-gray-400">{t("filterByPerson")}</label>
        <select
          value={person ?? ""}
          onChange={(e) => onPerson(e.target.value || null)}
          aria-label={t("filterByPerson")}
          className="min-w-0 flex-1 rounded border px-1 py-0.5 text-xs dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
        >
          <option value="">{t("allPeople")}</option>
          {persons.map((p) => (
            <option key={p} value={p}>{p}</option>
          ))}
        </select>
      </div>

      {actions.length === 0 ? (
        <p className="p-4 text-sm text-gray-500 dark:text-gray-400">{t("noActionsAll")}</p>
      ) : (
        <ul className="divide-y dark:divide-gray-800">
          {actions.map((a) => {
            const isSel = selectedIds.includes(a.id);
            const completedDate = a.completedAt
              ? new Date(a.completedAt).toLocaleDateString(i18n.language)
              : null;
            return (
              <li
                key={a.id}
                onClick={() => (selectMode ? toggle(a.id) : set([a.id]))}
                className={`cursor-pointer px-3 py-2 ${
                  isSel ? "bg-blue-50 dark:bg-blue-900/30" : "hover:bg-gray-50 dark:hover:bg-gray-800/60"
                }`}
              >
                <div className="flex items-start gap-2">
                  {selectMode && (
                    <input
                      type="checkbox"
                      checked={isSel}
                      onChange={() => toggle(a.id)}
                      onClick={(e) => e.stopPropagation()}
                      aria-label={t("selectActionAria", { text: a.text })}
                      className="mt-0.5 shrink-0"
                    />
                  )}
                  <div className="min-w-0 flex-1">
                    {/* Title links to the source transcript; stop propagation so it navigates, not selects. */}
                    <Link
                      to={`${basePath}/recordings/${a.recordingId}`}
                      onClick={(e) => e.stopPropagation()}
                      className={`block truncate text-sm font-medium hover:underline ${
                        a.completed
                          ? "text-gray-400 line-through dark:text-gray-500"
                          : "text-blue-700 dark:text-blue-300"
                      }`}
                      title={a.text}
                    >
                      {a.text || t("untitledAction")}
                    </Link>
                    <div className="mt-0.5 flex flex-wrap items-center gap-x-2 text-xs text-gray-500 dark:text-gray-400">
                      <span className="truncate">{a.recordingName}</span>
                      {a.actor && <span className="truncate">· {a.actor}</span>}
                      {a.deadline && <span className="truncate">· {a.deadline}</span>}
                      {a.completed && (
                        <span className="text-green-600 dark:text-green-400">
                          ✓ {completedDate ? t("completedOn", { date: completedDate }) : t("completedLabel")}
                        </span>
                      )}
                    </div>
                  </div>
                </div>
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}
