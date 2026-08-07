/// Finding the "join this meeting" link on a calendar event.
///
/// Not the same thing as `htmlLink`, and the difference matters. For a **mirrored Outlook** event `htmlLink`
/// IS the join URL - a local Outlook appointment has no web permalink, so the desktop reader puts the
/// Teams/Zoom link there and leaves it null when the meeting is not an online one. For a **Google** event
/// `htmlLink` is the Google Calendar page for the invite, which is emphatically not a join link; treating it
/// as one would send someone to a web calendar when they expected to be in their meeting.
///
/// So Google events fall back to scanning the location and description, which is where a pasted invite puts
/// its join link and what the app already relies on when it linkifies those fields.

import { OUTLOOK_CALENDAR_PREFIX } from "./outlookSync";
import type { CalendarEvent } from "./types";

/// First http(s) URL in a string, trimmed of trailing punctuation that commonly follows a pasted link.
function firstUrl(text: string | null | undefined): string | null {
  if (!text) return null;
  const match = text.match(/https?:\/\/[^\s<>"')\]]+/i);
  if (!match) return null;
  return match[0].replace(/[.,;:]+$/, "");
}

/// The URL that joins this meeting, or null when there is none (which is what disables the Join control).
export function meetingJoinUrl(event: Pick<CalendarEvent, "htmlLink" | "location" | "description" | "calendarId"> | null | undefined): string | null {
  if (!event) return null;

  // Outlook: htmlLink is already the join link, and is only populated when the meeting has one.
  if (event.calendarId?.startsWith(OUTLOOK_CALENDAR_PREFIX)) return event.htmlLink || null;

  // Everything else: the join link, if there is one, lives in the invite text.
  return firstUrl(event.location) ?? firstUrl(event.description);
}
