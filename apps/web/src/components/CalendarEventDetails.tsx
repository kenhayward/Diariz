import { useState } from "react";
import { useTranslation } from "react-i18next";
import type { CalendarEvent, CalendarAttendee } from "../lib/types";
import { formatLongDate, formatTimeHm } from "../lib/format";
import LinkifiedText from "./LinkifiedText";

/// Localised label for a Google attendee response status; falls back to the raw value.
const RESP_KEY: Record<string, string> = {
  accepted: "calRespAccepted",
  declined: "calRespDeclined",
  tentative: "calRespTentative",
  needsAction: "calRespNeedsAction",
};

function attendeeName(a: CalendarAttendee): string {
  return a.displayName || a.email || "";
}

/// Shows a Google Calendar event's full invite details - when, where, organiser, attendees (with their
/// response), description, and a link out to Google Calendar - so the user needn't leave Diariz to check.
/// Shared by the recording Overview (a linked meeting) and the recording-less event preview.
/// When <c>showTitle</c> is set (the recording Overview), the event title is shown at the top as the link out
/// to Google Calendar (so no separate "Open in Google Calendar" line is needed); the event-preview page passes
/// it off because that page already shows the title as its own heading.
export default function CalendarEventDetails({
  event,
  showTitle = false,
}: {
  event: CalendarEvent;
  showTitle?: boolean;
}) {
  const { t, i18n } = useTranslation(["workspace"]);
  const when = `${formatLongDate(event.start, i18n.language)} · ${formatTimeHm(event.start)} - ${formatTimeHm(event.end)}`;
  const organizer = event.organizer ? attendeeName(event.organizer) : "";
  const attendees = event.attendees ?? [];
  const title = event.summary || t("workspace:meetingUntitled");
  // Collapsed by default: this is the longest block in the panel and rarely the reason the event was opened.
  const [attendeesOpen, setAttendeesOpen] = useState(false);

  return (
    <div className="space-y-3 text-sm">
      {showTitle &&
        (event.htmlLink ? (
          <a
            href={event.htmlLink}
            target="_blank"
            rel="noopener noreferrer"
            className="block font-medium text-indigo-600 hover:underline dark:text-indigo-400"
          >
            {title}
          </a>
        ) : (
          <div className="font-medium text-gray-900 dark:text-gray-100">{title}</div>
        ))}
      {(event.calendarName || event.recurring) && (
        <div className="flex items-center gap-2 text-xs text-gray-500 dark:text-gray-400">
          {event.calendarName && (
            <>
              <span
                className="inline-block h-2.5 w-2.5 shrink-0 rounded-full"
                style={{ backgroundColor: event.color ?? "#0B8043" }}
                aria-hidden
              />
              <span className="truncate">{event.calendarName}</span>
            </>
          )}
          {event.recurring && (
            <span className="shrink-0 rounded-full bg-gray-100 px-1.5 py-0.5 text-[10px] font-medium uppercase tracking-wide text-gray-600 dark:bg-gray-800 dark:text-gray-300">
              {t("workspace:calRecurring")}
            </span>
          )}
        </div>
      )}
      <dl className="grid grid-cols-[max-content_1fr] gap-x-4 gap-y-1">
        <dt className="text-gray-500 dark:text-gray-400">{t("workspace:calWhen")}</dt>
        <dd className="text-gray-800 dark:text-gray-200">{when}</dd>
        {event.location && (
          <>
            <dt className="text-gray-500 dark:text-gray-400">{t("workspace:calWhere")}</dt>
            {/* A long unbroken value (e.g. a Zoom join URL) must break mid-token, or it forces the 1fr grid
                track wider than the panel. min-w-0 lets the track shrink; overflow-wrap:anywhere does the break. */}
            <dd className="min-w-0 [overflow-wrap:anywhere] text-gray-800 dark:text-gray-200">
              <LinkifiedText text={event.location} />
            </dd>
          </>
        )}
        {organizer && (
          <>
            <dt className="text-gray-500 dark:text-gray-400">{t("workspace:calOrganizer")}</dt>
            <dd className="text-gray-800 dark:text-gray-200">{organizer}</dd>
          </>
        )}
      </dl>

      {attendees.length > 0 && (
        <div>
          <button
            type="button"
            onClick={() => setAttendeesOpen((o) => !o)}
            aria-expanded={attendeesOpen}
            aria-controls="calendar-attendees"
            className="flex w-full items-center gap-1 text-xs font-medium uppercase tracking-wide text-gray-500 hover:text-gray-700 dark:text-gray-400 dark:hover:text-gray-200"
          >
            <span aria-hidden className={attendeesOpen ? "rotate-90 transition-transform" : "transition-transform"}>
              ›
            </span>
            {/* The count stays visible closed, so collapsing hides the names, not the fact that there are seven. */}
            {t("workspace:calAttendees")} ({attendees.length})
          </button>
          {attendeesOpen && (
            <ul id="calendar-attendees" className="mt-1 space-y-0.5">
              {attendees.map((a, i) => (
                <li key={a.email ?? i} className="flex items-center justify-between gap-2">
                  <span className="min-w-0 truncate text-gray-800 dark:text-gray-200">
                    {attendeeName(a)}
                    {a.self ? ` (${t("workspace:calYou")})` : ""}
                  </span>
                  {a.responseStatus && (
                    <span className="shrink-0 text-xs text-gray-500 dark:text-gray-400">
                      {RESP_KEY[a.responseStatus] ? t(`workspace:${RESP_KEY[a.responseStatus]}`) : a.responseStatus}
                    </span>
                  )}
                </li>
              ))}
            </ul>
          )}
        </div>
      )}

      {event.description && (
        <div>
          <div className="mb-1 text-xs font-medium uppercase tracking-wide text-gray-500 dark:text-gray-400">
            {t("workspace:calDescription")}
          </div>
          <p className="max-h-48 overflow-y-auto whitespace-pre-wrap [overflow-wrap:anywhere] text-gray-700 dark:text-gray-300">
            <LinkifiedText text={event.description} />
          </p>
        </div>
      )}

      {/* When the title is shown (Overview) it already links out to Google, so skip the redundant line.
          Skipped for a mirrored Outlook event too: there htmlLink is the meeting's JOIN link, not a calendar
          page, so this label would be plainly wrong - the Join the meeting button is the control for it. */}
      {!showTitle && event.htmlLink && !event.calendarId?.startsWith("outlook:") && (
        <a
          href={event.htmlLink}
          target="_blank"
          rel="noopener noreferrer"
          className="inline-block text-indigo-600 hover:underline dark:text-indigo-400"
        >
          {t("workspace:calOpenInGoogle")}
        </a>
      )}
    </div>
  );
}
