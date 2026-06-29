import { render, screen, fireEvent, waitFor, within } from "@testing-library/react";
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
    reidentify: vi.fn(),
    extractActions: vi.fn(),
    createAction: vi.fn(),
    updateAction: vi.fn(),
    deleteAction: vi.fn(),
    listSpeakerProfiles: vi.fn(),
    listAttachments: vi.fn().mockResolvedValue([]),
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
  minSpeakers: null,
  maxSpeakers: null,
  speakerNames: {},
  speakers: [],
  current: {
    id: "t1",
    model: "whisperx-large-v3",
    version: 1,
    language: "en",
    processingMs: 65_000,
    segments: [{ id: "seg-1", speaker: "SPEAKER_00", speakerDisplay: "Alice", startMs: 0, endMs: 1000, text: "Hi" }],
  },
  summary: null,
  actions: [],
  actionsExtracted: false,
  hasAudio: true,
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

  it("re-transcribe (kebab) opens a modal and re-transcribes on confirm", async () => {
    renderPage(base);
    await waitFor(() => expect(api.getRecording).toHaveBeenCalledTimes(1));

    fireEvent.click(await screen.findByRole("button", { name: "Actions" }));
    fireEvent.click(screen.getByRole("menuitem", { name: /re-transcribe/i }));
    // Confirm without entering any hints (scope to the dialog — the toolbar also has a Re-transcribe button).
    const dialog = screen.getByRole("dialog", { name: /re-transcribe/i });
    fireEvent.click(within(dialog).getByRole("button", { name: /^re-transcribe$/i }));

    await waitFor(() =>
      expect(api.retranscribe).toHaveBeenCalledWith("rec-123", { speakers: { min: null, max: null } }),
    );
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

  it("shows the transcription processing time in the subtitle", async () => {
    renderPage(base);
    // 65,000 ms → 1:05.
    expect(await screen.findByText(/transcribed in 1:05/)).toBeTruthy();
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

    fireEvent.click(screen.getByRole("button", { name: /collapse speakers section/i }));
    expect(screen.queryByLabelText(/assign SPEAKER_00 to a person/i)).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: /expand speakers section/i }));
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

  it("re-transcribes with speaker hints entered in the modal", async () => {
    (api.retranscribe as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    renderPage(base);
    await screen.findByText("Hi");

    fireEvent.click(screen.getByRole("button", { name: "Actions" }));
    fireEvent.click(screen.getByRole("menuitem", { name: /re-transcribe/i }));
    fireEvent.change(screen.getByLabelText(/minimum speakers/i), { target: { value: "2" } });
    const dialog = screen.getByRole("dialog", { name: /re-transcribe/i });
    fireEvent.click(within(dialog).getByRole("button", { name: /^re-transcribe$/i }));

    await waitFor(() =>
      expect(api.retranscribe).toHaveBeenCalledWith("rec-123", { speakers: { min: 2, max: null } }),
    );
  });

  it("re-identifies speakers via the kebab", async () => {
    (api.reidentify as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    renderPage(base);
    await screen.findByText("Hi");

    fireEvent.click(screen.getByRole("button", { name: "Actions" }));
    fireEvent.click(screen.getByRole("menuitem", { name: /re-identify speakers/i }));

    await waitFor(() => expect(api.reidentify).toHaveBeenCalledWith("rec-123"));
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

  it("extracts actions via the toolbar and shows the actions panel", async () => {
    (api.extractActions as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "act-1", text: "Send the report", actor: "Bob", deadline: "Friday", ordinal: 0 },
    ]);
    renderPage(base);
    await screen.findByText("Hi");

    // After extraction the page refetches; the recording now reports actionsExtracted = true with the action.
    (api.getRecording as ReturnType<typeof vi.fn>).mockResolvedValue({
      ...base,
      actionsExtracted: true,
      actions: [{ id: "act-1", text: "Send the report", actor: "Bob", deadline: "Friday", ordinal: 0 }],
    });
    fireEvent.click(screen.getByRole("button", { name: "Extract actions" }));

    await waitFor(() => expect(api.extractActions).toHaveBeenCalledWith("rec-123"));
    expect(await screen.findByRole("heading", { name: /actions/i })).toBeTruthy();
    expect((await screen.findByLabelText("Action 1") as HTMLInputElement).value).toBe("Send the report");
  });

  it("shows a banner while extraction is in flight", async () => {
    let resolve!: (v: unknown[]) => void;
    (api.extractActions as ReturnType<typeof vi.fn>).mockReturnValue(new Promise((r) => (resolve = r)));
    renderPage(base);
    await screen.findByText("Hi");

    fireEvent.click(screen.getByRole("button", { name: "Extract actions" }));
    expect(await screen.findByText(/extracting actions from the transcript/i)).toBeTruthy();

    resolve([]); // finish the extraction → banner clears
    await waitFor(() => expect(screen.queryByText(/extracting actions from the transcript/i)).toBeNull());
  });

  it("does not show the actions panel until extraction has run", async () => {
    renderPage(base);
    await screen.findByText("Hi");
    expect(screen.queryByRole("heading", { name: /^actions$/i })).toBeNull();
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
