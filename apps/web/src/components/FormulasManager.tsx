import { useTranslation } from "react-i18next";
import { formatRelativeTime } from "../lib/format";
import type { FormulaResult } from "../lib/types";
import FlaskIcon from "./FlaskIcon";

/// The Formulas tab's content: the "Generated" results list (name + a muted "Generated {{time}} from the
/// ... formula" meta line), single-select on click. Chrome-less, like AttachmentsManager - hosts the tab's
/// body while FormulasToolbar (a sibling passed to the tab's `toolbar` slot) hosts the actions. Selection
/// is lifted to RecordingDetail (the toolbar needs to read it too) rather than owned locally.
export default function FormulasManager({
  results,
  selectedId,
  onSelect,
}: {
  results: FormulaResult[];
  selectedId: string | null;
  onSelect: (id: string | null) => void;
}) {
  const { t, i18n } = useTranslation("workspace");

  if (results.length === 0) {
    return (
      <div className="flex flex-col items-center gap-2 px-4 py-10 text-center text-sm text-gray-500 dark:text-gray-400">
        <FlaskIcon />
        <p>{t("formulasEmpty")}</p>
      </div>
    );
  }

  return (
    <ul className="divide-y px-4 pb-4 dark:divide-gray-700">
      {results.map((r) => (
        <li key={r.id}>
          <button
            type="button"
            onClick={() => onSelect(selectedId === r.id ? null : r.id)}
            aria-pressed={selectedId === r.id}
            className={`block w-full rounded px-2 py-2 text-left ${
              selectedId === r.id ? "bg-blue-50 dark:bg-blue-900/30" : "hover:bg-gray-50 dark:hover:bg-gray-800"
            }`}
          >
            <span className="block text-sm font-medium text-gray-800 dark:text-gray-100">{r.name}</span>
            <span className="block text-xs text-gray-400 dark:text-gray-500">
              {t("formulaGeneratedMeta", {
                time: formatRelativeTime(r.createdAt, i18n.language),
                name: r.name,
              })}
            </span>
          </button>
        </li>
      ))}
    </ul>
  );
}
