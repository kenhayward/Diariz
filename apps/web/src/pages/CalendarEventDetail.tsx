import { useState } from "react";
import { useParams, useNavigate } from "react-router-dom";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { api } from "../lib/api";
import DetailTabs, { type DetailTab } from "../components/DetailTabs";
import CalendarEventDetails from "../components/CalendarEventDetails";
import LinkRecordingModal from "../components/LinkRecordingModal";
import NotesSection from "../components/NotesSection";
import { useRoomBasePath } from "../lib/rooms";
import { meetingJoinUrl } from "../lib/meetingJoin";
import { requestRecording } from "../lib/recordRequest";

/// The centre panel for a Google Calendar meeting that has no recording (reached by clicking an event in
/// the Calendar tab). Shows a single Overview tab with the meeting's details, a "Link a recording" action,
/// and the user's pre-meeting notes (adopted onto the recording automatically once one links to this event).
export default function CalendarEventDetail() {
  const { eventId } = useParams();
  const navigate = useNavigate();
  const { t } = useTranslation(["workspace"]);
  // Keep the jump-to-recording inside the current room. Google Calendar is personal today, so this resolves
  // to "" (top-level route); the prefix keeps it correct should a calendar ever be reached under a room route.
  const basePath = useRoomBasePath();
  const qc = useQueryClient();
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

  // The link that actually joins - not htmlLink, which for a Google event is the Google Calendar page.
  const joinUrl = meetingJoinUrl(event);

  /// Open the meeting and start recording it in one go.
  ///
  /// The window is opened first and synchronously inside the click handler: browsers only allow a popup that
  /// is a direct result of a user gesture, so awaiting anything before it would get it blocked. Recording is
  /// requested after, with whatever audio source the user has already selected.
  ///
  /// The event travels with the request so the recorder can name the take after the invite and, when the
  /// user has asked for it, end the take by itself once the meeting is over. If another recording is already
  /// running the recorder stops and uploads that one first - joining a second meeting says the first is done.
  function joinAndRecord() {
    if (!joinUrl || !event) return;
    window.open(joinUrl, "_blank", "noopener,noreferrer");
    requestRecording({
      calendarEvent: {
        id: event.id,
        summary: event.summary,
        endsAt: event.allDay ? null : event.end,
        calendarId: event.calendarId ?? null,
      },
    });
  }

  // Pre-meeting notes anchored to this event (no recording-clock stamps; no jump target yet).
  const calId = event?.calendarId ?? "primary";
  const { data: eventNotes = [] } = useQuery({
    queryKey: ["event-notes", calId, event?.id],
    queryFn: () => api.listEventNotes(calId, event!.id),
    enabled: Boolean(event),
  });
  const refreshNotes = () => qc.invalidateQueries({ queryKey: ["event-notes", calId, event?.id] });
  const addEventNote = async (text: string) => {
    await api.createEventNotes(calId, event!.id, [{ text }]);
    refreshNotes();
  };
  const editEventNote = async (noteId: string, text: string) => {
    await api.updateEventNote(calId, event!.id, noteId, text);
    refreshNotes();
  };
  const removeEventNote = async (noteId: string) => {
    await api.deleteEventNote(calId, event!.id, noteId);
    refreshNotes();
  };

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
          <div className="mt-4 border-t pt-3 dark:border-gray-700">
            <h3 className="mb-1 text-sm font-medium text-gray-700 dark:text-gray-200">{t("eventNotesTitle")}</h3>
            <p className="mb-2 text-xs text-gray-400 dark:text-gray-500">{t("eventNotesHint")}</p>
            <NotesSection notes={eventNotes} onAdd={addEventNote} onEdit={editEventNote} onDelete={removeEventNote} />
          </div>
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
        <div className="flex shrink-0 items-center gap-2">
          {/* Joining and recording are one action: you are about to be in the meeting, and the reason you
              opened this page is to capture it. Disabled rather than hidden when the invite carries no join
              link, so the control does not appear and disappear between meetings. */}
          <button
            type="button"
            onClick={joinAndRecord}
            disabled={!joinUrl}
            title={joinUrl ? undefined : t("calNoMeetingLink")}
            className="rounded bg-blue-600 px-2 py-1 text-xs font-medium text-white hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-50 dark:bg-blue-600 dark:hover:bg-blue-500"
          >
            {t("calJoinMeeting")}
          </button>
          <button
            type="button"
            onClick={() => setPickOpen(true)}
            className="rounded border px-2 py-1 text-xs hover:bg-gray-50 dark:border-gray-700 dark:text-gray-200 dark:hover:bg-gray-800"
          >
            {t("calLinkRecordingAction")}
          </button>
        </div>
      </div>

      <DetailTabs tabs={tabs} active="overview" onSelect={() => {}} />

      {pickOpen && (
        <LinkRecordingModal
          eventId={event.id}
          calendarId={event.calendarId}
          onClose={() => setPickOpen(false)}
          onLinked={(recId) => {
            setPickOpen(false);
            navigate(`${basePath}/recordings/${recId}`);
          }}
        />
      )}
    </div>
  );
}
