import { useEffect, useMemo, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api, apiErrorMessage } from "../lib/api";
import type { CalendarEvent, CalendarLink } from "../lib/types";
import { formatDate, formatTimeHm } from "../lib/format";

const WINDOW_DAYS = 7;

/// Local-midnight Date `days` from `d` (DST-safe via the y/m/d ctor).
function midnightPlus(d: Date, days: number): Date {
  return new Date(d.getFullYear(), d.getMonth(), d.getDate() + days);
}

/// Browse-and-pick modal to link a recording to a calendar meeting, even when the times don't line up.
/// Shows a moving date window (prev/later) around the recording's day and a title filter; clicking a
/// meeting persists a manual link.
export default function CalendarLinkModal({
  recordingId,
  aroundDate,
  onClose,
  onLinked,
}: {
  recordingId: string;
  aroundDate: string; // ISO - the recording's createdAt; the window is centred on this day
  onClose: () => void;
  onLinked?: (link: CalendarLink) => void;
}) {
  const { t, i18n } = useTranslation(["workspace", "common"]);
  const qc = useQueryClient();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [filter, setFilter] = useState("");

  // Window anchor: start 3 days before the recording's day so the meeting is comfortably in view.
  const base = new Date(aroundDate);
  const [anchor, setAnchor] = useState(() => midnightPlus(base, -3));
  const rangeEnd = midnightPlus(anchor, WINDOW_DAYS);
  const timeMin = anchor.toISOString();
  const timeMax = rangeEnd.toISOString();

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  const { data: events = [], isLoading } = useQuery({
    queryKey: ["calendar-link-events", timeMin, timeMax],
    queryFn: () => api.getCalendarEvents(timeMin, timeMax),
  });

  const shown = useMemo(() => {
    const q = filter.trim().toLowerCase();
    return events
      .filter((e) => !q || (e.summary ?? "").toLowerCase().includes(q))
      .sort((a, b) => new Date(a.start).getTime() - new Date(b.start).getTime());
  }, [events, filter]);

  async function link(ev: CalendarEvent) {
    setBusy(true);
    setError(null);
    try {
      const l = await api.putCalendarLink(recordingId, ev.id, true, ev.calendarId);
      qc.invalidateQueries({ queryKey: ["recording", recordingId] });
      qc.invalidateQueries({ queryKey: ["recordings"] });
      onLinked?.(l);
      onClose();
    } catch (e) {
      setError(apiErrorMessage(e));
      setBusy(false);
    }
  }

  const rangeLabel = `${formatDate(timeMin, i18n.language)} - ${formatDate(midnightPlus(anchor, WINDOW_DAYS - 1).toISOString(), i18n.language)}`;

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4" onClick={onClose}>
      <div
        role="dialog"
        aria-label={t("calLinkModalTitle")}
        className="flex max-h-[80vh] w-full max-w-md flex-col rounded-lg border bg-white p-4 shadow-xl dark:border-gray-700 dark:bg-gray-900"
        onClick={(e) => e.stopPropagation()}
      >
        <h2 className="text-base font-semibold dark:text-gray-100">{t("calLinkModalTitle")}</h2>
        <p className="mt-1 text-xs text-gray-500 dark:text-gray-400">{t("calLinkModalHint")}</p>

        <div className="mt-3 flex items-center gap-2">
          <button
            type="button"
            onClick={() => setAnchor((a) => midnightPlus(a, -WINDOW_DAYS))}
            disabled={busy}
            className="shrink-0 rounded border px-2 py-1 text-sm hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            ‹ {t("calRangePrev")}
          </button>
          <span className="min-w-0 flex-1 truncate text-center text-xs tabular-nums text-gray-600 dark:text-gray-300">
            {rangeLabel}
          </span>
          <button
            type="button"
            onClick={() => setAnchor((a) => midnightPlus(a, WINDOW_DAYS))}
            disabled={busy}
            className="shrink-0 rounded border px-2 py-1 text-sm hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("calRangeNext")} ›
          </button>
        </div>

        <input
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
          placeholder={t("calFilterPlaceholder")}
          aria-label={t("calFilterPlaceholder")}
          className="mt-2 w-full rounded border px-2 py-1 text-sm dark:border-gray-700 dark:bg-gray-800 dark:text-gray-100"
        />

        <div className="mt-2 min-h-0 flex-1 overflow-y-auto">
          {isLoading ? (
            <p className="px-1 py-4 text-sm text-gray-500 dark:text-gray-400">{t("calLinkLoading")}</p>
          ) : shown.length === 0 ? (
            <p className="px-1 py-4 text-sm text-gray-500 dark:text-gray-400">{t("calNoEventsInRange")}</p>
          ) : (
            <ul className="space-y-1">
              {shown.map((ev) => {
                const title = ev.summary || t("calUntitledEvent");
                return (
                  <li key={ev.id}>
                    <button
                      type="button"
                      disabled={busy}
                      onClick={() => link(ev)}
                      aria-label={t("calLinkThisEvent", { name: title })}
                      className="block w-full rounded border px-3 py-1.5 text-left text-sm hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:hover:bg-gray-800"
                    >
                      <div className="truncate text-gray-800 dark:text-gray-200">{title}</div>
                      <div className="text-xs tabular-nums text-gray-500 dark:text-gray-400">
                        {formatDate(ev.start, i18n.language)} · {formatTimeHm(ev.start)} - {formatTimeHm(ev.end)}
                      </div>
                    </button>
                  </li>
                );
              })}
            </ul>
          )}
        </div>

        {error && <p className="mt-2 text-sm text-red-600 dark:text-red-400">{error}</p>}

        <div className="mt-3 flex justify-end border-t pt-3 dark:border-gray-700">
          <button
            type="button"
            onClick={onClose}
            className="rounded border px-3 py-1 text-sm hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("common:cancel")}
          </button>
        </div>
      </div>
    </div>
  );
}
