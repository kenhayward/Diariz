import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import MeetingCard from "./MeetingCard";
import type { CalendarEvent, CalendarLink } from "../../lib/types";

const link: CalendarLink = {
  eventId: "evt1",
  calendarId: "work@g",
  summary: "QnR Competences merging to one",
  start: "2026-06-30T19:00:00Z",
  end: "2026-06-30T19:30:00Z",
  htmlLink: "https://cal/evt1",
} as unknown as CalendarLink;

const event: CalendarEvent = {
  id: "evt1",
  summary: "QnR Competences merging to one",
  start: "2026-06-30T19:00:00Z",
  end: "2026-06-30T19:30:00Z",
  htmlLink: "https://cal/evt1",
  description: "Apologies it is late for Europe.",
} as unknown as CalendarEvent;

const suggestion = { id: "evt2", summary: "Chris Not In" } as unknown as CalendarEvent;

const handlers = () => ({
  onLink: vi.fn(),
  onAcceptSuggestion: vi.fn(),
  onUnlink: vi.fn(),
});

let h: ReturnType<typeof handlers>;
beforeEach(() => {
  h = handlers();
});

describe("MeetingCard", () => {
  it("shows the linked meeting's details, with change and unlink actions", () => {
    render(<MeetingCard calendarLink={link} linkedEvent={event} suggestion={null} calendarConnected {...h} />);
    expect(screen.getByText("QnR Competences merging to one")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Change meeting" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Unlink meeting" })).toBeTruthy();
  });

  it("unlinks the meeting", () => {
    render(<MeetingCard calendarLink={link} linkedEvent={event} suggestion={null} calendarConnected {...h} />);
    fireEvent.click(screen.getByRole("button", { name: "Unlink meeting" }));
    expect(h.onUnlink).toHaveBeenCalled();
  });

  it("falls back to the stored snapshot when the live event hasn't loaded", () => {
    render(<MeetingCard calendarLink={link} linkedEvent={null} suggestion={null} calendarConnected {...h} />);
    expect(screen.getByText("QnR Competences merging to one")).toBeTruthy();
  });

  it("offers the suggested meeting when the recording isn't linked yet", () => {
    render(<MeetingCard calendarLink={null} linkedEvent={null} suggestion={suggestion} calendarConnected {...h} />);
    expect(screen.getByText(/Chris Not In/)).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Link" }));
    expect(h.onAcceptSuggestion).toHaveBeenCalled();
  });

  it("always offers to pick a meeting by hand when unlinked", () => {
    render(<MeetingCard calendarLink={null} linkedEvent={null} suggestion={null} calendarConnected {...h} />);
    fireEvent.click(screen.getByRole("button", { name: "Link a meeting" }));
    expect(h.onLink).toHaveBeenCalled();
  });

  it("renders nothing when the calendar isn't connected and nothing is linked - there is no card to show", () => {
    const { container } = render(
      <MeetingCard calendarLink={null} linkedEvent={null} suggestion={null} calendarConnected={false} {...h} />,
    );
    expect(container.firstChild).toBeNull();
  });

  it("still shows a linked meeting even if the calendar has since been disconnected", () => {
    render(<MeetingCard calendarLink={link} linkedEvent={event} suggestion={null} calendarConnected={false} {...h} />);
    expect(screen.getByText("QnR Competences merging to one")).toBeTruthy();
  });
});
