import { useTranslation } from "react-i18next";
import { ChevronDownIcon } from "../icons";
import type { ListSort, SortKey } from "../../lib/listSort";

const KEYS: SortKey[] = ["manual", "date", "name", "duration"];

/// How the List tab's recordings are ordered: the key, plus a direction toggle beside it.
///
/// A native `<select>` rather than a custom popover - it is the narrowest control that holds four labels in
/// a panel this width, and it is keyboard- and screen-reader-correct without any work of its own.
///
/// The direction toggle is absent under Manual: there is no direction for the order you arranged by hand.
export default function SortMenu({
  sort,
  onChange,
}: {
  sort: ListSort;
  onChange: (next: ListSort) => void;
}) {
  const { t } = useTranslation("workspace");
  const label: Record<SortKey, string> = {
    manual: t("sortManual"),
    date: t("sortDate"),
    name: t("sortName"),
    duration: t("sortDuration"),
  };
  const ascending = sort.dir === "asc";

  return (
    <div className="flex shrink-0 items-center gap-1">
      <select
        aria-label={t("sortBy")}
        title={t("sortBy")}
        value={sort.key}
        onChange={(e) => onChange({ key: e.target.value as SortKey, dir: sort.dir })}
        className="rounded border bg-gray-50 px-1 py-0.5 text-[11px] text-gray-600 outline-none focus:ring-2 focus:ring-blue-500 dark:border-gray-700 dark:bg-gray-800 dark:text-gray-300"
      >
        {KEYS.map((key) => (
          <option key={key} value={key}>
            {label[key]}
          </option>
        ))}
      </select>
      {sort.key !== "manual" && (
        <button
          type="button"
          aria-label={ascending ? t("sortAscending") : t("sortDescending")}
          title={ascending ? t("sortAscending") : t("sortDescending")}
          onClick={() => onChange({ key: sort.key, dir: ascending ? "desc" : "asc" })}
          className="rounded border px-1 py-0.5 text-gray-500 hover:bg-gray-100 dark:border-gray-700 dark:text-gray-400 dark:hover:bg-gray-800"
        >
          {/* One glyph rotated, rather than two icons: the pair can never drift apart, and the app has no
              up-chevron of its own. The span carries the rotation because the icons take no className. */}
          <span className={`block ${ascending ? "rotate-180" : ""}`}>
            <ChevronDownIcon size={12} />
          </span>
        </button>
      )}
    </div>
  );
}
