import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter, Routes, Route } from "react-router-dom";
import { describe, it, expect, vi, beforeEach } from "vitest";
import type { CalendarEvent } from "../lib/types";

vi.mock("../lib/api", () => ({
  api: { getCalendarEvent: vi.fn(), listRecordings: vi.fn(), putCalendarLink: vi.fn() },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../lib/api";
import CalendarEventDetail from "./CalendarEventDetail";

const event: CalendarEvent = {
  id: "evt1", summary: "Quarterly Planning", start: "2026-07-02T09:00:00Z", end: "2026-07-02T10:00:00Z",
  htmlLink: "https://cal/evt1", location: "Room 4", description: "Agenda", attendees: [],
  calendarId: "team@g", calendarName: "Team", color: "#0B8043",
};

function renderPage() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <MemoryRouter initialEntries={["/calendar-event/evt1"]}>
        <Routes>
          <Route path="/calendar-event/:eventId" element={<CalendarEventDetail />} />
          <Route path="/recordings/:id" element={<div>RECORDING PAGE</div>} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe("CalendarEventDetail", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    (api.getCalendarEvent as ReturnType<typeof vi.fn>).mockResolvedValue(event);
    (api.listRecordings as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "r1", title: "Weekly Standup", name: "Weekly Standup", source: "Microphone", durationMs: 0,
        status: "Transcribed", createdAt: "2026-07-02T09:00:00Z", sectionId: null, sectionName: null,
        hasActions: false, hasAudio: true, calendarEventId: null },
    ]);
    (api.putCalendarLink as ReturnType<typeof vi.fn>).mockResolvedValue({
      eventId: "evt1", summary: null, start: "", end: "", htmlLink: null, linkedManually: true,
    });
  });

  it("shows the event title and its full details", async () => {
    renderPage();
    expect(await screen.findByRole("heading", { name: "Quarterly Planning" })).toBeTruthy();
    expect(screen.getByText("Room 4")).toBeTruthy();
    expect(api.getCalendarEvent).toHaveBeenCalledWith("evt1");
  });

  it("links an existing recording to the meeting and navigates to it", async () => {
    renderPage();
    await screen.findByRole("heading", { name: "Quarterly Planning" });

    fireEvent.click(screen.getByRole("button", { name: /link a recording/i }));
    fireEvent.click(await screen.findByText("Weekly Standup"));

    await waitFor(() => expect(api.putCalendarLink).toHaveBeenCalledWith("r1", "evt1", true, "team@g"));
    expect(await screen.findByText("RECORDING PAGE")).toBeTruthy(); // navigated to the recording
  });

  it("shows an unavailable message when the event is gone", async () => {
    (api.getCalendarEvent as ReturnType<typeof vi.fn>).mockRejectedValue(new Error("404"));
    renderPage();
    expect(await screen.findByText(/no longer available/i)).toBeTruthy();
  });
});
