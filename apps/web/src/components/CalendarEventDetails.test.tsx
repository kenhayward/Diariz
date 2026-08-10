import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect } from "vitest";
import CalendarEventDetails from "./CalendarEventDetails";
import type { CalendarEvent } from "../lib/types";

const fullEvent: CalendarEvent = {
  id: "e1",
  summary: "Quarterly Planning",
  start: "2026-07-02T09:00:00Z",
  end: "2026-07-02T10:00:00Z",
  htmlLink: "https://cal/e1",
  location: "Room 4",
  description: "Agenda:\n- Budget\n- Roadmap",
  organizer: { email: "boss@x.test", displayName: "The Boss", responseStatus: null, organizer: true, self: false },
  attendees: [
    { email: "boss@x.test", displayName: "The Boss", responseStatus: "accepted", organizer: true, self: false },
    { email: "me@x.test", displayName: null, responseStatus: "needsAction", organizer: false, self: true },
  ],
  calendarName: "Team", color: "#0B8043",
};

describe("CalendarEventDetails", () => {
  it("renders location, organiser, attendees with response, and description", () => {
    render(<CalendarEventDetails event={fullEvent} />);

    expect(screen.getByText("Team")).toBeTruthy(); // the calendar name (with a colour swatch)
    expect(screen.getByText("Room 4")).toBeTruthy();
    expect(screen.getByText("Attendees (2)")).toBeTruthy();
    expect(screen.getByText(/Budget/)).toBeTruthy(); // description body

    fireEvent.click(screen.getByRole("button", { name: /Attendees \(2\)/ }));
    expect(screen.getByText("Going")).toBeTruthy(); // accepted -> Going
    expect(screen.getByText("No response")).toBeTruthy(); // needsAction
    expect(screen.getByText(/me@x\.test \(you\)/)).toBeTruthy(); // self, falls back to email
    // "The Boss" appears twice: as the organiser row and as an attendee (organiser is also an attendee).
    expect(screen.getAllByText("The Boss")).toHaveLength(2);

    const link = screen.getByRole("link", { name: "Open in Google Calendar" });
    expect(link.getAttribute("href")).toBe("https://cal/e1");
  });

  it("omits optional sections when the event is bare", () => {
    const bare: CalendarEvent = {
      id: "e2", summary: "Quick sync", start: "2026-07-02T09:00:00Z", end: "2026-07-02T09:15:00Z", htmlLink: null,
    };
    render(<CalendarEventDetails event={bare} />);

    expect(screen.getByText("When")).toBeTruthy(); // the one always-present row
    expect(screen.queryByText("Where")).toBeNull();
    expect(screen.queryByText(/^Attendees/)).toBeNull();
    expect(screen.queryByText("Description")).toBeNull();
    expect(screen.queryByRole("link", { name: "Open in Google Calendar" })).toBeNull();
  });

  it("with showTitle, shows the title as the Google link and drops the separate line", () => {
    render(<CalendarEventDetails event={fullEvent} showTitle />);

    // The title itself is the link out to Google Calendar...
    const title = screen.getByRole("link", { name: "Quarterly Planning" });
    expect(title.getAttribute("href")).toBe("https://cal/e1");
    // ...so the separate "Open in Google Calendar" line is gone.
    expect(screen.queryByRole("link", { name: "Open in Google Calendar" })).toBeNull();
  });

  // Attendees is the longest block in the panel and is rarely what you opened the event for, so it starts
  // closed - with the count still on show, so collapsing hides the names, not the fact that there are two.
  it("keeps attendees collapsed until asked, and shows the count either way", () => {
    render(<CalendarEventDetails event={fullEvent} />);

    const toggle = screen.getByRole("button", { name: /Attendees \(2\)/ });
    expect(toggle.getAttribute("aria-expanded")).toBe("false");
    expect(screen.queryByText("Going")).toBeNull();

    fireEvent.click(toggle);

    expect(toggle.getAttribute("aria-expanded")).toBe("true");
    expect(screen.getByText("Going")).toBeTruthy();
    expect(screen.getByText("No response")).toBeTruthy();
  });

  it("marks an event that repeats", () => {
    render(<CalendarEventDetails event={{ ...fullEvent, recurring: true }} />);
    expect(screen.getByText("Repeats")).toBeTruthy();
  });

  it("says nothing about repetition for a one-off", () => {
    render(<CalendarEventDetails event={fullEvent} />);
    expect(screen.queryByText("Repeats")).toBeNull();
  });
});
