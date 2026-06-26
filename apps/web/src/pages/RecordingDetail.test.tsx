import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter, Routes, Route } from "react-router-dom";
import { describe, it, expect, vi, beforeEach } from "vitest";
import type { RecordingDetail as RecordingDetailType } from "../lib/types";

// SignalR is irrelevant to the button behaviour and needs a live server; stub it.
vi.mock("../lib/signalr", () => ({
  createHub: () => ({ start: () => Promise.resolve(), stop: () => Promise.resolve(), on: () => {} }),
}));

vi.mock("../lib/api", () => ({
  api: {
    getRecording: vi.fn(),
    retranscribe: vi.fn(),
    renameSpeaker: vi.fn(),
  },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../lib/api";
import RecordingDetail from "./RecordingDetail";

const recording: RecordingDetailType = {
  id: "rec-123",
  title: "Test recording",
  durationMs: 1000,
  status: "Transcribed",
  error: null,
  createdAt: new Date("2026-06-26T12:00:00Z").toISOString(),
  speakerNames: {},
  current: { id: "t1", model: "whisperx-large-v3", version: 1, language: "en", segments: [] },
};

function renderPage() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter initialEntries={["/recordings/rec-123"]}>
        <Routes>
          <Route path="/recordings/:id" element={<RecordingDetail />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe("RecordingDetail re-transcribe button", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    (api.getRecording as ReturnType<typeof vi.fn>).mockResolvedValue(recording);
    (api.retranscribe as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
  });

  it("enqueues a re-transcribe and refetches the recording so the UI reflects it", async () => {
    renderPage();

    const button = await screen.findByRole("button", { name: /re-transcribe/i });
    await waitFor(() => expect(api.getRecording).toHaveBeenCalledTimes(1));

    fireEvent.click(button);

    await waitFor(() => expect(api.retranscribe).toHaveBeenCalledWith("rec-123"));
    // The fix: a successful enqueue must invalidate the recording query so the page
    // refetches (status flips to Queued) instead of silently doing nothing.
    await waitFor(() => expect(api.getRecording).toHaveBeenCalledTimes(2));
  });
});
