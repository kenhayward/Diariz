import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";
import type { CalendarEvent } from "../lib/types";

vi.mock("../lib/api", () => ({
  api: { getCalendarEvents: vi.fn(), putCalendarLink: vi.fn() },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../lib/api";
import CalendarLinkModal from "./CalendarLinkModal";

const ev = (id: string, summary: string, start: string): CalendarEvent => ({
  id, summary, start, end: start, htmlLink: null,
});

function renderModal(onClose = vi.fn(), onLinked = vi.fn()) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <CalendarLinkModal
        recordingId="rec-1"
        aroundDate="2026-07-02T09:00:00Z"
        onClose={onClose}
        onLinked={onLinked}
      />
    </QueryClientProvider>,
  );
  return { onClose, onLinked };
}

describe("CalendarLinkModal", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    (api.getCalendarEvents as ReturnType<typeof vi.fn>).mockResolvedValue([
      ev("e1", "Planning", "2026-07-02T09:00:00Z"),
      ev("e2", "Retro", "2026-07-03T14:00:00Z"),
    ]);
    (api.putCalendarLink as ReturnType<typeof vi.fn>).mockResolvedValue({
      eventId: "e2", summary: "Retro", start: "2026-07-03T14:00:00Z", end: "2026-07-03T14:00:00Z",
      htmlLink: null, linkedManually: true,
    });
  });

  it("lists meetings in the window and filters by title", async () => {
    renderModal();
    expect(await screen.findByText("Planning")).toBeTruthy();
    expect(screen.getByText("Retro")).toBeTruthy();

    fireEvent.change(screen.getByLabelText("Filter by title"), { target: { value: "retro" } });
    expect(screen.queryByText("Planning")).toBeNull();
    expect(screen.getByText("Retro")).toBeTruthy();
  });

  it("links a chosen meeting (manual) and calls back", async () => {
    const { onClose, onLinked } = renderModal();
    await screen.findByText("Retro");

    fireEvent.click(screen.getByRole("button", { name: /link "retro"/i }));

    await waitFor(() => expect(api.putCalendarLink).toHaveBeenCalledWith("rec-1", "e2", true));
    expect(onLinked).toHaveBeenCalled();
    expect(onClose).toHaveBeenCalled();
  });

  it("moves the window later and refetches", async () => {
    renderModal();
    await screen.findByText("Planning");
    (api.getCalendarEvents as ReturnType<typeof vi.fn>).mockClear();

    fireEvent.click(screen.getByRole("button", { name: /later/i }));
    await waitFor(() => expect(api.getCalendarEvents).toHaveBeenCalled());
  });
});
