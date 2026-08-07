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
