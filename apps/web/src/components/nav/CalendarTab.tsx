import { useMemo, useState } from "react";
import { NavLink } from "react-router-dom";
import { useTranslation } from "react-i18next";
import type { TFunction } from "i18next";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "../../lib/api";
import { useSelection } from "../../lib/selection";
import { useDragAutoScroll } from "../../lib/dragAutoScroll";
import { recordingDayKeys, dayKey, eventDayKeys, visibleGridRange, dayItems } from "../../lib/calendar";
import { iconProps } from "../ToolbarButton";
import MonthCalendar from "../MonthCalendar";
import { RecordingRow } from "./RecordingRow";
import type { CalendarEvent, RecordingSummary } from "../../lib/types";

/// The panel's Calendar tab: a month grid over the selected day's merged list of recordings and (for a
/// personal room with Google Calendar connected) unlinked calendar events.
///
/// Owns its own state and its two queries. Because the panel mounts this only while the tab is showing,
/// those queries do not run while you are reading the meetings list - and the month resets to today when
/// you leave and come back. That reset is the deliberate trade: the alternative is keeping the month alive
/// in the panel, which is cold state in the hottest file in the app. Pinned by CalendarTab.test.tsx.
export default function CalendarTab({
  recordings,
  isPersonalRoom,
}: {
  recordings: RecordingSummary[];
  /// The Google overlay is personal-only: a shared room shows its own recordings and nothing else.
  isPersonalRoom: boolean;
}) {
  const { t, i18n } = useTranslation("workspace");
  const qc = useQueryClient();
  const selection = useSelection();
  // Native HTML5 DnD doesn't scroll the list while dragging near its edges, so a drop target outside the
  // viewport is unreachable in a long day list.
  const dayScrollRef = useDragAutoScroll<HTMLDivElement>();

  const [month, setMonth] = useState(() => {
    const now = new Date();
    return { year: now.getFullYear(), month: now.getMonth() };
  });
  const [selectedDay, setSelectedDay] = useState<string | null>(() => dayKey(new Date()));
  const dayKeys = useMemo(() => recordingDayKeys(recordings), [recordings]);

  // Google Calendar overlay: fetch the visible month's events (only when the user has connected Calendar).
  // Keyed by month, so navigating months auto-refetches; a short staleTime avoids refetch churn on focus.
  const { data: profile } = useQuery({ queryKey: ["user-profile"], queryFn: api.getProfile });
  const calendarConnected = profile?.googleCalendar === true;
  const { data: calendarEvents = [], isFetching: eventsFetching } = useQuery({
    queryKey: ["calendar-events", month.year, month.month],
    queryFn: () => {
      const { timeMin, timeMax } = visibleGridRange(month.year, month.month);
      return api.getCalendarEvents(timeMin, timeMax);
    },
    // A shared room shows only its recordings on the calendar - no personal Google-event overlay.
    enabled: calendarConnected && isPersonalRoom,
    staleTime: 5 * 60_000,
    retry: false,
  });
  // The Google overlay is personal-only. Force events empty in a shared room (the disabled query still holds
  // the last personal-room data in cache - the key is room-agnostic - so gate the derived value, not just the fetch).
  const showCalendarOverlay = calendarConnected && isPersonalRoom;
  // Memoised, not `isPersonalRoom ? calendarEvents : []`: a fresh [] every render defeated the eventKeys
  // memo on the next line, and dayItems below it, for every shared-room render.
  const events = useMemo(() => (isPersonalRoom ? calendarEvents : []), [isPersonalRoom, calendarEvents]);
  const eventKeys = useMemo(() => eventDayKeys(events), [events]);
  const selectedItems = useMemo(
    () => (selectedDay ? dayItems(recordings, events, selectedDay) : []),
    [recordings, events, selectedDay],
  );
  function stepMonth(delta: number) {
    setMonth((m) => {
      const d = new Date(m.year, m.month + delta, 1);
      return { year: d.getFullYear(), month: d.getMonth() };
    });
  }

  return (
    // Calendar: the month grid stays fixed at the top; only the selected day's list scrolls.
    // min-w-0 is essential: without it this flex child grows to the widest day-list row, which would
    // stretch the grid-cols-7 month grid wider than the panel and make the calendar appear to resize
    // when you pick a day with longer recording names.
    <div className="flex min-h-0 min-w-0 flex-1 flex-col">
      <div className="shrink-0 border-b dark:border-gray-800">
        <MonthCalendar
          year={month.year}
          month={month.month}
          daysWithRecordings={dayKeys}
          daysWithEvents={showCalendarOverlay ? eventKeys : undefined}
          selectedKey={selectedDay}
          onSelect={setSelectedDay}
          onPrev={() => stepMonth(-1)}
          onNext={() => stepMonth(1)}
        />
        {showCalendarOverlay && (
          <div className="flex items-center justify-end px-2 pb-1">
            <button
              type="button"
              onClick={() => qc.invalidateQueries({ queryKey: ["calendar-events", month.year, month.month] })}
              disabled={eventsFetching}
              className="text-[10px] text-gray-400 hover:text-gray-600 disabled:opacity-50 dark:text-gray-500 dark:hover:text-gray-300"
            >
              {eventsFetching ? t("calRefreshing") : t("calRefreshEvents")}
            </button>
          </div>
        )}
      </div>
      {/* Reserve the scrollbar gutter so toggling the day list's scrollbar never shifts its width. */}
      <div ref={dayScrollRef} className="min-h-0 min-w-0 flex-1 overflow-y-auto [scrollbar-gutter:stable]">
        {selectedItems.length === 0 ? (
          <p className="p-4 text-sm text-gray-500 dark:text-gray-400">
            {showCalendarOverlay ? t("calDayEmpty") : t("calNoRecordings")}
          </p>
        ) : (
          <ul className="divide-y dark:divide-gray-800">
            {selectedItems.map((it) =>
              it.type === "recording" ? (
                <RecordingRow
                  key={it.recording.id}
                  r={it.recording}
                  indentClass="pl-3"
                  selectMode={selection.selectMode}
                  selected={selection.selectedIds.includes(it.recording.id)}
                  onToggleSelect={() => selection.toggle(it.recording.id)}
                  onDropBefore={() => {}}
                />
              ) : (
                <EventRow key={`ev-${it.event.id}`} event={it.event} locale={i18n.language} t={t} />
              ),
            )}
          </ul>
        )}
      </div>
    </div>
  );
}

/// A Google Calendar event row in the Calendar tab's merged day list — time range + title. Clicking the row
/// opens the event preview (a meeting with no recording); the calendar glyph still links out to Google.
/// Only unlinked events reach this row (a linked event is shown by its recording row, deduped in `dayItems`).
/// Events from an external .ics feed (`calendarId` starting `ics:`) are display-only - they have no Google
/// event to preview or link a recording to - so their row is a static (non-clickable) block, still coloured.
function EventRow({ event, locale, t }: { event: CalendarEvent; locale: string; t: TFunction }) {
  const fmt = new Intl.DateTimeFormat(locale, { hour: "2-digit", minute: "2-digit" });
  const title = event.summary || t("calUntitledEvent");
  const range = `${fmt.format(new Date(event.start))} – ${fmt.format(new Date(event.end))}`;
  const isFeed = event.calendarId?.startsWith("ics:") ?? false;

  const inner = (
    <>
      <svg
        {...iconProps}
        style={event.color ? { color: event.color } : undefined}
        className={`mt-0.5 h-3.5 w-3.5 shrink-0 ${event.color ? "" : "text-green-600 dark:text-green-400"}`}
        aria-label={t("calEventLabel")}
      >
        <rect x="3" y="4" width="18" height="18" rx="2" ry="2" />
        <line x1="16" y1="2" x2="16" y2="6" />
        <line x1="8" y1="2" x2="8" y2="6" />
        <line x1="3" y1="10" x2="21" y2="10" />
      </svg>
      <div className="min-w-0 flex-1">
        <div className="truncate text-gray-800 dark:text-gray-200">{title}</div>
        <div className="flex items-center gap-1.5 text-xs text-gray-500 dark:text-gray-400">
          <span className="tabular-nums">{range}</span>
          {event.calendarName && <span className="truncate">· {event.calendarName}</span>}
        </div>
      </div>
    </>
  );

  return (
    <li>
      {isFeed ? (
        <div className="flex items-start gap-2 py-1.5 pl-3 pr-2 text-sm">{inner}</div>
      ) : (
        <NavLink
          to={`/calendar-event/${encodeURIComponent(event.id)}`}
          className={({ isActive }) =>
            `flex items-start gap-2 py-1.5 pl-3 pr-2 text-sm ${
              isActive ? "bg-blue-50 dark:bg-blue-900/30" : "hover:bg-gray-50 dark:hover:bg-gray-800"
            }`
          }
        >
          {inner}
        </NavLink>
      )}
    </li>
  );
}
