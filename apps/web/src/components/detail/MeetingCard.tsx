import { useTranslation } from "react-i18next";
import type { CalendarEvent, CalendarLink } from "../../lib/types";
import CalendarEventDetails from "../CalendarEventDetails";
import { CalendarIcon } from "./SectionIcons";

/// The hub's calendar card: the meeting this recording came from.
///
/// This content used to sit under the hub as bare, unstyled rows - a naked "Suggested meeting: ... Link"
/// line, or an invite in a plain box - which read as leftovers below the tiles rather than part of the hub.
/// It now wears the same chrome as the tiles, so the hub is a grid of cards all the way down.
///
/// It is not a tile: there is no Meeting section to drill into, and a linked invite (title, time, location,
/// organiser, description) is far too tall for a tile, so it spans the hub's full width beneath the grid.
///
/// Three states: linked (the invite + change/unlink), unlinked with a suggestion from the calendar (accept it,
/// or pick another), and unlinked with none (just pick one). Calendar is personal-only, so the caller hides
/// this entirely inside a shared room.
export default function MeetingCard({
  calendarLink,
  linkedEvent,
  suggestion,
  calendarConnected,
  onLink,
  onAcceptSuggestion,
  onUnlink,
}: {
  calendarLink: CalendarLink | null;
  /// The live event, which may still be loading - fall back to the snapshot stored on the link.
  linkedEvent: CalendarEvent | null | undefined;
  suggestion: CalendarEvent | null | undefined;
  calendarConnected: boolean;
  onLink: () => void;
  onAcceptSuggestion: () => void;
  onUnlink: () => void;
}) {
  const { t } = useTranslation("workspace");

  // Nothing linked and no calendar to link from: there is nothing to say, so don't leave an empty card.
  // (A linked meeting still shows even if the calendar was since disconnected - the snapshot outlives it.)
  if (!calendarLink && !calendarConnected) return null;

  const linked = calendarLink != null;

  return (
    <section className="rounded-2xl border border-gray-200 bg-white p-4 dark:border-gray-700 dark:bg-gray-800">
      <div className="flex items-center gap-2.5">
        <span className="grid h-8 w-8 shrink-0 place-items-center rounded-[9px] bg-emerald-500/15 text-emerald-600 dark:text-emerald-400">
          <CalendarIcon size={18} />
        </span>
        <div className="min-w-0">
          <div className="truncate text-sm font-semibold text-gray-900 dark:text-gray-100">
            {t("hubMeetingTitle")}
          </div>
          <div className="truncate text-[11.5px] text-gray-500 dark:text-gray-400">
            {linked
              ? t("hubMeetingLinkedSubtitle")
              : suggestion
                ? t("hubMeetingSuggestedSubtitle")
                : t("hubMeetingUnlinkedSubtitle")}
          </div>
        </div>

        <div className="ml-auto flex shrink-0 items-center gap-1.5">
          {linked ? (
            <>
              <CardAction label={t("calChangeMeeting")} onClick={onLink} />
              <CardAction label={t("calUnlinkMeeting")} onClick={onUnlink} />
            </>
          ) : (
            <>
              {suggestion && <CardAction label={t("calAcceptSuggestion")} onClick={onAcceptSuggestion} accent />}
              <CardAction label={t("calLinkModalTitle")} onClick={onLink} />
            </>
          )}
        </div>
      </div>

      <div className="mt-3">
        {linked ? (
          <CalendarEventDetails
            showTitle
            event={
              linkedEvent ?? {
                id: calendarLink.eventId,
                summary: calendarLink.summary,
                start: calendarLink.start,
                end: calendarLink.end,
                htmlLink: calendarLink.htmlLink,
              }
            }
          />
        ) : suggestion ? (
          <p className="text-xs text-gray-700 dark:text-gray-300">
            <span className="text-gray-500 dark:text-gray-400">{t("calSuggestedMeeting")}: </span>
            {suggestion.summary || t("meetingUntitled")}
          </p>
        ) : (
          <p className="text-xs text-gray-400 dark:text-gray-500">{t("hubMeetingNone")}</p>
        )}
      </div>
    </section>
  );
}

/// The card's header actions. Mirrors `TileAction` so the meeting card's controls read the same as the
/// tiles' "+ New" / "Run".
function CardAction({ label, onClick, accent }: { label: string; onClick: () => void; accent?: boolean }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={`inline-flex items-center gap-1.5 rounded-lg px-2.5 py-1 text-[11.5px] font-semibold ${
        accent
          ? "bg-blue-600 text-white hover:bg-blue-700"
          : "border border-gray-200 bg-gray-50 text-blue-600 hover:bg-gray-100 dark:border-gray-600 dark:bg-gray-700/60 dark:text-blue-300 dark:hover:bg-gray-700"
      }`}
    >
      {label}
    </button>
  );
}
