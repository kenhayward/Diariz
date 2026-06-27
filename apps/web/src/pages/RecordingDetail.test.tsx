import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter, Routes, Route } from "react-router-dom";
import { describe, it, expect, vi, beforeEach } from "vitest";
import type { RecordingDetail as RecordingDetailType } from "../lib/types";

vi.mock("../lib/signalr", () => ({
  createHub: () => ({ start: () => Promise.resolve(), stop: () => Promise.resolve(), on: () => {} }),
}));

vi.mock("../lib/api", () => ({
  api: {
    getRecording: vi.fn(),
    retranscribe: vi.fn(),
    renameSpeaker: vi.fn(),
    renameRecording: vi.fn(),
    deleteRecording: vi.fn(),
    summarize: vi.fn(),
    audioUrl: vi.fn(),
    downloadTranscript: vi.fn(),
    downloadAudio: vi.fn(),
    updateSegment: vi.fn(),
    mergeSegments: vi.fn(),
    emailTranscript: vi.fn(),
    listSpeakerProfiles: vi.fn(),
    assignSpeaker: vi.fn(),
    createSpeakerProfile: vi.fn(),
  },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../lib/api";
import RecordingDetail from "./RecordingDetail";

const base: RecordingDetailType = {
  id: "rec-123",
  title: "Mic 6/26/2026",
  name: null,
  source: "Microphone",
  durationMs: 2000,
  status: "Transcribed",
  error: null,
  createdAt: new Date("2026-06-26T12:00:00Z").toISOString(),
  speakerNames: {},
  speakers: [],
  current: {
    id: "t1",
    model: "whisperx-large-v3",
    version: 1,
    language: "en",
    segments: [{ id: "seg-1", speaker: "SPEAKER_00", speakerDisplay: "Alice", startMs: 0, endMs: 1000, text: "Hi" }],
  },
  summary: null,
};

function renderPage(rec: RecordingDetailType) {
  (api.getRecording as ReturnType<typeof vi.fn>).mockResolvedValue(rec);
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

describe("RecordingDetail", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear(); // speakers-panel collapse state persists
    (api.retranscribe as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    (api.summarize as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    (api.audioUrl as ReturnType<typeof vi.fn>).mockResolvedValue("blob:audio");
    (api.listSpeakerProfiles as ReturnType<typeof vi.fn>).mockResolvedValue([]);
  });

  it("re-transcribe (kebab) enqueues and refetches the recording", async () => {
    renderPage(base);
    await waitFor(() => expect(api.getRecording).toHaveBeenCalledTimes(1));

    fireEvent.click(await screen.findByRole("button", { name: "Actions" }));
    fireEvent.click(screen.getByRole("menuitem", { name: /re-transcribe/i }));

    await waitFor(() => expect(api.retranscribe).toHaveBeenCalledWith("rec-123"));
    await waitFor(() => expect(api.getRecording).toHaveBeenCalledTimes(2));
  });

  it("Summarise (kebab) calls the API and refetches", async () => {
    renderPage(base);
    await screen.findByText("Hi");

    fireEvent.click(screen.getByRole("button", { name: "Actions" }));
    fireEvent.click(screen.getByRole("menuitem", { name: /summarise/i }));

    await waitFor(() => expect(api.summarize).toHaveBeenCalledWith("rec-123"));
    await waitFor(() => expect(api.getRecording).toHaveBeenCalledTimes(2));
  });

  it("renders the summary text when present", async () => {
    renderPage({ ...base, summary: { model: "gpt", text: "The key decisions.", createdAt: base.createdAt } });
    expect(await screen.findByText("The key decisions.")).toBeTruthy();
  });

  it("clicking a segment resolves the audio URL and plays from its start", async () => {
    const play = vi
      .spyOn(window.HTMLMediaElement.prototype, "play")
      .mockResolvedValue(undefined);
    renderPage({
      ...base,
      current: {
        ...base.current!,
        segments: [{ id: "seg-9", speaker: "SPEAKER_00", speakerDisplay: "Alice", startMs: 1000, endMs: 2000, text: "Hello there" }],
      },
    });

    fireEvent.click(await screen.findByText("Hello there"));

    await waitFor(() => expect(api.audioUrl).toHaveBeenCalledWith("rec-123"));
    await waitFor(() => expect(play).toHaveBeenCalled());
  });

  it("collapses and expands the speakers panel", async () => {
    renderPage(base);
    await screen.findByText("Hi");
    expect(screen.getByLabelText(/assign SPEAKER_00 to a person/i)).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: /collapse speakers panel/i }));
    expect(screen.queryByLabelText(/assign SPEAKER_00 to a person/i)).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: /expand speakers panel/i }));
    expect(screen.getByLabelText(/assign SPEAKER_00 to a person/i)).toBeTruthy();
  });

  it("merges same-speaker rows via the button (after confirm)", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    (api.mergeSegments as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    renderPage(base);
    await screen.findByText("Hi");

    fireEvent.click(screen.getByRole("button", { name: /merge same-speaker rows/i }));

    await waitFor(() => expect(api.mergeSegments).toHaveBeenCalledWith("rec-123"));
  });

  it("emails the transcript via the kebab and confirms", async () => {
    (api.emailTranscript as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    renderPage(base);
    await screen.findByText("Hi");

    fireEvent.click(screen.getByRole("button", { name: "Actions" }));
    fireEvent.click(screen.getByRole("menuitem", { name: /email me the transcript/i }));

    await waitFor(() => expect(api.emailTranscript).toHaveBeenCalledWith("rec-123"));
    expect(await screen.findByText(/emailed to your account/i)).toBeTruthy();
  });

  it("edits a segment via its kebab and saves the new text", async () => {
    (api.updateSegment as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    renderPage(base);
    await screen.findByText("Hi");

    // Open the segment's kebab → Edit, change the text, save.
    fireEvent.click(screen.getByRole("button", { name: /segment actions/i }));
    fireEvent.click(screen.getByRole("menuitem", { name: /edit/i }));
    const textarea = screen.getByRole("textbox", { name: /segment text/i });
    fireEvent.change(textarea, { target: { value: "Hi, corrected" } });
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await waitFor(() => expect(api.updateSegment).toHaveBeenCalledWith("rec-123", "seg-1", "Hi, corrected"));
  });
});
