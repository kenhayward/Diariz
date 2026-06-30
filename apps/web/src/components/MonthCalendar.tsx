import { useTranslation } from "react-i18next";
import { iconProps } from "./ToolbarButton";
import { buildMonthGrid, weekdayLabels, monthLabel, dayKey } from "../lib/calendar";

/// A month grid for the recordings Calendar tab. Days that have recordings are highlighted green and
/// are the only selectable cells; days without are grey. Purely presentational — the parent owns the
/// visible month, the set of days-with-recordings, and the selection.
export default function MonthCalendar({
  year,
  month,
  daysWithRecordings,
  selectedKey,
  onSelect,
  onPrev,
  onNext,
}: {
  year: number;
  month: number; // 0-based
  daysWithRecordings: Set<string>;
  selectedKey: string | null;
  onSelect: (key: string) => void;
  onPrev: () => void;
  onNext: () => void;
}) {
  const { t, i18n } = useTranslation("workspace");
  // Drop any trailing week made up entirely of next-month days — keeps the grid compact.
  const weeks = buildMonthGrid(year, month).filter((w) => w.some((d) => d.inMonth));
  const todayKey = dayKey(new Date());

  return (
    <div className="px-2 py-1.5">
      <div className="mb-1 flex items-center justify-between">
        <button
          type="button"
          onClick={onPrev}
          aria-label={t("calPrevMonth")}
          className="rounded px-1.5 py-1 text-gray-500 hover:bg-gray-100 dark:text-gray-400 dark:hover:bg-gray-800"
        >
          <svg {...iconProps}><polyline points="15 18 9 12 15 6" /></svg>
        </button>
        <span className="text-sm font-medium capitalize dark:text-gray-100">{monthLabel(year, month, i18n.language)}</span>
        <button
          type="button"
          onClick={onNext}
          aria-label={t("calNextMonth")}
          className="rounded px-1.5 py-1 text-gray-500 hover:bg-gray-100 dark:text-gray-400 dark:hover:bg-gray-800"
        >
          <svg {...iconProps}><polyline points="9 18 15 12 9 6" /></svg>
        </button>
      </div>

      <div className="grid grid-cols-7 gap-0.5 text-center text-[9px] uppercase text-gray-400 dark:text-gray-500">
        {weekdayLabels(i18n.language).map((w, i) => (
          <div key={i}>{w}</div>
        ))}
      </div>

      {/* Short rectangular cells (not square) keep the calendar compact so the day list below gets room. */}
      <div className="mt-0.5 grid grid-cols-7 gap-0.5">
        {weeks.flat().map((day, i) => {
          // Don't show adjacent-month days (confusing) — render an empty placeholder to keep alignment.
          if (!day.inMonth) return <div key={`adj-${i}`} className="h-5" />;
          const has = daysWithRecordings.has(day.key);
          const selected = day.key === selectedKey;
          const isToday = day.key === todayKey;
          return (
            <button
              key={day.key}
              type="button"
              disabled={!has}
              onClick={() => has && onSelect(day.key)}
              aria-pressed={selected}
              aria-current={isToday ? "date" : undefined}
              className={[
                // A constant 2px inset ring is reserved on every cell; only its colour changes (exactly one
                // colour class below), so selecting a day never grows the box and the grid can't reflow.
                "flex h-5 items-center justify-center rounded text-[11px] tabular-nums ring-2 ring-inset",
                has
                  ? "cursor-pointer bg-green-100 text-green-900 hover:bg-green-200 dark:bg-green-900/40 dark:text-green-200 dark:hover:bg-green-900/60"
                  : "cursor-default bg-gray-50 text-gray-400 dark:bg-gray-800/60 dark:text-gray-500",
                selected ? "ring-green-500 dark:ring-green-400" : isToday ? "ring-blue-400" : "ring-transparent",
              ].join(" ")}
            >
              {day.date.getDate()}
            </button>
          );
        })}
      </div>
    </div>
  );
}
