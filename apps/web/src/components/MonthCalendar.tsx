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
  const weeks = buildMonthGrid(year, month);
  const todayKey = dayKey(new Date());

  return (
    <div className="px-3 py-2">
      <div className="mb-2 flex items-center justify-between">
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

      <div className="grid grid-cols-7 gap-1 text-center text-[10px] uppercase text-gray-400 dark:text-gray-500">
        {weekdayLabels(i18n.language).map((w, i) => (
          <div key={i} className="py-0.5">{w}</div>
        ))}
      </div>

      <div className="mt-1 grid grid-cols-7 gap-1">
        {weeks.flat().map((day) => {
          const has = daysWithRecordings.has(day.key);
          const selected = day.key === selectedKey;
          const isToday = day.key === todayKey;
          return (
            <button
              key={day.key + (day.inMonth ? "" : "-adj")}
              type="button"
              disabled={!has}
              onClick={() => has && onSelect(day.key)}
              aria-pressed={selected}
              aria-current={isToday ? "date" : undefined}
              className={[
                "aspect-square rounded text-xs tabular-nums",
                has
                  ? "cursor-pointer bg-green-100 text-green-900 hover:bg-green-200 dark:bg-green-900/40 dark:text-green-200 dark:hover:bg-green-900/60"
                  : "cursor-default bg-gray-50 text-gray-300 dark:bg-gray-800/60 dark:text-gray-600",
                !day.inMonth ? "opacity-50" : "",
                selected ? "ring-2 ring-inset ring-green-500 dark:ring-green-400" : "",
                isToday && !selected ? "ring-1 ring-inset ring-blue-400" : "",
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
