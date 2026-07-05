import { useState } from "react";
import { useParams, useNavigate } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api } from "../lib/api";
import DetailTabs, { type DetailTab } from "../components/DetailTabs";
import CalendarEventDetails from "../components/CalendarEventDetails";
import LinkRecordingModal from "../components/LinkRecordingModal";

/// The centre panel for a Google Calendar meeting that has no recording (reached by clicking an event in
/// the Calendar tab). Shows a single Overview tab with the meeting's details and a "Link a recording"
/// action to attach an existing recording to it.
export default function CalendarEventDetail() {
  const { eventId } = useParams();
  const navigate = useNavigate();
  const { t } = useTranslation(["workspace"]);
  const [pickOpen, setPickOpen] = useState(false);

  const {
    data: event,
    isLoading,
    isError,
  } = useQuery({
    queryKey: ["calendar-event", eventId],
    queryFn: () => api.getCalendarEvent(eventId!),
    enabled: Boolean(eventId),
    retry: false,
  });

  if (isLoading) {
    return <p className="p-2 text-sm text-gray-500 dark:text-gray-400">{t("calEventLoading")}</p>;
  }
  if (isError || !event) {
    return <p className="p-2 text-sm text-gray-500 dark:text-gray-400">{t("calEventUnavailable")}</p>;
  }

  const tabs: DetailTab[] = [
    {
      key: "overview",
      label: t("detailTabOverview"),
      content: (
        <div className="px-4 pb-4">
          <CalendarEventDetails event={event} />
        </div>
      ),
    },
  ];

  return (
    <div>
      <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
        <h1 className="min-w-0 truncate text-lg font-semibold text-gray-900 dark:text-gray-100">
          {event.summary || t("calUntitledEvent")}
        </h1>
        <button
          type="button"
          onClick={() => setPickOpen(true)}
          className="shrink-0 rounded border px-2 py-1 text-xs hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
        >
          {t("calLinkRecordingAction")}
        </button>
      </div>

      <DetailTabs tabs={tabs} active="overview" onSelect={() => {}} />

      {pickOpen && (
        <LinkRecordingModal
          eventId={event.id}
          calendarId={event.calendarId}
          onClose={() => setPickOpen(false)}
          onLinked={(recId) => {
            setPickOpen(false);
            navigate(`/recordings/${recId}`);
          }}
        />
      )}
    </div>
  );
}
