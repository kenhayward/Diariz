import { useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "../../lib/api";
import { useDragAutoScroll } from "../../lib/dragAutoScroll";
import { recordingDayKeys, dayKey, eventDayKeys, visibleGridRange, dayItems, type DayItem } from "../../lib/calendar";
import {
  canSyncOutlook as shellCanSyncOutlook,
  onOutlookState,
  outlookAvailable as checkOutlookAvailable,
  syncOutlookNow,
} from "../../lib/outlookSync";
import MonthCalendar from "../MonthCalendar";
import DayGrid from "./DayGrid";
import type { RecordingSummary } from "../../lib/types";

/// The panel's Calendar tab: a month grid over the selected day laid out on a **time axis** - recordings and
/// (in a personal room) unlinked calendar events from every source the user has (Google, subscribed .ics
/// feeds, and a mirrored desktop Outlook calendar), positioned and sized by when they actually ran.
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
  /// The event overlay is personal-only: a shared room shows its own recordings and nothing else.
  isPersonalRoom: boolean;
}) {
  const { t, i18n } = useTranslation("workspace");
  const qc = useQueryClient();
  // Native HTML5 DnD doesn't scroll the list while dragging near its edges, so a drop target outside the
  // viewport is unreachable in a long day. DayGrid owns the scroller (it positions it on the hour axis)
  // and merges this callback ref onto it.
  const dayScrollRef = useDragAutoScroll<HTMLDivElement>();

  const [month, setMonth] = useState(() => {
    const now = new Date();
    return { year: now.getFullYear(), month: now.getMonth() };
  });
  const [selectedDay, setSelectedDay] = useState<string | null>(() => dayKey(new Date()));
  const dayKeys = useMemo(() => recordingDayKeys(recordings), [recordings]);

  // Calendar overlay: fetch the visible month's events. Keyed by month, so navigating months auto-refetches;
  // a short staleTime avoids refetch churn on focus.
  const { data: settings } = useQuery({ queryKey: ["user-settings"], queryFn: api.getUserSettings });
  const { data: calendarEvents = [], isFetching: eventsFetching } = useQuery({
    queryKey: ["calendar-events", month.year, month.month],
    queryFn: () => {
      const { timeMin, timeMax } = visibleGridRange(month.year, month.month);
      return api.getCalendarEvents(timeMin, timeMax);
    },
    // A shared room shows only its recordings on the calendar - the event overlay is personal-only.
    enabled: isPersonalRoom,
    staleTime: 5 * 60_000,
    retry: false,
  });
  // Personal-only. Force events empty in a shared room (the disabled query still holds the last personal-room
  // data in cache - the key is room-agnostic - so gate the derived value, not just the fetch).
  //
  // Deliberately NOT gated on a Google connection any more. It used to be, which gave anyone whose calendar
  // was entirely .ics feeds - or now a desktop Outlook mirror - a permanently empty Calendar tab. The endpoint
  // already returns [] when nothing is connected, so the gate bought nothing and cost those users the feature.
  const showCalendarOverlay = isPersonalRoom;

  // A "Sync Outlook" affordance right where the meetings are, so a user who notices a missing one does not
  // have to go to Preferences to refresh. Availability is asked once; the phase keeps the button honest while
  // a sync runs. All three are inert in a browser, so this costs nothing off the desktop.
  const [outlookAvailable, setOutlookAvailable] = useState(false);
  const [outlookSyncing, setOutlookSyncing] = useState(false);
  const outlookOptedIn = outlookAvailable && settings?.outlookSyncEnabled === true;

  useEffect(() => {
    let live = true;
    void checkOutlookAvailable().then((ok) => {
      if (live) setOutlookAvailable(ok);
    });
    return () => {
      live = false;
    };
  }, []);

  useEffect(() => onOutlookState((s) => setOutlookSyncing(s.phase !== "idle")), []);

  async function syncOutlook() {
    const { started } = await syncOutlookNow();
    // A refusal (cooldown, busy) needs no message here - Preferences is where the detail lives. Refreshing on
    // a started sync is what makes the new meetings appear without the user doing anything else.
    if (started) void qc.invalidateQueries({ queryKey: ["calendar-events", month.year, month.month] });
  }
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
    // Calendar: the month grid, the day heading and the all-day strip stay fixed at the top; only the
    // hour axis scrolls.
    // min-w-0 is essential: without it this flex child grows to its widest content, which would stretch
    // the grid-cols-7 month grid wider than the panel and make the calendar appear to resize when you
    // pick a day with longer recording names.
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
          <div className="flex items-center justify-end gap-3 px-2 pb-1">
            {/* Only on the Windows desktop, with Outlook reachable and the user opted in - elsewhere there is
                nothing that could answer, so no button rather than one that explains itself away. */}
            {shellCanSyncOutlook() && outlookOptedIn && (
              <button
                type="button"
                onClick={() => void syncOutlook()}
                disabled={outlookSyncing}
                className="text-[10px] text-gray-400 hover:text-gray-600 disabled:opacity-50 dark:text-gray-500 dark:hover:text-gray-300"
              >
                {outlookSyncing ? t("calSyncingOutlook") : t("calSyncOutlook")}
              </button>
            )}
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
      {selectedDay && (
        <>
          <DayHeading dayKey={selectedDay} items={selectedItems} locale={i18n.language} />
          <DayGrid
            items={selectedItems}
            dayKey={selectedDay}
            isToday={selectedDay === dayKey(new Date())}
            emptyLabel={showCalendarOverlay ? t("calDayEmpty") : t("calNoRecordings")}
            autoScrollRef={dayScrollRef}
          />
        </>
      )}
    </div>
  );
}

/// The day's date and what is on it, pinned above the grid so it does not scroll away with the hours.
function DayHeading({ dayKey: key, items, locale }: { dayKey: string; items: DayItem[]; locale: string }) {
  const { t } = useTranslation("workspace");
  const [y, m, d] = key.split("-").map(Number);
  // Built from parts, never `new Date("2026-08-08")`: the string form is parsed as UTC midnight, which
  // renders the *previous* day anywhere west of Greenwich.
  const date = new Date(y, m - 1, d);
  const recordings = items.filter((i) => i.type === "recording").length;
  const meetings = items.length - recordings;
  const counts = [
    meetings > 0 ? t("calDayMeetingCount", { count: meetings }) : null,
    recordings > 0 ? t("calDayRecordingCount", { count: recordings }) : null,
  ].filter(Boolean).join(" · ");

  return (
    <div className="flex shrink-0 items-baseline justify-between gap-2 border-b px-2.5 py-1.5 dark:border-gray-800">
      <span className="truncate text-[13px] font-semibold capitalize text-gray-900 dark:text-gray-100">
        {new Intl.DateTimeFormat(locale, { weekday: "long", day: "numeric", month: "long" }).format(date)}
      </span>
      <span className="shrink-0 text-[11px] text-gray-400 dark:text-gray-500">{counts}</span>
    </div>
  );
}
