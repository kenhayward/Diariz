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
    listSections: vi.fn().mockResolvedValue([]),
    retranscribe: vi.fn(),
    summarize: vi.fn(),
    deleteRecording: vi.fn(),
    renameRecording: vi.fn(),
    moveRecording: vi.fn(),
    renameSection: vi.fn(),
    deleteSection: vi.fn(),
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
  sectionId: null,
  sectionName: null,
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

  it("groups recordings under section headings with Ungrouped last", async () => {
    (api.listRecordings as ReturnType<typeof vi.fn>).mockResolvedValue([
      { ...rec, id: "a", name: "Grouped one", sectionId: "sec-1", sectionName: "Work" },
      { ...rec, id: "b", name: "Loose one", sectionId: null, sectionName: null },
    ]);
    renderList();

    const work = await screen.findByRole("heading", { name: "Work" });
    const ungrouped = screen.getByRole("heading", { name: "Ungrouped" });
    expect(work).toBeTruthy();
    // Ungrouped heading comes after the Work heading in document order.
    expect(work.compareDocumentPosition(ungrouped) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it("deletes a section from its heading menu (recordings fall back to Ungrouped)", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    (api.deleteSection as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    (api.listRecordings as ReturnType<typeof vi.fn>).mockResolvedValue([
      { ...rec, id: "a", sectionId: "sec-1", sectionName: "Work" },
      { ...rec, id: "b", sectionId: null, sectionName: null },
    ]);
    renderList();
    await screen.findByRole("heading", { name: "Work" });

    fireEvent.click(screen.getByRole("button", { name: /section actions/i }));
    fireEvent.click(screen.getByRole("menuitem", { name: /delete/i }));

    await waitFor(() => expect(api.deleteSection).toHaveBeenCalledWith("sec-1"));
  });

  it("kebab includes Re-transcribe, Summarise and Move (and no Download both)", async () => {
    renderList();
    await screen.findByText("Weekly Standup");
    fireEvent.click(screen.getByRole("button", { name: /actions/i }));

    expect(screen.getByRole("menuitem", { name: /re-transcribe/i })).toBeTruthy();
    expect(screen.getByRole("menuitem", { name: /summarise/i })).toBeTruthy();
    expect(screen.getByRole("menuitem", { name: /move to section/i })).toBeTruthy();
    expect(screen.queryByRole("menuitem", { name: /download both/i })).toBeNull();
  });
});
