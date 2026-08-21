/// Finding the "join this meeting" link on a calendar event.
///
/// Not the same thing as `htmlLink`, and the difference matters. For a **mirrored Outlook** event `htmlLink`
/// IS the join URL - a local Outlook appointment has no web permalink, so the desktop reader puts the
/// Teams/Zoom link there. For a **Google** event `htmlLink` is the Google Calendar page for the invite, which
/// is emphatically not a join link; treating it as one would send someone to a web calendar when they
/// expected to be in their meeting.
///
/// Everything else falls back to scanning the location and description, which is where a pasted invite puts
/// its join link and what the app already relies on when it linkifies those fields.
///
/// **Why the scan knows the providers by name.** The original rule was "first http(s) URL wins", which works
/// for Zoom because a Zoom invite puts the join URL in the *location* and nothing else competes with it. A
/// Teams invite is the opposite case: the location reads "Microsoft Teams Meeting" (no URL at all) and the
/// body carries five links, of which only one joins - the rest are a help article, a dial-in page, the
/// organizer's meeting options, and whatever the organizer pasted into the agenda. First-URL-wins picked the
/// agenda link or the help article, so the button either did nothing useful or, with no URL anywhere, stayed
/// disabled. Recognising the provider is the only way to tell those five apart.

import { OUTLOOK_CALENDAR_PREFIX } from "./outlookSync";
import type { CalendarEvent } from "./types";

/// URLs that join a meeting on a service we know. Matched as a **substring** of the whole URL, not anchored
/// to the host, so a link a mail gateway has rewritten (Proofpoint's `urldefense.com/v3/__https://...`) is
/// still recognised - and handing back the wrapped form is right, since the wrapper redirects to the meeting.
const JOIN_PATTERNS: RegExp[] = [
  /teams\.microsoft\.com\/l\/meetup-join\//i, // Teams, the long-standing form
  /teams\.microsoft\.com\/meet\//i, // Teams, the short form it hands out today
  /teams\.microsoft\.us\/l\/meetup-join\//i, // Teams for US government tenants
  /teams\.live\.com\/meet\//i, // Teams on a personal account
  /zoom\.us\/(j|w|s|my)\//i, // Zoom, including a vanity host like 3ds.zoom.us
  /meet\.google\.com\//i,
  /\.webex\.com\/(meet|join|m)\//i,
  /whereby\.com\//i,
  /meet\.goto\.com\//i,
  /gotomeet\.me\//i,
];

/// Links that sit *next to* a join link in an invite and must never be mistaken for one. Offering a Join
/// button that opens a help article or a dial-in page is worse than offering none: the user finds out it was
/// not a join link only once the meeting has started.
const NOT_JOIN_PATTERNS: RegExp[] = [
  /aka\.ms\/JoinTeamsMeeting/i, // "Need help?" article, and it precedes the real link in every Teams body
  /\/\/dialin\./i, // phone numbers - Teams, Skype for Business and Webex all publish one
  /teams\.microsoft\.com\/meetingOptions/i, // the organizer's settings page
];

/// Every http(s) URL in a string, in order, trimmed of trailing punctuation that commonly follows a pasted
/// link. The character class stops at `<` and `>` so a plain-text invite's `Join now <https://...>` and an
/// HTML one's `href="https://..."` both yield the bare URL.
function urls(text: string | null | undefined): string[] {
  if (!text) return [];
  return (text.match(/https?:\/\/[^\s<>"')\]]+/gi) ?? []).map((u) => u.replace(/[.,;:]+$/, ""));
}

const matches = (url: string, patterns: RegExp[]) => patterns.some((p) => p.test(url));

/// The URL that joins this meeting, or null when there is none (which is what disables the Join control).
export function meetingJoinUrl(
  event: Pick<CalendarEvent, "htmlLink" | "location" | "description" | "calendarId"> | null | undefined,
): string | null {
  if (!event) return null;

  // Outlook: htmlLink is already the join link, and is the one Outlook itself would open - so it wins. It is
  // null more often than you would expect, though: Outlook does not populate its OnlineMeetingUrl property
  // for a Teams appointment, and that appointment's location is the words "Microsoft Teams Meeting" rather
  // than a URL. So fall through to the invite text rather than giving up, which is the whole fix here.
  if (event.calendarId?.startsWith(OUTLOOK_CALENDAR_PREFIX) && event.htmlLink) return event.htmlLink;

  // A Google htmlLink is the calendar page, never a join link, so it is deliberately not a candidate.
  // Location first, then description: a Zoom invite puts the join URL in the location, and an organizer who
  // pastes one there means it.
  const candidates = [...urls(event.location), ...urls(event.description)].filter(
    (u) => !matches(u, NOT_JOIN_PATTERNS),
  );

  // A provider we recognise beats position, because in a Teams body the join link is never the first URL.
  // Anything else keeps the old first-URL rule, so a service this list has never heard of still joins.
  return candidates.find((u) => matches(u, JOIN_PATTERNS)) ?? candidates[0] ?? null;
}
