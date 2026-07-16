import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { ChevronDownIcon } from "../icons";
import type { SearchFacets, SearchFilterState } from "../../lib/searchResults";

/// The Section / Date / Speaker chips shown over an "everywhere" result set. Their options come from the hits
/// themselves (see `facetsOf`), so every choice is guaranteed to leave something behind.
///
/// Only rendered for a global search: a folder-scoped one is already narrow, and offering to narrow it further
/// is noise.
export default function SearchFilters({
  facets,
  filters,
  onChange,
}: {
  facets: SearchFacets;
  filters: SearchFilterState;
  onChange: (next: SearchFilterState) => void;
}) {
  const { t } = useTranslation("workspace");

  const sectionLabel = filters.sectionId
    ? facets.sections.find((s) => s.id === filters.sectionId)?.name ?? t("searchFilterSection")
    : t("searchFilterSection");

  return (
    <div className="flex shrink-0 flex-wrap items-center gap-1 border-b px-2 py-1.5 dark:border-gray-800">
      {facets.sections.length > 0 && (
        <Chip
          label={sectionLabel}
          active={!!filters.sectionId}
          options={facets.sections.map((s) => ({ value: s.id, label: `${s.name} (${s.count})` }))}
          onPick={(value) => onChange({ ...filters, sectionId: value })}
        />
      )}
      <DateChip
        from={filters.from ?? null}
        onPick={(from) => onChange({ ...filters, from })}
        label={filters.from ? t("searchFilterDateSince", { date: filters.from }) : t("searchFilterDate")}
        active={!!filters.from}
      />
      {facets.speakers.length > 0 && (
        <Chip
          label={filters.speaker ?? t("searchFilterSpeaker")}
          active={!!filters.speaker}
          options={facets.speakers.map((s) => ({ value: s, label: s }))}
          onPick={(value) => onChange({ ...filters, speaker: value })}
        />
      )}
    </div>
  );
}

/// A chip that opens a menu of options. Picking the active option again clears the filter, so a chip is its own
/// undo - there is no separate "clear" affordance to hunt for.
function Chip({
  label,
  active,
  options,
  onPick,
}: {
  label: string;
  active: boolean;
  options: { value: string; label: string }[];
  onPick: (value: string | null) => void;
}) {
  const { t } = useTranslation("workspace");
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    function onDown(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    }
    function onKey(e: KeyboardEvent) {
      if (e.key === "Escape") setOpen(false);
    }
    document.addEventListener("mousedown", onDown);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onDown);
      document.removeEventListener("keydown", onKey);
    };
  }, [open]);

  return (
    <div className="relative" ref={ref}>
      <button
        type="button"
        aria-haspopup="menu"
        aria-expanded={open}
        onClick={() => setOpen((v) => !v)}
        className={`flex items-center gap-0.5 rounded-full border px-2 py-0.5 text-[10px] font-medium ${
          active
            ? "border-blue-300 bg-blue-50 text-blue-700 dark:border-blue-800 dark:bg-blue-900/40 dark:text-blue-300"
            : "border-gray-200 text-gray-600 hover:bg-gray-50 dark:border-gray-700 dark:text-gray-400 dark:hover:bg-gray-800"
        }`}
      >
        <span className="max-w-[10ch] truncate">{label}</span>
        <ChevronDownIcon size={10} />
      </button>
      {open && (
        <div
          role="menu"
          className="absolute left-0 z-30 mt-1 max-h-56 w-44 overflow-y-auto rounded-lg border bg-white py-1 shadow-lg dark:border-gray-700 dark:bg-gray-900"
        >
          {active && (
            <button
              type="button"
              role="menuitem"
              onClick={() => {
                setOpen(false);
                onPick(null);
              }}
              className="block w-full px-3 py-1 text-left text-xs text-gray-500 hover:bg-gray-50 dark:text-gray-400 dark:hover:bg-gray-800"
            >
              {t("searchFilterAny")}
            </button>
          )}
          {options.map((o) => (
            <button
              key={o.value}
              type="button"
              role="menuitem"
              onClick={() => {
                setOpen(false);
                onPick(o.value);
              }}
              className="block w-full truncate px-3 py-1 text-left text-xs text-gray-700 hover:bg-gray-50 dark:text-gray-200 dark:hover:bg-gray-800"
            >
              {o.label}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

/// The date chip is a plain date input rather than a menu: the useful question is "since when", and canned
/// ranges would either miss the one you want or need a submenu.
function DateChip({
  from,
  label,
  active,
  onPick,
}: {
  from: string | null;
  label: string;
  active: boolean;
  onPick: (from: string | null) => void;
}) {
  const { t } = useTranslation("workspace");
  return (
    <label
      className={`flex items-center gap-0.5 rounded-full border px-2 py-0.5 text-[10px] font-medium ${
        active
          ? "border-blue-300 bg-blue-50 text-blue-700 dark:border-blue-800 dark:bg-blue-900/40 dark:text-blue-300"
          : "border-gray-200 text-gray-600 hover:bg-gray-50 dark:border-gray-700 dark:text-gray-400 dark:hover:bg-gray-800"
      }`}
    >
      <span className="max-w-[12ch] truncate">{label}</span>
      <input
        type="date"
        aria-label={t("searchFilterDate")}
        value={from ?? ""}
        onChange={(e) => onPick(e.target.value || null)}
        className="w-0 opacity-0"
      />
    </label>
  );
}
