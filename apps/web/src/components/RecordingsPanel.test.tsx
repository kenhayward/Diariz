import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter } from "react-router-dom";
import { describe, it, expect, vi, beforeEach } from "vitest";
import type { RecordingSummary } from "../lib/types";

vi.mock("../lib/signalr", () => ({
  createHub: () => ({ start: () => Promise.resolve(), stop: () => Promise.resolve(), on: () => {} }),
}));

vi.mock("../lib/api", () => ({
  api: {
    listRecordings: vi.fn(),
    summarize: vi.fn(),
    deleteRecording: vi.fn(),
    renameRecording: vi.fn(),
    audioUrl: vi.fn(),
    downloadTranscript: vi.fn(),
    downloadAudio: vi.fn(),
  },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../lib/api";
import RecordingsPanel from "./RecordingsPanel";

const rec: RecordingSummary = {
  id: "rec-1",
  title: "Mic 6/26/2026",
  name: "Weekly Standup",
  source: "System",
  durationMs: 9000,
  status: "Transcribed",
  createdAt: new Date("2026-06-26T12:00:00Z").toISOString(),
};

function renderList() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter>
        <RecordingsPanel />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe("RecordingsPanel", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    (api.listRecordings as ReturnType<typeof vi.fn>).mockResolvedValue([rec]);
    (api.summarize as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    (api.deleteRecording as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
  });

  it("shows the name as primary and the source label as secondary", async () => {
    renderList();
    expect(await screen.findByText("Weekly Standup")).toBeTruthy();
    expect(screen.getByText(/System audio/)).toBeTruthy();
  });

  it("Summarise action calls the API", async () => {
    renderList();
    await screen.findByText("Weekly Standup");
    fireEvent.click(screen.getByRole("button", { name: /actions/i }));
    fireEvent.click(screen.getByRole("menuitem", { name: /summarise/i }));
    await waitFor(() => expect(api.summarize).toHaveBeenCalledWith("rec-1"));
  });

  it("Delete action confirms then calls the API", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    renderList();
    await screen.findByText("Weekly Standup");
    fireEvent.click(screen.getByRole("button", { name: /actions/i }));
    fireEvent.click(screen.getByRole("menuitem", { name: /delete/i }));
    await waitFor(() => expect(api.deleteRecording).toHaveBeenCalledWith("rec-1"));
  });
});
