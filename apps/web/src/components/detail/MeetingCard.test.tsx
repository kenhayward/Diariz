import { render, screen, fireEvent } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";
import type { ReactElement } from "react";
import MeetingCard from "./MeetingCard";
import type { CalendarEvent, CalendarLink } from "../../lib/types";
import { api } from "../../lib/api";

// MeetingCard's linked branch mounts SeriesRecordings, which fetches over ../../lib/api and needs a
// QueryClientProvider in the tree.
vi.mock("../../lib/api", () => ({
  api: { getSeriesRecordings: vi.fn().mockResolvedValue([]) },
}));

function renderWithClient(ui: ReactElement) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={qc}>{ui}</QueryClientProvider>);
}

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
  vi.mocked(api.getSeriesRecordings).mockClear();
});

describe("MeetingCard", () => {
  it("shows the linked meeting's details, with change and unlink actions", () => {
    renderWithClient(<MeetingCard calendarLink={link} linkedEvent={event} suggestion={null} calendarConnected {...h} />);
    expect(screen.getByText("QnR Competences merging to one")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Change meeting" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Unlink meeting" })).toBeTruthy();
  });

  it("unlinks the meeting", () => {
    renderWithClient(<MeetingCard calendarLink={link} linkedEvent={event} suggestion={null} calendarConnected {...h} />);
    fireEvent.click(screen.getByRole("button", { name: "Unlink meeting" }));
    expect(h.onUnlink).toHaveBeenCalled();
  });

  it("falls back to the stored snapshot when the live event hasn't loaded", () => {
    renderWithClient(<MeetingCard calendarLink={link} linkedEvent={null} suggestion={null} calendarConnected {...h} />);
    expect(screen.getByText("QnR Competences merging to one")).toBeTruthy();
  });

  it("offers the suggested meeting when the recording isn't linked yet", () => {
    renderWithClient(<MeetingCard calendarLink={null} linkedEvent={null} suggestion={suggestion} calendarConnected {...h} />);
    expect(screen.getByText(/Chris Not In/)).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Link" }));
    expect(h.onAcceptSuggestion).toHaveBeenCalled();
  });

  it("always offers to pick a meeting by hand when unlinked", () => {
    renderWithClient(<MeetingCard calendarLink={null} linkedEvent={null} suggestion={null} calendarConnected {...h} />);
    fireEvent.click(screen.getByRole("button", { name: "Link a meeting" }));
    expect(h.onLink).toHaveBeenCalled();
  });

  it("renders nothing when the calendar isn't connected and nothing is linked - there is no card to show", () => {
    const { container } = renderWithClient(
      <MeetingCard calendarLink={null} linkedEvent={null} suggestion={null} calendarConnected={false} {...h} />,
    );
    expect(container.firstChild).toBeNull();
  });

  it("still shows a linked meeting even if the calendar has since been disconnected", () => {
    renderWithClient(<MeetingCard calendarLink={link} linkedEvent={event} suggestion={null} calendarConnected={false} {...h} />);
    expect(screen.getByText("QnR Competences merging to one")).toBeTruthy();
  });

  it("does not fetch other recordings of the series when the linked event isn't recurring", () => {
    renderWithClient(<MeetingCard calendarLink={link} linkedEvent={event} suggestion={null} calendarConnected {...h} />);
    expect(api.getSeriesRecordings).not.toHaveBeenCalled();
  });

  it("fetches other recordings of the series when the linked event is recurring, using the stored eventId", () => {
    renderWithClient(
      <MeetingCard calendarLink={link} linkedEvent={{ ...event, recurring: true }} suggestion={null} calendarConnected {...h} />,
    );
    expect(api.getSeriesRecordings).toHaveBeenCalledWith(link.eventId);
  });

  it("does not fetch other recordings while the live event hasn't loaded yet", () => {
    renderWithClient(<MeetingCard calendarLink={link} linkedEvent={null} suggestion={null} calendarConnected {...h} />);
    expect(api.getSeriesRecordings).not.toHaveBeenCalled();
  });
});
