import { render, screen, fireEvent, waitFor, within } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter, Routes, Route, useNavigate, useLocation } from "react-router-dom";
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

// The transcript weaves the current user's notes in, so the page reads useAuth for the note "speaker".
vi.mock("../auth", () => ({ useAuth: () => ({ fullName: "Test User", email: "t@x.test" }) }));
// RecordingDetail reads the current room to gate Share / Remove-from-room / Delete + the calendar surface;
// mutable so a test can view the recording inside a shared room.
const roomState: {
  rooms: unknown[];
  currentRoom: { id: string; name: string; isPersonal: boolean } | undefined;
} = { rooms: [], currentRoom: undefined };
vi.mock("../lib/rooms", () => ({ useRoom: () => roomState }));

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
    getCalendarEvent: vi.fn(),
    putCalendarLink: vi.fn(),
    deleteCalendarLink: vi.fn(),
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
    listMeetingTypes: vi.fn().mockResolvedValue([]),
    applyMeetingType: vi.fn(),
    listNotes: vi.fn().mockResolvedValue([]),
    createNotes: vi.fn(),
    updateNote: vi.fn(),
    deleteNote: vi.fn(),
    listFormulaResults: vi.fn().mockResolvedValue([]),
    listFormulas: vi.fn().mockResolvedValue([]),
    runFormula: vi.fn(),
    getFormulaResultText: vi.fn(),
    updateFormulaResult: vi.fn(),
    deleteFormulaResult: vi.fn(),
    emailFormulaResult: vi.fn(),
    downloadFormulaResult: vi.fn(),
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
  calendarLink: null,
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

/// Wait for the page to have loaded (the hub's tiles appear once the recording resolves).
const loaded = () => screen.findByRole("button", { name: "Transcript" });

/// Drill into a section from the hub by clicking its tile. The names are matched exactly: several header
/// and kebab actions also mention "transcript" ("Download transcript", "Email transcript"), so a loose
/// regex would be ambiguous.
const openTab = (name: string) => fireEvent.click(screen.getByRole("button", { name }));

/// Minutes is the one section with no tile — the hero card links to it ("Open full minutes").
const openMinutes = () => fireEvent.click(screen.getByRole("button", { name: /Open full minutes/ }));

/// Back out of a section to the hub, via the breadcrumb.
const backToHub = () => fireEvent.click(screen.getByRole("button", { name: /^Overview$/ }));

/// Open the header's overflow menu. It holds every action (rename / retranscribe / move / email / ...);
/// only Play, Copy link and Download are surfaced as buttons. Note the menu is "More actions", not
/// "Actions" — that name now belongs to the hub's Actions tile.
const openKebab = () => fireEvent.click(screen.getByRole("button", { name: "More actions" }));

describe("RecordingDetail", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    roomState.currentRoom = undefined; // default: personal-room context
    localStorage.clear(); // the selected tab persists in localStorage — reset between tests
    (api.retranscribe as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    (api.summarize as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    (api.audioUrl as ReturnType<typeof vi.fn>).mockResolvedValue("blob:audio");
    (api.listSpeakerProfiles as ReturnType<typeof vi.fn>).mockResolvedValue([]);
    (api.listAttachments as ReturnType<typeof vi.fn>).mockResolvedValue([]);
    (api.generateMeetingMinutes as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    (api.emailMeetingMinutes as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
  });

  it("lands on the hub and shows the summary inline in the hero card", async () => {
    renderPage({ ...base, summary: { model: "gpt", text: "The key decisions.", createdAt: base.createdAt } });
    // The hub is the landing view, so the summary is visible immediately (no tab switch, no expand step).
    expect(await screen.findByText("The key decisions.")).toBeTruthy();
    expect(screen.getByRole("heading", { name: "Summary" })).toBeTruthy();
  });

  it("shows the recording's date, time and duration as hero chips (the old Overview list)", async () => {
    renderPage({ ...base, durationMs: 3_900_000 }); // 1h 05m
    await loaded();
    // The date and time now share one chip (so their text spans several nodes), and the duration reads as a
    // human length rather than the old clock format.
    const chip = (re: RegExp) =>
      screen.getByText((_content, el) => el?.tagName === "SPAN" && re.test(el.textContent ?? ""));
    expect(chip(/26th June 2026 · \d{2}:\d{2}/)).toBeTruthy();
    expect(chip(/^1 h 5 min$/)).toBeTruthy();
  });

  it("offers a tile for every section, so nothing is discoverable only by clicking a tab", async () => {
    renderPage(base);
    await loaded();
    for (const tile of ["Transcript", "Actions", "Speakers", "Notes", "Files", "Formulas"]) {
      expect(screen.getByRole("button", { name: tile })).toBeTruthy();
    }
  });

  it("returns to the hub from a section's breadcrumb", async () => {
    renderPage(base);
    await loaded();
    openTab("Speakers");
    expect(screen.queryByRole("button", { name: "Formulas" })).toBeNull(); // the hub's tiles are gone
    backToHub();
    expect(screen.getByRole("button", { name: "Formulas" })).toBeTruthy(); // and back
  });

  it("lands on the hub for someone whose last-used tab was the Overview that no longer exists", async () => {
    localStorage.setItem("diariz.detailSection", "overview"); // the key the old tab strip persisted
    renderPage(base);
    // Migrated to the hub rather than opening a section that isn't there any more.
    expect(await screen.findByRole("heading", { name: "Summary" })).toBeTruthy();
  });

  it("auto-saves the suggested meeting when Calendar is connected and the recording is unlinked", async () => {
    (api.getProfile as ReturnType<typeof vi.fn>).mockResolvedValue({ googleCalendar: true });
    (api.getCalendarMatch as ReturnType<typeof vi.fn>).mockResolvedValue({
      id: "evt1", summary: "Quarterly Planning", start: base.createdAt, end: base.createdAt, htmlLink: "https://cal/evt1", calendarId: "team@g",
    });
    (api.putCalendarLink as ReturnType<typeof vi.fn>).mockResolvedValue({
      eventId: "evt1", calendarId: "team@g", summary: "Quarterly Planning", start: base.createdAt, end: base.createdAt,
      htmlLink: "https://cal/evt1", linkedManually: false,
    });
    renderPage(base); // base.calendarLink is null
    await loaded();
    // Auto-save carries the match's calendarId so the link targets the right calendar.
    await waitFor(() => expect(api.putCalendarLink).toHaveBeenCalledWith("rec-123", "evt1", false, "team@g"));
  });

  it("shows the linked meeting's full details and manage actions on the Overview", async () => {
    (api.getProfile as ReturnType<typeof vi.fn>).mockResolvedValue({ googleCalendar: true });
    (api.getCalendarEvent as ReturnType<typeof vi.fn>).mockResolvedValue({
      id: "evt1", summary: "Quarterly Planning", start: base.createdAt, end: base.createdAt,
      htmlLink: "https://cal/evt1", location: "Room 4", description: "Agenda",
      organizer: { email: "boss@x.test", displayName: "The Boss", responseStatus: null, organizer: true, self: false },
      attendees: [],
    });
    renderPage({
      ...base,
      calendarLink: { eventId: "evt1", calendarId: "primary", summary: "Quarterly Planning", start: base.createdAt, end: base.createdAt, htmlLink: "https://cal/evt1", linkedManually: false },
    });
    await loaded();

    const link = await screen.findByRole("link", { name: "Quarterly Planning" });
    expect(link.getAttribute("href")).toBe("https://cal/evt1");
    expect(await screen.findByText("Room 4")).toBeTruthy(); // live details rendered
    expect(api.getCalendarEvent).toHaveBeenCalledWith("evt1");
    expect(screen.getByRole("button", { name: /change meeting/i })).toBeTruthy();
    expect(screen.getByRole("button", { name: /unlink meeting/i })).toBeTruthy();
    // Already linked → the auto-save must not fire.
    expect(api.putCalendarLink).not.toHaveBeenCalled();
  });

  it("hides the linked meeting on the Overview while viewing in a shared room (calendar is personal-only)", async () => {
    roomState.currentRoom = { id: "eng-room", name: "Podcasts", isPersonal: false };
    (api.getProfile as ReturnType<typeof vi.fn>).mockResolvedValue({ googleCalendar: true });
    renderPage({
      ...base,
      calendarLink: { eventId: "evt1", calendarId: "primary", summary: "Quarterly Planning", start: base.createdAt, end: base.createdAt, htmlLink: "https://cal/evt1", linkedManually: false },
    });
    await loaded();

    expect(screen.queryByRole("link", { name: "Quarterly Planning" })).toBeNull(); // block hidden
    expect(screen.queryByRole("button", { name: /change meeting/i })).toBeNull();
    expect(api.getCalendarEvent).not.toHaveBeenCalled(); // no calendar fetch in a shared room
  });

  it("does not link (auto or manual) while viewing an unlinked recording in a shared room", async () => {
    roomState.currentRoom = { id: "eng-room", name: "Podcasts", isPersonal: false };
    (api.getProfile as ReturnType<typeof vi.fn>).mockResolvedValue({ googleCalendar: true });
    (api.getCalendarMatch as ReturnType<typeof vi.fn>).mockResolvedValue({
      id: "evt1", summary: "Quarterly Planning", start: base.createdAt, end: base.createdAt, htmlLink: "https://cal/evt1", calendarId: "team@g",
    });
    renderPage(base); // unlinked
    await loaded();

    expect(api.getCalendarMatch).not.toHaveBeenCalled(); // suggestion not even fetched
    expect(api.putCalendarLink).not.toHaveBeenCalled(); // no auto-link
    expect(screen.queryByRole("button", { name: /link.*meeting|link a meeting/i })).toBeNull();
  });

  it("unlinks the meeting when Unlink is clicked", async () => {
    (api.getProfile as ReturnType<typeof vi.fn>).mockResolvedValue({ googleCalendar: true });
    (api.getCalendarEvent as ReturnType<typeof vi.fn>).mockResolvedValue({
      id: "evt1", summary: "Quarterly Planning", start: base.createdAt, end: base.createdAt, htmlLink: null,
    });
    (api.deleteCalendarLink as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    renderPage({
      ...base,
      calendarLink: { eventId: "evt1", calendarId: "primary", summary: "Quarterly Planning", start: base.createdAt, end: base.createdAt, htmlLink: null, linkedManually: true },
    });
    await loaded();
    fireEvent.click(await screen.findByRole("button", { name: /unlink meeting/i }));
    await waitFor(() => expect(api.deleteCalendarLink).toHaveBeenCalledWith("rec-123"));
  });

  it("does not query the calendar when Calendar isn't connected", async () => {
    (api.getProfile as ReturnType<typeof vi.fn>).mockResolvedValue({ googleCalendar: false });
    renderPage(base);
    await loaded();
    expect(api.getCalendarMatch).not.toHaveBeenCalled();
    expect(api.putCalendarLink).not.toHaveBeenCalled();
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
    // Despite the hub being the default, the deep-link opens the Transcript so the segment is shown. The
    // breadcrumb (not a tab strip) is what marks the active section now.
    expect(await screen.findByText("Hi")).toBeTruthy();
    expect(screen.getByRole("navigation", { name: "Transcript" })).toBeTruthy();
  });

  it("weaves the user's timed notes into the transcript with the user as the speaker", async () => {
    (api.getRecording as ReturnType<typeof vi.fn>).mockResolvedValue(base);
    (api.listNotes as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "note-1", text: "budget concern", capturedAtMs: 500, ordinal: 0, createdAt: "2026-07-01T00:00:00Z" },
    ]);
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
    expect(await screen.findByText("budget concern")).toBeTruthy();
    expect(await screen.findByText("Test User")).toBeTruthy(); // the note's "speaker" (from useAuth)
  });

  it("switches to the Minutes tab; Re-create calls the API", async () => {
    renderPage({ ...base, meetingMinutes: minutes });
    await loaded();
    // Minutes content isn't shown until its tab is active.
    expect(screen.queryByText("We met and agreed.")).toBeNull();
    openMinutes();
    expect(screen.getByText("We met and agreed.")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: /re-create minutes/i }));
    await waitFor(() => expect(api.generateMeetingMinutes).toHaveBeenCalledWith("rec-123"));
  });

  it("Email minutes with no attachments emails directly", async () => {
    renderPage({ ...base, meetingMinutes: minutes });
    await loaded();
    openMinutes();
    fireEvent.click(screen.getByRole("button", { name: /email minutes to me/i }));
    await waitFor(() => expect(api.emailMeetingMinutes).toHaveBeenCalledWith("rec-123", false));
  });

  it("Email minutes with attachments prompts to include them", async () => {
    (api.listAttachments as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "a1", kind: "File", name: "doc.pdf", contentType: "application/pdf", sizeBytes: 8, url: null, ordinal: 0 },
    ]);
    renderPage({ ...base, meetingMinutes: minutes });
    await loaded();
    openMinutes();

    fireEvent.click(screen.getByRole("button", { name: /email minutes to me/i }));
    expect(api.emailMeetingMinutes).not.toHaveBeenCalled();
    fireEvent.click(await screen.findByRole("button", { name: /include attachments/i }));
    await waitFor(() => expect(api.emailMeetingMinutes).toHaveBeenCalledWith("rec-123", true));
  });

  it("Minutes tab shows an empty state and disables Edit/Email when there are none", async () => {
    renderPage(base); // base.meetingMinutes is null
    await loaded();
    openMinutes();
    expect(screen.getByText(/no meeting minutes yet/i)).toBeTruthy();
    expect((screen.getByRole("button", { name: /re-create minutes/i }) as HTMLButtonElement).disabled).toBe(false);
    expect((screen.getByRole("button", { name: /edit minutes/i }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole("button", { name: /email minutes to me/i }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("Generate meeting minutes (kebab) calls the API even when the recording has none yet", async () => {
    renderPage(base);
    await loaded();

    openKebab();
    fireEvent.click(screen.getByRole("menuitem", { name: /generate meeting minutes/i }));

    await waitFor(() => expect(api.generateMeetingMinutes).toHaveBeenCalledWith("rec-123"));
  });

  it("re-transcribe (kebab) opens a modal and re-transcribes on confirm", async () => {
    renderPage(base);
    await waitFor(() => expect(api.getRecording).toHaveBeenCalledTimes(1));
    await loaded();

    openKebab();
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

    openKebab();
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
    openTab("Transcript");

    fireEvent.click(await screen.findByText("Hello there"));
    expect(api.audioUrl).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole("button", { name: /play selected/i }));
    await waitFor(() => expect(api.audioUrl).toHaveBeenCalledWith("rec-123"));
    await waitFor(() => expect(play).toHaveBeenCalled());
  });

  it("Play selected turns into Pause while the selection plays, and clicking it stops playback", async () => {
    vi.spyOn(window.HTMLMediaElement.prototype, "play").mockResolvedValue(undefined);
    const pause = vi.spyOn(window.HTMLMediaElement.prototype, "pause").mockImplementation(() => {});
    renderPage(base);
    await loaded();
    openTab("Transcript");

    fireEvent.click(await screen.findByText("Hi"));
    fireEvent.click(screen.getByRole("button", { name: /play selected/i }));

    const pauseBtn = await screen.findByRole("button", { name: /pause selected/i });
    fireEvent.click(pauseBtn);
    expect(pause).toHaveBeenCalled();
    expect(screen.getByRole("button", { name: /play selected/i })).toBeTruthy();
  });

  it("the selection play button reads Play again when the transcript is re-entered", async () => {
    vi.spyOn(window.HTMLMediaElement.prototype, "play").mockResolvedValue(undefined);
    renderPage(base);
    await loaded();
    openTab("Transcript");

    fireEvent.click(await screen.findByText("Hi"));
    fireEvent.click(screen.getByRole("button", { name: /play selected/i }));
    await screen.findByRole("button", { name: /pause selected/i });

    backToHub();
    openTab("Transcript");
    expect(await screen.findByRole("button", { name: /play selected/i })).toBeTruthy();
  });

  it("assigns a speaker from the transcript row's dropdown without selecting the segment", async () => {
    (api.listSpeakerProfiles as ReturnType<typeof vi.fn>).mockResolvedValue([{ id: "p2", name: "Bob", sampleCount: 1 }]);
    (api.assignSpeaker as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    renderPage(base);
    await loaded();
    openTab("Transcript");

    // The row's speaker column is the same typeahead as the Speakers tab, so a speaker can be named while
    // reading the transcript.
    fireEvent.click(await screen.findByRole("button", { name: "Assign SPEAKER_00 to a person" }));
    fireEvent.change(screen.getByRole("combobox"), { target: { value: "Bob" } });
    fireEvent.click(screen.getByRole("option", { name: "Bob" }));

    await waitFor(() => expect(api.assignSpeaker).toHaveBeenCalledWith("rec-123", "SPEAKER_00", "p2"));
    // Using the dropdown must not double as clicking the row (which would select the segment).
    expect(screen.getByRole("button", { name: /play selected/i }).hasAttribute("disabled")).toBe(true);
  });

  it("shows the current speaker name on the transcript row's dropdown", async () => {
    renderPage(base);
    await loaded();
    openTab("Transcript");
    expect((await screen.findByRole("button", { name: "Assign SPEAKER_00 to a person" })).textContent).toContain("Alice");
  });

  it("shows the speaker assign typeahead on the Speakers tab", async () => {
    renderPage(base);
    await loaded();
    expect(screen.queryByLabelText(/assign SPEAKER_00 to a person/i)).toBeNull();
    openTab("Speakers");
    expect(screen.getByLabelText(/assign SPEAKER_00 to a person/i)).toBeTruthy();
  });

  it("merges same-speaker rows via the button (after confirm)", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    (api.mergeSegments as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    renderPage(base);
    await loaded();
    openTab("Transcript");

    fireEvent.click(screen.getByRole("button", { name: /merge same-speaker rows/i }));
    await waitFor(() => expect(api.mergeSegments).toHaveBeenCalledWith("rec-123"));
  });

  it("re-transcribes with speaker hints entered in the modal", async () => {
    (api.retranscribe as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    renderPage(base);
    await loaded();

    openKebab();
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

    openKebab();
    fireEvent.click(screen.getByRole("menuitem", { name: /re-identify speakers/i }));

    await waitFor(() => expect(api.reidentify).toHaveBeenCalledWith("rec-123"));
  });

  it("disables the Speakers-tab re-identify button while it runs (shows it's in progress)", async () => {
    let resolve!: () => void;
    (api.reidentify as ReturnType<typeof vi.fn>).mockReturnValue(new Promise<void>((r) => (resolve = r)));
    renderPage(base);
    await loaded();
    openTab("Speakers");

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

    openKebab();
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
    openTab("Actions");

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
    openTab("Actions");

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
    openTab("Actions");
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
    openTab("Actions");
    expect(screen.getByRole("button", { name: /extract action items/i })).toBeTruthy();
    expect(screen.getByRole("button", { name: /add action/i })).toBeTruthy();
  });

  it("selects a segment and edits it from the toolbar, saving the new text", async () => {
    (api.updateSegment as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    renderPage(base);
    await loaded();
    openTab("Transcript");

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
    openTab("Transcript");

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
    openTab("Files");

    expect(screen.getByRole("button", { name: /add file/i })).toBeTruthy();
    expect((await screen.findByDisplayValue("doc.pdf"))).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: /^remove$/i }));
    await waitFor(() => expect(api.deleteAttachment).toHaveBeenCalledWith("rec-123", "a1"));
  });

  it("selects a formula result and deletes it from the toolbar", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    (api.deleteFormulaResult as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    (api.listFormulaResults as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "fr1", recordingId: "rec-123", name: "Action Items", status: "Ready", error: null, createdByUserId: "u1", createdAt: base.createdAt, updatedAt: base.createdAt, origin: { kind: "personal", personName: "You", personPictureUrl: null } },
    ]);
    renderPage(base);
    await loaded();
    openTab("Formulas");

    // Delete is disabled until a result is selected; selecting the row enables it and wires the id through.
    expect((screen.getByRole("button", { name: /^delete$/i }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(await screen.findByText("Action Items"));
    fireEvent.click(screen.getByRole("button", { name: /^delete$/i }));
    await waitFor(() => expect(api.deleteFormulaResult).toHaveBeenCalledWith("rec-123", "fr1"));
  });

  it("downloads the selected formula result from the toolbar", async () => {
    (api.downloadFormulaResult as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    (api.listFormulaResults as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "fr1", recordingId: "rec-123", name: "Action Items", status: "Ready", error: null, createdByUserId: "u1", createdAt: base.createdAt, updatedAt: base.createdAt, origin: { kind: "personal", personName: "You", personPictureUrl: null } },
    ]);
    renderPage(base);
    await loaded();
    openTab("Formulas");

    fireEvent.click(await screen.findByText("Action Items"));
    fireEvent.click(screen.getByRole("button", { name: /^download$/i }));
    await waitFor(() => expect(api.downloadFormulaResult).toHaveBeenCalledWith("rec-123", "fr1"));
  });

  it("shows the Notes tab, lists notes, and adds one on Enter", async () => {
    (api.listNotes as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "n1", text: "Comp expectations", capturedAtMs: 61_000, ordinal: 0, createdAt: base.createdAt },
    ]);
    (api.createNotes as ReturnType<typeof vi.fn>).mockResolvedValue([]);
    renderPage(base);
    await loaded();
    openTab("Notes");

    expect(await screen.findByText("Comp expectations")).toBeTruthy();

    const box = screen.getByPlaceholderText(/add a note/i);
    fireEvent.change(box, { target: { value: "IPO experience APAC" } });
    fireEvent.keyDown(box, { key: "Enter" });
    await waitFor(() => expect(api.createNotes).toHaveBeenCalledWith("rec-123", [{ text: "IPO experience APAC" }]));
  });

  it("offers Re-create minutes from the Notes tab toolbar", async () => {
    (api.generateMeetingMinutes as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    renderPage(base);
    await loaded();
    openTab("Notes");

    fireEvent.click(screen.getByRole("button", { name: /re-create minutes/i }));
    await waitFor(() => expect(api.generateMeetingMinutes).toHaveBeenCalledWith("rec-123"));
  });

  it("clicking a note stamp switches to the transcript", async () => {
    (api.listNotes as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "n1", text: "Comp expectations", capturedAtMs: 61_000, ordinal: 0, createdAt: base.createdAt },
    ]);
    renderPage(base);
    await loaded();
    openTab("Notes");

    fireEvent.click(await screen.findByRole("button", { name: /jump to 1:01/i }));
    await waitFor(() => expect(screen.getByRole("navigation", { name: "Transcript" })).toBeTruthy());
  });

  // Bug: the minutes picker spinner only cleared on a SignalR "completed" push. If that event is missed
  // (slow LLM + a proxy that idle-drops the socket), minutes finished but the spinner span forever until a
  // manual refresh. The page must poll while a run is in flight so it notices the fresh minutes regardless.
  it("clears the minutes spinner by polling when no SignalR completion event arrives", async () => {
    const t0 = "2026-06-26T12:00:00.000Z";
    const t1 = "2026-06-26T12:05:00.000Z";
    const withMinutes = (createdAt: string, text: string): RecordingDetailType => ({
      ...base,
      meetingMinutes: { model: "gpt", text, createdAt, isUserEdited: false },
    } as unknown as RecordingDetailType);
    let done = false; // flips true when the backend "finishes" - but no SignalR event is fired.
    (api.getRecording as ReturnType<typeof vi.fn>).mockImplementation(() =>
      Promise.resolve(done ? withMinutes(t1, "New minutes") : withMinutes(t0, "Old minutes")),
    );
    (api.generateMeetingMinutes as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);

    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={qc}>
        <MemoryRouter initialEntries={["/recordings/rec-123"]}>
          <Routes>
            <Route path="/recordings/:id" element={<RecordingDetail />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>,
    );
    await loaded();
    openMinutes();

    // Kick off a re-create; the picker goes busy (spinner shown on the Meeting type button).
    fireEvent.click(screen.getByRole("button", { name: /re-create minutes/i }));
    await waitFor(() =>
      expect(screen.getByRole("button", { name: "Meeting type" }).querySelector(".animate-spin")).toBeTruthy(),
    );

    // Backend finished, but NO SignalR event is delivered. Polling must still pick up the fresh minutes.
    done = true;
    await waitFor(
      () => expect(screen.getByRole("button", { name: "Meeting type" }).querySelector(".animate-spin")).toBeNull(),
      { timeout: 6000 },
    );
  }, 10000);

  // Bug 1: the shared <audio> must stay mounted on every tab. It used to live inside the Transcript tab's
  // content, so switching to Speakers unmounted it and per-speaker Play silently no-op'd (audioRef was null).
  it("plays a speaker's audio from the Speakers tab (audio element is mounted off-tab)", async () => {
    renderPage(base);
    await loaded();
    openTab("Speakers");

    fireEvent.click(screen.getByRole("button", { name: /play speaker_00's segments/i }));
    await waitFor(() => expect(api.audioUrl).toHaveBeenCalledWith("rec-123"));
  });

  // Bug 2: a recording that no longer exists (deleted here, on another device, or a stale list link) must
  // redirect to the home page instead of showing "Loading..." forever / a stale transcript.
  it("redirects to the home page when the recording is not found (404)", async () => {
    let path = "";
    function PathSpy() { path = useLocation().pathname; return null; }
    (api.getRecording as ReturnType<typeof vi.fn>).mockRejectedValue({
      isAxiosError: true, response: { status: 404 },
    });
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={qc}>
        <MemoryRouter initialEntries={["/recordings/rec-123"]}>
          <PathSpy />
          <Routes>
            <Route path="/" element={<div>HOME</div>} />
            <Route path="/recordings/:id" element={<RecordingDetail />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>,
    );
    await waitFor(() => expect(path).toBe("/"));
  });
});
