import { describe, it, expect } from "vitest";
import { meetingJoinUrl } from "./meetingJoin";

const ev = (over: Partial<Parameters<typeof meetingJoinUrl>[0]> = {}) => ({
  htmlLink: null,
  location: null,
  description: null,
  calendarId: null,
  ...over,
});

describe("meetingJoinUrl", () => {
  /// The distinction the whole helper exists for: a mirrored Outlook event's htmlLink IS the join link (the
  /// desktop reader puts the Teams/Zoom URL there because a local appointment has no web permalink), whereas
  /// a Google event's htmlLink is the Google Calendar page. Treating the latter as a join link would send
  /// someone to a web calendar when they expected to be in their meeting.
  it("uses htmlLink for a mirrored Outlook event", () => {
    expect(
      meetingJoinUrl(ev({ calendarId: "outlook:2f1c...", htmlLink: "https://teams.example/join/abc" })),
    ).toBe("https://teams.example/join/abc");
  });

  it("never treats a Google htmlLink as a join link", () => {
    expect(
      meetingJoinUrl(ev({ calendarId: "primary", htmlLink: "https://calendar.google.com/event?eid=xyz" })),
    ).toBeNull();
  });

  it("finds a join link in a Google event's location", () => {
    expect(
      meetingJoinUrl(ev({ calendarId: "primary", location: "https://meet.google.com/abc-defg-hij" })),
    ).toBe("https://meet.google.com/abc-defg-hij");
  });

  it("falls back to the description when the location has none", () => {
    expect(
      meetingJoinUrl(
        ev({ calendarId: "primary", location: "Room 3", description: "Dial in: https://zoom.us/j/123456" }),
      ),
    ).toBe("https://zoom.us/j/123456");
  });

  it("prefers the location over the description", () => {
    expect(
      meetingJoinUrl(
        ev({ calendarId: "primary", location: "https://meet.google.com/aaa", description: "https://zoom.us/j/1" }),
      ),
    ).toBe("https://meet.google.com/aaa");
  });

  /// Pasted links routinely end up butted against punctuation; carrying it into the URL breaks the join.
  it("trims trailing punctuation from a pasted link", () => {
    expect(meetingJoinUrl(ev({ calendarId: "primary", description: "Join at https://zoom.us/j/123." }))).toBe(
      "https://zoom.us/j/123",
    );
    expect(meetingJoinUrl(ev({ calendarId: "primary", description: "(https://zoom.us/j/456)" }))).toBe(
      "https://zoom.us/j/456",
    );
  });

  it("returns null when there is nothing to join - which is what disables the button", () => {
    expect(meetingJoinUrl(ev({ calendarId: "primary", location: "Room 3", description: "Bring coffee" }))).toBeNull();
    expect(meetingJoinUrl(ev({ calendarId: "outlook:2f1c...", htmlLink: null }))).toBeNull();
    expect(meetingJoinUrl(null)).toBeNull();
    expect(meetingJoinUrl(undefined)).toBeNull();
  });
});

/// A Teams invite is the case the plain "first URL wins" rule cannot handle, and the reason the helper now
/// knows the providers by name. The body of a real one (these fixtures are shaped after invites from the
/// user's own mailbox) carries, in this order: unrelated agenda links, the "Need help?" aka.ms article, the
/// actual join link, a dial-in number page, and the organizer's meeting-options page. Four of those five are
/// URLs you must not send someone to when they press Join.
describe("meetingJoinUrl - Microsoft Teams", () => {
  const TEAMS_BODY = [
    "Agenda: https://confluence.example.org/display/BRR/Quality",
    "________________________________________________________________________________",
    "Microsoft Teams Need help? <https://aka.ms/JoinTeamsMeeting?omkt=en-US>",
    "Join the meeting now <https://teams.microsoft.com/l/meetup-join/19%3ameeting_ZjE1ZDJkY2M@thread.v2/0?context=%7b%22Tid%22%3a%22abc%22%7d>",
    "Meeting ID: 980 737 257",
    "Dial in by phone <https://dialin.teams.microsoft.com/6071d915?id=980737257>",
    "For organizers: Meeting options <https://teams.microsoft.com/meetingOptions/?organizerId=4857fc1c>",
  ].join("\n");

  it("finds the join link in a Teams invite body, past the agenda and help links that precede it", () => {
    expect(meetingJoinUrl(ev({ calendarId: "primary", location: "Microsoft Teams Meeting", description: TEAMS_BODY })))
      .toBe(
        "https://teams.microsoft.com/l/meetup-join/19%3ameeting_ZjE1ZDJkY2M@thread.v2/0?context=%7b%22Tid%22%3a%22abc%22%7d",
      );
  });

  /// The short link Teams hands out today, and the personal-account equivalent.
  it("recognises the teams.microsoft.com/meet short form", () => {
    expect(meetingJoinUrl(ev({ calendarId: "primary", location: "https://teams.microsoft.com/meet/1234567890?p=Ab3" })))
      .toBe("https://teams.microsoft.com/meet/1234567890?p=Ab3");
    expect(meetingJoinUrl(ev({ calendarId: "primary", description: "https://teams.live.com/meet/9876543210" })))
      .toBe("https://teams.live.com/meet/9876543210");
  });

  /// Mail security rewrites every link in the body. The wrapper still redirects to the meeting, so the right
  /// answer is to hand back the wrapped URL - but only after recognising which of the wrapped links is the
  /// join one, which means matching the provider inside the wrapper.
  it("recognises a Teams link that a mail gateway has rewritten", () => {
    const wrapped =
      "https://urldefense.com/v3/__https://teams.microsoft.com/l/meetup-join/19*3ameeting_ZjE1__;!!FbCVDoc3r24$";
    const body = `Need help? https://urldefense.com/v3/__https://aka.ms/JoinTeamsMeeting?omkt=en-US__;!!FbC$\nJoin now ${wrapped}`;
    expect(meetingJoinUrl(ev({ calendarId: "primary", description: body }))).toBe(wrapped);
  });

  /// Offering a Join button that opens a help article, a dial-in page or the organizer's settings is worse
  /// than offering none: the user finds out it was not a join link only after the meeting has started.
  it("never offers a Teams help, dial-in or meeting-options link as the join link", () => {
    const body = [
      "Microsoft Teams Need help? <https://aka.ms/JoinTeamsMeeting?omkt=en-GB>",
      "Dial in <https://dialin.teams.microsoft.com/usp/pstnconferencing>",
      "Meeting options <https://teams.microsoft.com/meetingOptions/?organizerId=1>",
    ].join("\n");
    expect(meetingJoinUrl(ev({ calendarId: "primary", location: "Microsoft Teams Meeting", description: body })))
      .toBeNull();
  });
});

/// The mirrored-Outlook half of the same gap. Outlook's own OnlineMeetingUrl property is not populated for a
/// Teams appointment, and its Location reads "Microsoft Teams Meeting" rather than a URL - so the desktop
/// reader has nothing to send and htmlLink arrives null. Zoom invites put the URL straight in the Location,
/// which is the whole reason Zoom worked and Teams did not.
describe("meetingJoinUrl - mirrored Outlook without a direct join link", () => {
  it("falls back to the invite text when Outlook supplied no join link", () => {
    expect(
      meetingJoinUrl(
        ev({
          calendarId: "outlook:2f1c...",
          htmlLink: null,
          location: "Microsoft Teams Meeting",
          description: "Join the meeting now <https://teams.microsoft.com/l/meetup-join/19%3ameeting_abc>",
        }),
      ),
    ).toBe("https://teams.microsoft.com/l/meetup-join/19%3ameeting_abc");
  });

  /// The direct link stays authoritative - it is the one Outlook itself would open.
  it("still prefers the link Outlook supplied", () => {
    expect(
      meetingJoinUrl(
        ev({
          calendarId: "outlook:2f1c...",
          htmlLink: "https://3ds.zoom.us/j/84082224069",
          description: "https://teams.microsoft.com/l/meetup-join/19%3ameeting_abc",
        }),
      ),
    ).toBe("https://3ds.zoom.us/j/84082224069");
  });
});

/// Knowing the providers must not narrow what the button works for: a meeting on a service the list has
/// never heard of still joins on the old first-URL rule.
describe("meetingJoinUrl - providers the list does not know", () => {
  it("still falls back to the first URL in the invite", () => {
    expect(meetingJoinUrl(ev({ calendarId: "primary", location: "https://vc.example.com/room/42" }))).toBe(
      "https://vc.example.com/room/42",
    );
  });

  it("prefers a known provider's link over an unrelated URL that appears earlier", () => {
    expect(
      meetingJoinUrl(
        ev({ calendarId: "primary", description: "Agenda: https://wiki.example.com/x\nJoin https://zoom.us/j/99" }),
      ),
    ).toBe("https://zoom.us/j/99");
  });
});

/// The list is not Teams-only. Each of these joins a meeting on a service the app should know, and each is
/// checked with an unrelated link ahead of it - an agenda or a wiki page is exactly what an organizer pastes
/// above the join block, and position alone would hand back the wrong one.
describe("meetingJoinUrl - the rest of the provider list", () => {
  const cases: [string, string][] = [
    ["Google Meet", "https://meet.google.com/abc-defg-hij"],
    ["Zoom on a vanity host", "https://3ds.zoom.us/j/84082224069?pwd=y3tBMEPbw75Xso"],
    ["Zoom personal room", "https://zoom.us/my/jane.doe"],
    ["Zoom webinar", "https://zoom.us/w/81898223973"],
    ["Webex", "https://acme.webex.com/meet/jane.doe"],
    ["Webex join", "https://acme.webex.com/join/jane.doe"],
    ["Whereby", "https://whereby.com/jane-doe"],
    ["GoToMeeting", "https://meet.goto.com/JaneDoe"],
    ["GoToMeeting personal link", "https://www.gotomeet.me/JaneDoe"],
    ["Teams on a US government tenant", "https://teams.microsoft.us/l/meetup-join/19%3ameeting_abc"],
  ];

  it.each(cases)("finds a %s link past an unrelated one", (_name, url) => {
    expect(meetingJoinUrl(ev({ calendarId: "primary", description: `Agenda: https://wiki.example.com/x\n${url}` })))
      .toBe(url);
  });
});

/// Dial-in pages are not Teams-specific, and offering one as the join link is the same mistake as offering
/// the Teams help article: the user only finds out it was not a join link once the meeting has started.
describe("meetingJoinUrl - dial-in pages", () => {
  it("never offers a dial-in page as the join link", () => {
    expect(meetingJoinUrl(ev({ calendarId: "primary", description: "Phone: https://dialin.contoso.com" }))).toBeNull();
  });
});
