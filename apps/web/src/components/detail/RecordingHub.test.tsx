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
      { label: "SPEAKER_00", displayName: "Ada Lovelace", personId: null, identifiedAuto: false, isMultiSpeaker: false },
      { label: "SPEAKER_01", displayName: "Nadia Dubois", personId: null, identifiedAuto: false, isMultiSpeaker: false },
    ],
    current: {
      id: "t1",
      model: "large-v3",
      version: 1,
      language: "en",
      createdAt: "2026-06-30T19:50:00Z",
      segments: Array.from({ length: 142 }, (_, i) => ({
        id: `s${i}`,
        // Alternating, so the fixture is internally consistent: it declares two speakers above, and the
        // hub's speaker count now derives from who actually speaks rather than from how many Speaker
        // rows exist. Attributing all 142 segments to one of them made the fixture describe a recording
        // that could not exist.
        speaker: i % 2 === 0 ? "SPEAKER_00" : "SPEAKER_01",
        speakerDisplay: i % 2 === 0 ? "Ada Lovelace" : "Nadia Dubois",
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
      { id: "a1", text: "Draft consolidated catalogue", actor: "PA", deadline: "", ordinal: 0, completed: false, completedAt: null },
      { id: "a2", text: "Resolve ORION vs VEGA", actor: "AL", deadline: "", ordinal: 1, completed: false, completedAt: null },
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
  { id: "n1", text: "Check whether VEGA covers the audit trail.", capturedAtMs: 1000, ordinal: 0, createdAt: "x" },
];
const attachments: Attachment[] = [
  { id: "f1", kind: "File", name: "roadmap-matrix.pdf", contentType: "application/pdf", sizeBytes: 248_000, url: null, ordinal: 0 },
  { id: "f2", kind: "Url", name: "example.zoom.us/j/0000", contentType: null, sizeBytes: 0, url: "https://example.zoom.us/j/0000", ordinal: 1 },
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

function renderHub(
  h: ReturnType<typeof handlers>,
  over: Partial<RecordingDetail> = {},
  shots: unknown[] = [],
) {
  return wrap(
    <RecordingHub
      rec={rec(over)}
      notes={notes}
      attachments={attachments}
      formulaResults={formulaResults}
      shots={shots}
      meetingTypeTitle="Meeting minutes template"
      speakerNameOf={(l) => (l === "SPEAKER_00" ? "Ada Lovelace" : "Nadia Dubois")}
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
    expect(screen.getByText(/Draft consolidated catalogue/)).toBeTruthy();
    expect(screen.getByText("Check whether VEGA covers the audit trail.")).toBeTruthy();
    expect(screen.getByText("roadmap-matrix.pdf")).toBeTruthy();
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

  it("keeps the Notes subtitle to just the note count when there are no screenshots", () => {
    renderHub(h, {}, []);
    expect(screen.getByText("1 note")).toBeTruthy();
  });

  it("appends the screenshot count to the Notes subtitle once there are any", () => {
    renderHub(h, {}, [{ id: "sh1" }, { id: "sh2" }, { id: "sh3" }]);
    expect(screen.getByText("1 note · 3 screenshots")).toBeTruthy();
  });

  // The hub's width is `window - recordings panel - chat panel`, so it is not tied to the viewport at all:
  // on a wide window with the chat panel dragged out, an `xl:` media query kept three columns in a pane a
  // third that wide, and the tile contents (the "+ New"/"+ Add"/"Run" buttons, the nav chevrons) painted
  // outside the card border, because Tailwind's tracks are `minmax(0, 1fr)` and squeeze below min-content.
  // The columns are therefore gated on the hub's own width via container queries, the same way the capture
  // bar's cluster is (AudioSourceChip/RecordHero against CaptureBar's `@container`). jsdom computes no
  // geometry and loads no Tailwind CSS, so this only proves the gating classes are present; that nothing
  // spills was measured in a browser across pane widths.
  it("gates its columns on the hub's own width, not the window's", () => {
    renderHub(h);
    const classes = screen.getByTestId("hub-tiles").className.split(/\s+/);
    expect(classes).toContain("grid-cols-1");
    expect(classes).toContain("@lg:grid-cols-2");
    expect(classes).toContain("@3xl:grid-cols-3");
    // The viewport breakpoints are not a fallback: two rules would fight, and `xl:` winning on a wide
    // window is exactly the overflow this replaced.
    expect(classes).not.toContain("md:grid-cols-2");
    expect(classes).not.toContain("xl:grid-cols-3");
    // A container query measures the nearest `@container` ancestor. Without one it resolves against the
    // small-viewport default and every tile silently stays one per row, however wide the pane.
    expect(screen.getByTestId("hub").className.split(/\s+/)).toContain("@container");
  });

  // The last resort beneath the column gating: a track can still be narrower than a tile's natural header
  // (`minmax(0, 1fr)` zeroes a grid item's automatic minimum), and the header must give way rather than
  // spill. The title block is the part that gives - `min-w-0` plus a `truncate` on each line - so the
  // glyph and the action pill stay whole. Class presence only; the widths were measured in a browser, at
  // which point a 163px tile still drew its "+ New" pill entirely inside the card.
  it("lets a tile header shrink instead of painting outside the card", () => {
    renderHub(h);
    const title = screen.getByText("Notes");
    expect(title.className).toContain("truncate");
    expect(title.parentElement!.className.split(/\s+/)).toContain("min-w-0");
    // The subtitle truncates too: it is the longer of the two lines and would otherwise set the floor.
    expect(screen.getByText("1 note").className).toContain("truncate");
  });

  // A `truncate` that is a flex item shrinks only if it is also `min-w-0`: on its own, `min-width: auto`
  // floors the item at the nowrap text's full width, so the row runs straight through the card border
  // instead of ellipsising. Measured in the browser before the fix as 33px of a long action item hanging
  // outside a three-column tile.
  it("ellipsises a long preview row rather than letting it run past the card edge", () => {
    renderHub(h);
    for (const text of [/Draft consolidated catalogue/, "roadmap-matrix.pdf", "Risk register extract"]) {
      const row = screen.getByText(text);
      const classes = row.className.split(/\s+/);
      expect(classes).toContain("truncate");
      expect(classes).toContain("min-w-0");
    }
  });
});
