import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";
import type { RecordingSummary } from "../lib/types";

vi.mock("../lib/api", () => ({
  api: { listRecordings: vi.fn(), putCalendarLink: vi.fn() },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../lib/api";
import LinkRecordingModal from "./LinkRecordingModal";

const rec = (id: string, name: string): RecordingSummary => ({
  id, title: name, name, source: "Microphone", durationMs: 0, status: "Transcribed",
  createdAt: "2026-07-02T09:00:00Z", sectionId: null, sectionName: null, hasActions: false, hasAudio: true,
  calendarEventId: null,
});

function renderModal(onLinked = vi.fn(), onClose = vi.fn()) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <LinkRecordingModal eventId="evt1" onClose={onClose} onLinked={onLinked} />
    </QueryClientProvider>,
  );
  return { onLinked, onClose };
}

describe("LinkRecordingModal", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    (api.listRecordings as ReturnType<typeof vi.fn>).mockResolvedValue([rec("a", "Weekly Standup"), rec("b", "Retro")]);
    (api.putCalendarLink as ReturnType<typeof vi.fn>).mockResolvedValue({
      eventId: "evt1", summary: null, start: "", end: "", htmlLink: null, linkedManually: true,
    });
  });

  it("lists the user's recordings and filters them", async () => {
    renderModal();
    expect(await screen.findByText("Weekly Standup")).toBeTruthy();
    expect(screen.getByText("Retro")).toBeTruthy();

    fireEvent.change(screen.getByLabelText("Filter recordings"), { target: { value: "retro" } });
    expect(screen.queryByText("Weekly Standup")).toBeNull();
    expect(screen.getByText("Retro")).toBeTruthy();
  });

  it("links the chosen recording to the event (manual) and calls back", async () => {
    const { onLinked } = renderModal();
    fireEvent.click(await screen.findByText("Retro"));

    await waitFor(() => expect(api.putCalendarLink).toHaveBeenCalledWith("b", "evt1", true));
    expect(onLinked).toHaveBeenCalledWith("b");
  });
});
