import { useTranslation } from "react-i18next";
import { formatRelativeTime } from "../lib/format";
import { initialsFromName } from "../lib/initials";
import type { FormulaResult, FormulaResultOrigin } from "../lib/types";
import Avatar from "./Avatar";
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
          {r.status === "Generating" ? (
            // In-flight run: a muted, non-interactive row (nothing to view yet) with a spinner.
            <div className="flex w-full items-center gap-2 rounded px-2 py-2 text-left opacity-70">
              <Spinner />
              <span className="min-w-0 flex-1">
                <span className="block truncate text-sm font-medium text-gray-800 dark:text-gray-100">{r.name}</span>
                <span className="block truncate text-xs text-gray-400 dark:text-gray-500">{t("formulaGenerating")}</span>
              </span>
            </div>
          ) : r.status === "Failed" ? (
            // Failed run: an error-tinted, non-interactive row exposing the reason (inline + as a tooltip).
            <div
              className="flex w-full items-center gap-2 rounded bg-red-50 px-2 py-2 text-left dark:bg-red-900/20"
              title={r.error ?? undefined}
            >
              <OriginIcon origin={r.origin} />
              <span className="min-w-0 flex-1">
                <span className="block truncate text-sm font-medium text-gray-800 dark:text-gray-100">{r.name}</span>
                <span className="block truncate text-xs text-red-600 dark:text-red-400">
                  {r.error ? `${t("formulaFailed")}: ${r.error}` : t("formulaFailed")}
                </span>
              </span>
            </div>
          ) : (
            <button
              type="button"
              onClick={() => onSelect(selectedId === r.id ? null : r.id)}
              aria-pressed={selectedId === r.id}
              className={`flex w-full items-center gap-2 rounded px-2 py-2 text-left ${
                selectedId === r.id ? "bg-blue-50 dark:bg-blue-900/30" : "hover:bg-gray-50 dark:hover:bg-gray-800"
              }`}
            >
              <OriginIcon origin={r.origin} />
              <span className="min-w-0 flex-1">
                <span className="block truncate text-sm font-medium text-gray-800 dark:text-gray-100">{r.name}</span>
                <span className="block truncate text-xs text-gray-400 dark:text-gray-500">
                  {t("formulaGeneratedMeta", {
                    time: formatRelativeTime(r.createdAt, i18n.language),
                    name: r.name,
                  })}
                </span>
              </span>
            </button>
          )}
        </li>
      ))}
    </ul>
  );
}

/// A small spinning ring shown while a formula result is still generating.
function Spinner() {
  return (
    <span
      className="h-6 w-6 shrink-0 animate-spin rounded-full border-2 border-gray-300 border-t-blue-500 dark:border-gray-600 dark:border-t-blue-400"
      aria-hidden="true"
    />
  );
}

/// Diariz + Platform formulas are "official" -> the Diariz logo; personal/shared -> the person's avatar.
function OriginIcon({ origin }: { origin: FormulaResultOrigin }) {
  if (origin.kind === "diariz" || origin.kind === "platform") {
    return <img src="/logo.png" alt="" className="h-6 w-6 shrink-0 rounded-full object-cover" />;
  }
  return <Avatar size="xs" initials={initialsFromName(origin.personName)} pictureUrl={origin.personPictureUrl} />;
}
