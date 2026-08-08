import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { NavLink } from "react-router-dom";
import { useTranslation } from "react-i18next";
import { dayItemSpan, type DayItem } from "../../lib/calendar";
import { showStatusBadge } from "../../lib/recordingStatus";
import {
  BADGED_MIN_HEIGHT, MIN_BLOCK_HEIGHT, hourToPx, initialScrollTop, layoutDay,
  type DayGridInput, type DayLayout,
} from "../../lib/dayGrid";
import DayBlock from "./DayBlock";

/// The Calendar tab's day view: the selected day laid out on a time axis, so you can see when meetings
/// sat, how long they ran and where the gaps are - rather than a flat list that makes a 15-minute catch-up
/// and a 90-minute all-hands look identical.
///
/// Owns the scroller (so it can position it) and the all-day strip above it; the pure geometry lives in
/// `lib/dayGrid.ts`.

/// A day item joined to its grid coordinates. `layoutDay` hands the whole object back on each block, so
/// rendering needs no lookup step.
export interface DayGridItem extends DayGridInput {
  dayItem: DayItem;
}

/// Once a "+N" chip is opened the cap is lifted for the day, but not to infinity: a 200-meeting cluster
/// would otherwise become 200 columns of half a percent each.
const EXPANDED_COLUMNS = 8;

export default function DayGrid({
  items,
  dayKey,
  isToday,
  emptyLabel,
  autoScrollRef,
}: {
  items: DayItem[];
  /// The selected local day (YYYY-MM-DD) - what the spans are measured against.
  dayKey: string;
  isToday: boolean;
  emptyLabel: string;
  /// The panel's drag-auto-scroll callback ref, merged onto this component's own scroller.
  autoScrollRef: (node: HTMLDivElement | null) => void;
}) {
  const { i18n } = useTranslation("workspace");
  const scroller = useRef<HTMLDivElement | null>(null);
  const [expanded, setExpanded] = useState(false);

  useEffect(() => {
    setExpanded(false);
  }, [dayKey]);

  const layout = useMemo(() => {
    const inputs: DayGridItem[] = items.map((it) => {
      const span = dayItemSpan(it, dayKey);
      // A status pill must never be the thing that gets dropped, so a badged recording asks for the
      // taller floor - which keeps it above the one-line threshold.
      const badged = it.type === "recording" && showStatusBadge(it.recording.status);
      return {
        key: it.type === "recording" ? `rec-${it.recording.id}` : `ev-${it.event.id}`,
        startHour: span.startHour,
        endHour: span.endHour,
        allDay: span.allDay,
        minHeight: badged ? BADGED_MIN_HEIGHT : MIN_BLOCK_HEIGHT,
        dayItem: it,
      };
    });
    return layoutDay(inputs, { maxColumns: expanded ? EXPANDED_COLUMNS : undefined });
  }, [items, dayKey, expanded]);

  // Open on the working day, not the window's first hour - and on today, an hour before now so the now
  // line is on screen. Keyed on the day (and the window's start) rather than on `layout`, so a late
  // arriving events query doesn't yank the user's scroll position back.
  const windowStart = layout.startHour;
  useEffect(() => {
    const node = scroller.current;
    if (!node) return;
    node.scrollTop = initialScrollTop({ startHour: windowStart }, isToday ? hourOfDay(new Date()) : null);
  }, [dayKey, windowStart, isToday]);

  const setScroller = useCallback(
    (node: HTMLDivElement | null) => {
      scroller.current = node;
      autoScrollRef(node);
    },
    [autoScrollRef],
  );

  const hourLabel = useMemo(() => {
    const fmt = new Intl.DateTimeFormat(i18n.language, { hour: "numeric" });
    return (h: number) => fmt.format(new Date(2024, 0, 1, h));
  }, [i18n.language]);

  return (
    <>
      {layout.allDay.length > 0 && <AllDayRow items={layout.allDay} />}
      {/* Reserve the scrollbar gutter so toggling the scrollbar never shifts the grid's width. */}
      <div
        ref={setScroller}
        data-testid="day-scroller"
        className="min-h-0 min-w-0 flex-1 overflow-y-auto [scrollbar-gutter:stable]"
      >
        <div className="relative" style={{ height: layout.totalHeight }}>
          {/* The rail reads as its own quiet surface, so the axis stays legible against the panel. */}
          <div aria-hidden className="absolute inset-y-0 left-0 w-[44px] bg-gray-50 dark:bg-white/[0.07]" />
          <div aria-hidden className="absolute inset-y-0 left-[44px] w-px bg-gray-200 dark:bg-gray-700" />
          {layout.hours.map((h) => (
            <div
              key={h}
              data-testid="hour-row"
              aria-hidden
              className="relative box-border h-[44px] border-t border-gray-100 dark:border-gray-800"
            >
              {/* Straddles its own rule, so the label reads as marking the line rather than the row. */}
              <span className="absolute left-0 top-[-7px] w-[38px] text-right text-[10px] tabular-nums text-gray-500 dark:font-medium dark:text-gray-300">
                {hourLabel(h)}
              </span>
            </div>
          ))}

          <div role="list" className="absolute inset-y-0 left-[54px] right-[10px]">
            {/* Rendered in the layout's order, which is TIME order - and therefore the keyboard focus
                order. Never re-sort by column: that would make tabbing through a day jump about. */}
            {layout.blocks.map((b) => (
              <DayBlock key={b.item.key} block={b} locale={i18n.language} />
            ))}
            {layout.overflow.map((chip) => (
              <MoreChip key={chip.items[0].key} top={chip.top} count={chip.count} onExpand={() => setExpanded(true)} />
            ))}
          </div>

          {/* An empty day keeps its axis - where the gaps are is half of what this view is for. */}
          {items.length === 0 && (
            <p className="absolute left-[54px] right-[10px] top-1/3 text-center text-sm text-gray-500 dark:text-gray-400">
              {emptyLabel}
            </p>
          )}

          {isToday && <NowLine layout={layout} locale={i18n.language} />}
        </div>
      </div>
    </>
  );
}

/// Meetings with no meaningful start on this day - all-day entries, and multi-day spans that began
/// earlier. Pinned above the axis rather than drawn as a block at midnight.
function AllDayRow({ items }: { items: DayGridItem[] }) {
  const { t } = useTranslation("workspace");
  return (
    <div
      data-testid="all-day-row"
      className="flex shrink-0 flex-wrap items-center gap-1 border-b px-2.5 py-1 dark:border-gray-800"
    >
      <span className="shrink-0 text-[10px] uppercase text-gray-400 dark:text-gray-500">{t("calAllDay")}</span>
      {items.map((i) => (
        <AllDayChip key={i.key} item={i} />
      ))}
    </div>
  );
}

function AllDayChip({ item }: { item: DayGridItem }) {
  const { t } = useTranslation("workspace");
  if (item.dayItem.type !== "event") return null;
  const e = item.dayItem.event;
  const title = e.summary || t("calUntitledEvent");
  const isFeed = e.calendarId?.startsWith("ics:") ?? false;
  const className =
    "max-w-full truncate rounded-[3px] px-1.5 py-0.5 text-[11px] text-gray-800 dark:text-gray-200";
  const style = e.color ? { boxShadow: `inset 2px 0 0 ${e.color}` } : undefined;
  return isFeed ? (
    <span className={`${className} bg-gray-100 dark:bg-white/5`} style={style} title={title}>{title}</span>
  ) : (
    <NavLink
      to={`/calendar-event/${encodeURIComponent(e.id)}`}
      className={`${className} bg-gray-100 hover:bg-gray-200 dark:bg-white/5 dark:hover:bg-white/10`}
      style={style}
      title={title}
    >
      {title}
    </NavLink>
  );
}

/// A cluster too wide to draw. A real button, not a static "+2": a chip that silently hides meetings and
/// cannot be opened is a hole in the day.
function MoreChip({ top, count, onExpand }: { top: number; count: number; onExpand: () => void }) {
  const { t } = useTranslation("workspace");
  return (
    <button
      type="button"
      onClick={onExpand}
      style={{ top }}
      className="absolute right-0 rounded-[3px] bg-gray-200 px-1 text-[9px] font-semibold text-gray-700 hover:bg-gray-300 dark:bg-white/10 dark:text-gray-200 dark:hover:bg-white/20"
    >
      {t("calMoreItems", { count })}
    </button>
  );
}

/// Where the clock is now, on today's grid only. Its own component with its own timer so the minute tick
/// re-renders six nodes rather than every block on the day.
function NowLine({ layout, locale }: { layout: DayLayout<DayGridItem>; locale: string }) {
  const { t } = useTranslation("workspace");
  const [now, setNow] = useState(() => new Date());

  useEffect(() => {
    const id = setInterval(() => setNow(new Date()), 60_000);
    return () => clearInterval(id);
  }, []);

  const hour = hourOfDay(now);
  if (hour < layout.startHour || hour > layout.endHour) return null;
  const label = new Intl.DateTimeFormat(locale, { hour: "numeric", minute: "2-digit" }).format(now);

  return (
    <div
      data-testid="now-line"
      className="pointer-events-none absolute left-0 right-[10px] z-[5]"
      style={{ top: hourToPx(hour, layout) }}
    >
      <span aria-hidden className="absolute left-[44px] right-0 top-0 border-t border-dashed border-[var(--hub-red)]" />
      <span aria-hidden className="absolute left-[41px] top-[-3px] h-1.5 w-1.5 rounded-full bg-[var(--hub-red)]" />
      <span
        role="img"
        aria-label={`${t("calNowAria")} ${label}`}
        className="absolute left-0 top-[-7px] w-[38px] text-right text-[9px] font-bold tabular-nums text-[var(--hub-red)]"
      >
        {label}
      </span>
    </div>
  );
}

/// Fractional local hours since midnight - wall clock, so a DST day still has 24 of them.
function hourOfDay(d: Date): number {
  return d.getHours() + d.getMinutes() / 60;
}
