import { render, screen, fireEvent, waitFor, within } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter, Routes, Route, useNavigate } from "react-router-dom";
import { describe, it, expect, vi, beforeEach } from "vitest";
import type { RecordingDetail as RecordingDetailType } from "../lib/types";
import { StatusProvider, useStatus } from "../lib/status";

/// Renders the current global status message, so a test can observe what the page pushed to the status bar
/// (pipeline progress like "Extracting actions..." is shown only there, not in an in-page banner).
function StatusProbe() {
  const { status } = useStatus();
  return <div data-testid="status">{status?.text ?? ""}</div>;
}

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
    generateMeetingMinutes: vi.fn(),
    updateMeetingMinutes: vi.fn(),
    emailMeetingMinutes: vi.fn(),
    getCalendarMatch: vi.fn().mockResolvedValue(null),
    audioUrl: vi.fn(),
    downloadTranscript: vi.fn(),
    downloadAudio: vi.fn(),
    updateSegment: vi.fn(),
    deleteSegments: vi.fn(),
    translateSegments: vi.fn(),
    mergeSegments: vi.fn(),
    emailTranscript: vi.fn(),
    reidentify: vi.fn(),
    extractActions: vi.fn(),
    createAction: vi.fn(),
    updateAction: vi.fn(),
    deleteAction: vi.fn(),
    listSpeakerProfiles: vi.fn(),
    getProfile: vi.fn().mockResolvedValue(null),
    listAttachments: vi.fn().mockResolvedValue([]),
    addFileAttachment: vi.fn(),
    deleteAttachment: vi.fn(),
    attachmentContentUrl: (rec: string, aid: string) => `/api/recordings/${rec}/attachments/${aid}/content`,
    assignSpeaker: vi.fn(),
    createSpeakerProfile: vi.fn(),
  },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../lib/api";
import RecordingDetail from "./RecordingDetail";

function NavTo({ to }: { to: string }) {
  const navigate = useNavigate();
  return <button onClick={() => navigate(to)}>go</button>;
}

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
  meetingMinutes: null,
  actions: [],
  actionsExtracted: false,
  hasAudio: true,
} as unknown as RecordingDetailType;

const minutes = { model: "gpt", text: "## Overview\n\nWe met and agreed.", createdAt: base.createdAt, isUserEdited: false };

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

/// Wait for the page to have loaded (the tab strip appears once the recording resolves).
const loaded = () => screen.findByRole("tab", { name: /overview/i });
/// Switch to a tab by its label.
const openTab = (name: RegExp) => fireEvent.click(screen.getByRole("tab", { name }));

describe("RecordingDetail", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear(); // the selected tab persists in localStorage — reset between tests
    (api.retranscribe as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    (api.summarize as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    (api.audioUrl as ReturnType<typeof vi.fn>).mockResolvedValue("blob:audio");
    (api.listSpeakerProfiles as ReturnType<typeof vi.fn>).mockResolvedValue([]);
    (api.listAttachments as ReturnType<typeof vi.fn>).mockResolvedValue([]);
    (api.generateMeetingMinutes as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    (api.emailMeetingMinutes as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
  });

  it("defaults to the Overview tab and shows the summary there", async () => {
    renderPage({ ...base, summary: { model: "gpt", text: "The key decisions.", createdAt: base.createdAt } });
    // Overview is the default tab, so the summary text is visible immediately (no expand step).
    expect(await screen.findByText("The key decisions.")).toBeTruthy();
    expect((await loaded()).getAttribute("aria-selected")).toBe("true");
  });

  it("shows Meeting Date / Time / Duration and a Summary heading on the Overview tab", async () => {
    renderPage({ ...base, durationMs: 3_900_000 }); // 1h 05m
    await loaded();
    expect(screen.getByText("Meeting Date")).toBeTruthy();
    expect(screen.getByText("Meeting Time")).toBeTruthy();
    expect(screen.getByText("01:05")).toBeTruthy(); // duration hh:mm
    // "Summary" appears both as the tab label and the in-tab heading.
    expect(screen.getByRole("heading", { name: "Summary" })).toBeTruthy();
  });

  it("shows the matching Google Calendar meeting on the Overview when Calendar is connected", async () => {
    (api.getProfile as ReturnType<typeof vi.fn>).mockResolvedValue({ googleCalendar: true });
    (api.getCalendarMatch as ReturnType<typeof vi.fn>).mockResolvedValue({
      id: "evt1", summary: "Quarterly Planning", start: base.createdAt, end: base.createdAt, htmlLink: "https://cal/evt1",
    });
    renderPage(base);
    await loaded();
    const link = await screen.findByRole("link", { name: "Quarterly Planning" });
    expect(link.getAttribute("href")).toBe("https://cal/evt1");
    expect(screen.getByText("Meeting")).toBeTruthy();
  });

  it("does not query the calendar when Calendar isn't connected", async () => {
    (api.getProfile as ReturnType<typeof vi.fn>).mockResolvedValue({ googleCalendar: false });
    renderPage(base);
    await loaded();
    expect(api.getCalendarMatch).not.toHaveBeenCalled();
    expect(screen.queryByText("Meeting")).toBeNull();
  });

  it("opens on the Transcript tab when the URL carries a segment deep-link (?t=)", async () => {
    (api.getRecording as ReturnType<typeof vi.fn>).mockResolvedValue(base);
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={qc}>
        <MemoryRouter initialEntries={["/recordings/rec-123?t=500"]}>
          <Routes>
            <Route path="/recordings/:id" element={<RecordingDetail />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>,
    );
    // Despite the default being Overview, the deep-link forces the Transcript tab so the segment is shown.
    const transcriptTab = await screen.findByRole("tab", { name: /transcript/i });
    expect(transcriptTab.getAttribute("aria-selected")).toBe("true");
    expect(await screen.findByText("Hi")).toBeTruthy();
  });

  it("switches to the Minutes tab; Re-create calls the API", async () => {
    renderPage({ ...base, meetingMinutes: minutes });
    await loaded();
    // Minutes content isn't shown until its tab is active.
    expect(screen.queryByText("We met and agreed.")).toBeNull();
    openTab(/minutes/i);
    expect(screen.getByText("We met and agreed.")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: /re-create minutes/i }));
    await waitFor(() => expect(api.generateMeetingMinutes).toHaveBeenCalledWith("rec-123"));
  });

  it("Email minutes with no attachments emails directly", async () => {
    renderPage({ ...base, meetingMinutes: minutes });
    await loaded();
    openTab(/minutes/i);
    fireEvent.click(screen.getByRole("button", { name: /email minutes to me/i }));
    await waitFor(() => expect(api.emailMeetingMinutes).toHaveBeenCalledWith("rec-123", false));
  });

  it("Email minutes with attachments prompts to include them", async () => {
    (api.listAttachments as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "a1", kind: "File", name: "doc.pdf", contentType: "application/pdf", sizeBytes: 8, url: null, ordinal: 0 },
    ]);
    renderPage({ ...base, meetingMinutes: minutes });
    await loaded();
    openTab(/minutes/i);

    fireEvent.click(screen.getByRole("button", { name: /email minutes to me/i }));
    expect(api.emailMeetingMinutes).not.toHaveBeenCalled();
    fireEvent.click(await screen.findByRole("button", { name: /include attachments/i }));
    await waitFor(() => expect(api.emailMeetingMinutes).toHaveBeenCalledWith("rec-123", true));
  });

  it("Minutes tab shows an empty state and disables Edit/Email when there are none", async () => {
    renderPage(base); // base.meetingMinutes is null
    await loaded();
    openTab(/minutes/i);
    expect(screen.getByText(/no meeting minutes yet/i)).toBeTruthy();
    expect((screen.getByRole("button", { name: /re-create minutes/i }) as HTMLButtonElement).disabled).toBe(false);
    expect((screen.getByRole("button", { name: /edit minutes/i }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole("button", { name: /email minutes to me/i }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("Generate meeting minutes (kebab) calls the API even when the recording has none yet", async () => {
    renderPage(base);
    await loaded();

    fireEvent.click(screen.getByRole("button", { name: "Actions" }));
    fireEvent.click(screen.getByRole("menuitem", { name: /generate meeting minutes/i }));

    await waitFor(() => expect(api.generateMeetingMinutes).toHaveBeenCalledWith("rec-123"));
  });

  it("re-transcribe (kebab) opens a modal and re-transcribes on confirm", async () => {
    renderPage(base);
    await waitFor(() => expect(api.getRecording).toHaveBeenCalledTimes(1));
    await loaded();

    fireEvent.click(screen.getByRole("button", { name: "Actions" }));
    fireEvent.click(screen.getByRole("menuitem", { name: /re-transcribe/i }));
    const dialog = screen.getByRole("dialog", { name: /re-transcribe/i });
    fireEvent.click(within(dialog).getByRole("button", { name: /^re-transcribe$/i }));

    await waitFor(() =>
      expect(api.retranscribe).toHaveBeenCalledWith("rec-123", { speakers: { min: null, max: null } }),
    );
    await waitFor(() => expect(api.getRecording).toHaveBeenCalledTimes(2));
  });

  it("Summarise (kebab) calls the API and refetches", async () => {
    renderPage(base);
    await loaded();

    fireEvent.click(screen.getByRole("button", { name: "Actions" }));
    fireEvent.click(screen.getByRole("menuitem", { name: /summarise/i }));

    await waitFor(() => expect(api.summarize).toHaveBeenCalledWith("rec-123"));
    await waitFor(() => expect(api.getRecording).toHaveBeenCalledTimes(2));
  });

  it("shows the transcription processing time in the subtitle", async () => {
    renderPage(base);
    // 65,000 ms → 1:05. The subtitle sits above the tabs.
    expect(await screen.findByText(/transcribed in 1:05/)).toBeTruthy();
  });

  it("clicking a segment (Transcript tab) selects it; Play selected then plays it", async () => {
    const play = vi.spyOn(window.HTMLMediaElement.prototype, "play").mockResolvedValue(undefined);
    renderPage({
      ...base,
      current: {
        ...base.current!,
        segments: [{ id: "seg-9", speaker: "SPEAKER_00", speakerDisplay: "Alice", startMs: 1000, endMs: 2000, text: "Hello there" }],
      },
    });
    await loaded();
    openTab(/transcript/i);

    fireEvent.click(await screen.findByText("Hello there"));
    expect(api.audioUrl).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole("button", { name: /play selected/i }));
    await waitFor(() => expect(api.audioUrl).toHaveBeenCalledWith("rec-123"));
    await waitFor(() => expect(play).toHaveBeenCalled());
  });

  it("shows the speaker assign typeahead on the Speakers tab", async () => {
    renderPage(base);
    await loaded();
    expect(screen.queryByLabelText(/assign SPEAKER_00 to a person/i)).toBeNull();
    openTab(/speakers/i);
    expect(screen.getByLabelText(/assign SPEAKER_00 to a person/i)).toBeTruthy();
  });

  it("merges same-speaker rows via the button (after confirm)", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    (api.mergeSegments as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    renderPage(base);
    await loaded();
    openTab(/transcript/i);

    fireEvent.click(screen.getByRole("button", { name: /merge same-speaker rows/i }));
    await waitFor(() => expect(api.mergeSegments).toHaveBeenCalledWith("rec-123"));
  });

  it("re-transcribes with speaker hints entered in the modal", async () => {
    (api.retranscribe as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    renderPage(base);
    await loaded();

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
    await loaded();

    fireEvent.click(screen.getByRole("button", { name: "Actions" }));
    fireEvent.click(screen.getByRole("menuitem", { name: /re-identify speakers/i }));

    await waitFor(() => expect(api.reidentify).toHaveBeenCalledWith("rec-123"));
  });

  it("disables the Speakers-tab re-identify button while it runs (shows it's in progress)", async () => {
    let resolve!: () => void;
    (api.reidentify as ReturnType<typeof vi.fn>).mockReturnValue(new Promise<void>((r) => (resolve = r)));
    renderPage(base);
    await loaded();
    openTab(/speakers/i);

    const btn = () => screen.getByRole("button", { name: /re-identify speakers/i }) as HTMLButtonElement;
    expect(btn().disabled).toBe(false);
    fireEvent.click(btn());
    await waitFor(() => expect(btn().disabled).toBe(true)); // in-flight → disabled
    resolve();
    await waitFor(() => expect(btn().disabled).toBe(false)); // settles back
  });

  it("emails the transcript via the kebab and confirms", async () => {
    (api.emailTranscript as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    renderPage(base);
    await loaded();

    fireEvent.click(screen.getByRole("button", { name: "Actions" }));
    fireEvent.click(screen.getByRole("menuitem", { name: /email me the transcript/i }));

    await waitFor(() => expect(api.emailTranscript).toHaveBeenCalledWith("rec-123"));
    expect(await screen.findByText(/emailed to your account/i)).toBeTruthy();
  });

  it("extracts actions via the Actions tab toolbar and lists them", async () => {
    (api.extractActions as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "act-1", text: "Send the report", actor: "Bob", deadline: "Friday", ordinal: 0 },
    ]);
    renderPage(base);
    await loaded();
    openTab(/actions/i);

    (api.getRecording as ReturnType<typeof vi.fn>).mockResolvedValue({
      ...base,
      actionsExtracted: true,
      actions: [{ id: "act-1", text: "Send the report", actor: "Bob", deadline: "Friday", ordinal: 0 }],
    });
    fireEvent.click(screen.getByRole("button", { name: /extract action items/i }));

    await waitFor(() => expect(api.extractActions).toHaveBeenCalledWith("rec-123"));
    // The Actions tab content updates in place (no expand step).
    expect((await screen.findByLabelText("Action 1") as HTMLInputElement).value).toBe("Send the report");
  });

  it("shows extraction progress in the status bar (not an in-page banner)", async () => {
    let resolve!: (v: unknown[]) => void;
    (api.extractActions as ReturnType<typeof vi.fn>).mockReturnValue(new Promise((r) => (resolve = r)));
    (api.getRecording as ReturnType<typeof vi.fn>).mockResolvedValue(base);
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={qc}>
        <StatusProvider>
          <MemoryRouter initialEntries={["/recordings/rec-123"]}>
            <Routes>
              <Route path="/recordings/:id" element={<RecordingDetail />} />
            </Routes>
          </MemoryRouter>
          <StatusProbe />
        </StatusProvider>
      </QueryClientProvider>,
    );
    await loaded();
    openTab(/actions/i);

    fireEvent.click(screen.getByRole("button", { name: /extract action items/i }));
    // Progress is pushed to the global status bar, and there is no in-page banner duplicating it.
    expect(await screen.findByText(/extracting actions from the transcript/i)).toBeTruthy();

    // When extraction finishes the progress is replaced by the result message (not the "extracting" text).
    resolve([]);
    await waitFor(() => expect(screen.queryByText(/extracting actions from the transcript/i)).toBeNull());
  });

  it("clears the action banner when navigating to a different recording", async () => {
    (api.extractActions as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "a1", text: "Send report", actor: "Bob", deadline: "Fri", ordinal: 0 },
    ]);
    (api.getRecording as ReturnType<typeof vi.fn>).mockImplementation((rid: string) =>
      Promise.resolve(rid === "rec-123" ? base : { ...base, id: "rec-999" }),
    );

    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={qc}>
        <MemoryRouter initialEntries={["/recordings/rec-123"]}>
          <Routes>
            <Route path="/recordings/:id" element={<RecordingDetail />} />
          </Routes>
          <NavTo to="/recordings/rec-999" />
        </MemoryRouter>
      </QueryClientProvider>,
    );

    await loaded();
    openTab(/actions/i);
    fireEvent.click(screen.getByRole("button", { name: /extract action items/i }));
    expect(await screen.findByText(/extracted 1 action/i)).toBeTruthy();

    // Navigate to a different recording — the transient banner must not carry over.
    fireEvent.click(screen.getByRole("button", { name: "go" }));
    await waitFor(() => expect(api.getRecording).toHaveBeenCalledWith("rec-999"));
    await waitFor(() => expect(screen.queryByText(/extracted 1 action/i)).toBeNull());
  });

  it("always offers the Actions tab with an extract button and Add action", async () => {
    renderPage(base);
    await loaded();
    openTab(/actions/i);
    expect(screen.getByRole("button", { name: /extract action items/i })).toBeTruthy();
    expect(screen.getByRole("button", { name: /add action/i })).toBeTruthy();
  });

  it("selects a segment and edits it from the toolbar, saving the new text", async () => {
    (api.updateSegment as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    renderPage(base);
    await loaded();
    openTab(/transcript/i);

    fireEvent.click(await screen.findByText("Hi"));
    fireEvent.click(screen.getByRole("button", { name: /edit segment/i }));
    const textarea = screen.getByRole("textbox", { name: /segment text/i });
    fireEvent.change(textarea, { target: { value: "Hi, corrected" } });
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await waitFor(() => expect(api.updateSegment).toHaveBeenCalledWith("rec-123", "seg-1", "Hi, corrected"));
  });

  it("bulk-deletes selected segments from the toolbar after confirming the count", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    (api.deleteSegments as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    renderPage(base);
    await loaded();
    openTab(/transcript/i);

    fireEvent.click(screen.getByRole("button", { name: /select segments/i }));
    fireEvent.click(await screen.findByText("Hi"));
    fireEvent.click(screen.getByRole("button", { name: /delete selected/i }));

    await waitFor(() => expect(api.deleteSegments).toHaveBeenCalledWith("rec-123", ["seg-1"]));
  });

  it("lists attachments and allows deleting on the Attachments tab", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    (api.deleteAttachment as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    (api.listAttachments as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "a1", kind: "File", name: "doc.pdf", contentType: "application/pdf", sizeBytes: 8, url: null, ordinal: 0 },
    ]);
    renderPage(base);
    await loaded();
    openTab(/attachments/i);

    expect(screen.getByRole("button", { name: /add file/i })).toBeTruthy();
    expect((await screen.findByDisplayValue("doc.pdf"))).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: /^remove$/i }));
    await waitFor(() => expect(api.deleteAttachment).toHaveBeenCalledWith("rec-123", "a1"));
  });
});
