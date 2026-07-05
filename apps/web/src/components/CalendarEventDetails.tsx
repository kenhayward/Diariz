import { useTranslation } from "react-i18next";
import type { CalendarEvent, CalendarAttendee } from "../lib/types";
import { formatLongDate, formatTimeHm } from "../lib/format";

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
export default function CalendarEventDetails({ event }: { event: CalendarEvent }) {
  const { t, i18n } = useTranslation(["workspace"]);
  const when = `${formatLongDate(event.start, i18n.language)} · ${formatTimeHm(event.start)} - ${formatTimeHm(event.end)}`;
  const organizer = event.organizer ? attendeeName(event.organizer) : "";
  const attendees = event.attendees ?? [];

  return (
    <div className="space-y-3 text-sm">
      {event.calendarName && (
        <div className="flex items-center gap-2 text-xs text-gray-500 dark:text-gray-400">
          <span
            className="inline-block h-2.5 w-2.5 shrink-0 rounded-full"
            style={{ backgroundColor: event.color ?? "#0B8043" }}
            aria-hidden
          />
          <span className="truncate">{event.calendarName}</span>
        </div>
      )}
      <dl className="grid grid-cols-[max-content_1fr] gap-x-4 gap-y-1">
        <dt className="text-gray-500 dark:text-gray-400">{t("workspace:calWhen")}</dt>
        <dd className="text-gray-800 dark:text-gray-200">{when}</dd>
        {event.location && (
          <>
            <dt className="text-gray-500 dark:text-gray-400">{t("workspace:calWhere")}</dt>
            <dd className="break-words text-gray-800 dark:text-gray-200">{event.location}</dd>
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
          <div className="mb-1 text-xs font-medium uppercase tracking-wide text-gray-500 dark:text-gray-400">
            {t("workspace:calAttendees")} ({attendees.length})
          </div>
          <ul className="space-y-0.5">
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
        </div>
      )}

      {event.description && (
        <div>
          <div className="mb-1 text-xs font-medium uppercase tracking-wide text-gray-500 dark:text-gray-400">
            {t("workspace:calDescription")}
          </div>
          <p className="max-h-48 overflow-y-auto whitespace-pre-wrap break-words text-gray-700 dark:text-gray-300">
            {event.description}
          </p>
        </div>
      )}

      {event.htmlLink && (
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
