import { render, screen, fireEvent } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";
import type { ReactElement } from "react";
import RecordingHub from "./RecordingHub";
import type { Attachment, FormulaResult, MeetingNote, RecordingDetail } from "../../lib/types";

vi.mock("../../lib/api", () => ({
  api: { listMeetingTypes: vi.fn().mockResolvedValue([]) },
  apiErrorMessage: (_e: unknown, f: string) => f,
}));

const rec = (over: Partial<RecordingDetail> = {}): RecordingDetail =>
  ({
    id: "r1",
    title: "Standup",
    name: null,
    source: "Microphone",
    durationMs: 21 * 60_000,
    sizeBytes: 1000,
    status: "Summarized",
    error: null,
    createdAt: "2026-06-30T19:26:00Z",
    minSpeakers: null,
    maxSpeakers: null,
    speakerNames: {},
    speakers: [
      { label: "SPEAKER_00", displayName: "Ken Hayward", profileId: null, identifiedAuto: false, isMultiSpeaker: false },
      { label: "SPEAKER_01", displayName: "Marie Dubois", profileId: null, identifiedAuto: false, isMultiSpeaker: false },
    ],
    current: {
      id: "t1",
      model: "large-v3",
      version: 1,
      language: "en",
      createdAt: "2026-06-30T19:50:00Z",
      segments: Array.from({ length: 142 }, (_, i) => ({
        id: `s${i}`,
        speaker: "SPEAKER_00",
        speakerDisplay: "Ken Hayward",
        startMs: i * 1000,
        endMs: i * 1000 + 900,
        original: "hi",
        revised: null,
        text: "hi",
      })),
      processingMs: null,
    },
    summary: { model: "gpt", text: "The team agreed to consolidate the frameworks.", createdAt: "x", isUserEdited: false },
    meetingMinutes: null,
    actions: [
      { id: "a1", text: "Draft consolidated matrix", actor: "PA", deadline: "", ordinal: 0, completed: false, completedAt: null },
      { id: "a2", text: "Resolve AURA vs MARIE", actor: "AL", deadline: "", ordinal: 1, completed: false, completedAt: null },
      { id: "a3", text: "Circulate agenda", actor: "KH", deadline: "", ordinal: 2, completed: true, completedAt: "x" },
    ],
    actionsExtracted: true,
    hasAudio: true,
    audioProtectedAt: null,
    audioDeletedAt: null,
    audioScheduledDeletionAt: null,
    calendarLink: null,
    meetingTypeId: null,
    recordedByUserId: "u1",
    recordedByName: null,
    rooms: null,
    ...over,
  }) as RecordingDetail;

const notes: MeetingNote[] = [
  { id: "n1", text: "Check whether MARIE covers the audit trail.", capturedAtMs: 1000, ordinal: 0, createdAt: "x" },
];
const attachments: Attachment[] = [
  { id: "f1", kind: "File", name: "QnR-matrix.pdf", contentType: "application/pdf", sizeBytes: 248_000, url: null, ordinal: 0 },
  { id: "f2", kind: "Url", name: "3ds.zoom.us/j/8303", contentType: null, sizeBytes: 0, url: "https://3ds.zoom.us/j/8303", ordinal: 1 },
];
const formulaResults: FormulaResult[] = [
  {
    id: "fr1",
    recordingId: "r1",
    name: "Risk register extract",
    status: "Ready",
    error: null,
    createdByUserId: "u1",
    createdAt: "x",
    updatedAt: "x",
    origin: { kind: "diariz", personName: null, personPictureUrl: null },
  },
];

const handlers = () => ({
  onOpenSection: vi.fn(),
  onApplyMeetingType: vi.fn(),
  onEditSummary: vi.fn(),
  onResummarise: vi.fn(),
  onNewNote: vi.fn(),
  onAddFile: vi.fn(),
  onRunFormula: vi.fn(),
});

function wrap(ui: ReactElement) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={qc}>{ui}</QueryClientProvider>);
}

function renderHub(h: ReturnType<typeof handlers>, over: Partial<RecordingDetail> = {}) {
  return wrap(
    <RecordingHub
      rec={rec(over)}
      notes={notes}
      attachments={attachments}
      formulaResults={formulaResults}
      meetingTypeTitle="Meeting minutes template"
      speakerNameOf={(l) => (l === "SPEAKER_00" ? "Ken Hayward" : "Marie Dubois")}
      minutesRunning={false}
      hasTranscript
      isSummarizing={false}
      showRooms
      {...h}
    />,
  );
}

let h: ReturnType<typeof handlers>;
beforeEach(() => {
  h = handlers();
});

describe("RecordingHub", () => {
  it("shows the summary inline, without needing a hover or a click to reveal it", () => {
    renderHub(h);
    expect(screen.getByText("The team agreed to consolidate the frameworks.")).toBeTruthy();
  });

  it("shows each tile's real count", () => {
    renderHub(h);
    expect(screen.getByText("142 segments · 21 min")).toBeTruthy();
    expect(screen.getByText("2 open · 1 done")).toBeTruthy();
    expect(screen.getByText("2 identified")).toBeTruthy();
    expect(screen.getByText("1 note")).toBeTruthy();
    expect(screen.getByText("2 attached")).toBeTruthy();
    expect(screen.getByText("1 run")).toBeTruthy();
  });

  it("previews the section's real contents, not placeholder copy", () => {
    renderHub(h);
    expect(screen.getByText(/Draft consolidated matrix/)).toBeTruthy();
    expect(screen.getByText("Check whether MARIE covers the audit trail.")).toBeTruthy();
    expect(screen.getByText("QnR-matrix.pdf")).toBeTruthy();
    expect(screen.getByText("Risk register extract")).toBeTruthy();
  });

  it("ties the Formulas tile to the meeting-type template driving it", () => {
    renderHub(h);
    expect(screen.getByText("From Meeting minutes template")).toBeTruthy();
  });

  it("navigates into a section when its tile is clicked", () => {
    renderHub(h);
    fireEvent.click(screen.getByRole("button", { name: "Transcript" }));
    expect(h.onOpenSection).toHaveBeenCalledWith("transcript");
  });

  it("navigates on Enter, so a tile is reachable without a mouse", () => {
    renderHub(h);
    fireEvent.keyDown(screen.getByRole("button", { name: "Speakers" }), { key: "Enter" });
    expect(h.onOpenSection).toHaveBeenCalledWith("speakers");
  });

  it("opens the new-note editor from the tile's action without also navigating into Notes", () => {
    renderHub(h);
    fireEvent.click(screen.getByRole("button", { name: "New" }));
    expect(h.onNewNote).toHaveBeenCalled();
    expect(h.onOpenSection).not.toHaveBeenCalled();
  });

  it("opens the add-file flow from the tile's action without also navigating into Files", () => {
    renderHub(h);
    fireEvent.click(screen.getByRole("button", { name: "Add" }));
    expect(h.onAddFile).toHaveBeenCalled();
    expect(h.onOpenSection).not.toHaveBeenCalled();
  });

  it("opens the formula-run modal from the tile's action without also navigating into Formulas", () => {
    renderHub(h);
    fireEvent.click(screen.getByRole("button", { name: "Run" }));
    expect(h.onRunFormula).toHaveBeenCalled();
    expect(h.onOpenSection).not.toHaveBeenCalled();
  });

  it("opens the Minutes section from the hero's full-minutes link", () => {
    renderHub(h);
    fireEvent.click(screen.getByRole("button", { name: /Open full minutes/ }));
    expect(h.onOpenSection).toHaveBeenCalledWith("minutes");
  });

  it("warns how long the audio has left when a deletion is scheduled", () => {
    const in16Days = new Date(Date.now() + 16 * 24 * 60 * 60 * 1000).toISOString();
    renderHub(h, { audioScheduledDeletionAt: in16Days });
    expect(screen.getByText(/16d left/)).toBeTruthy();
  });

  it("says so plainly when the recording has no summary yet", () => {
    renderHub(h, { summary: null });
    expect(screen.getByText("No summary yet.")).toBeTruthy();
  });
});
