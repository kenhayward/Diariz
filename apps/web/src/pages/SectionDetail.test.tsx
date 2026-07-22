import { render, screen, fireEvent, waitFor, within } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter, Routes, Route } from "react-router-dom";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { RoomPermission } from "../lib/types";
import type { RoomListItem, SectionDetail as SectionDetailT, SectionFormulaResult } from "../lib/types";

// FolderRecordingList (rendered on the Overview tab) reads the current room + base path. `rooms` is mutable so
// individual tests can seed the caller's room memberships (with permissions) to exercise the folder-attachment
// ManageContents gate, which resolves against section.roomId - not `currentRoom` (the URL-resolved room).
const roomsState: { rooms: RoomListItem[] } = { rooms: [] };
vi.mock("../lib/rooms", () => ({
  useRoom: () => ({ currentRoom: undefined, rooms: roomsState.rooms }),
  useRoomBasePath: () => "",
}));
// FormulaRunModal reads useAuth for the caller id (groups Personal vs Shared formulas).
vi.mock("../auth", () => ({ useAuth: () => ({ id: "u1", fullName: "Test User", email: "t@x.test" }) }));

// Copy-link wiring: keep folderUrl real (it's the thing under test - does the page pass the right roomBasePath
// into it) but stub copyRichLink so no real clipboard API is touched and we can inspect the URL it was given.
// vi.mock's factory is hoisted above top-level const declarations, so the mock fn is created via vi.hoisted.
const { copyRichLink } = vi.hoisted(() => ({ copyRichLink: vi.fn().mockResolvedValue(true) }));
vi.mock("../lib/clipboard", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../lib/clipboard")>();
  return { ...actual, copyRichLink };
});

vi.mock("../lib/api", () => ({
  api: {
    getSection: vi.fn(),
    renameSection: vi.fn(),
    listSectionActions: vi.fn().mockResolvedValue([]),
    listSectionNotes: vi.fn().mockResolvedValue([]),
    listSectionAttachments: vi.fn().mockResolvedValue([]),
    listFolderAttachments: vi.fn().mockResolvedValue([]),
    listRecordings: vi.fn().mockResolvedValue([]),
    listSections: vi.fn().mockResolvedValue([]),
    generateSectionSummary: vi.fn(),
    generateSectionMinutes: vi.fn(),
    updateSectionSummary: vi.fn(),
    updateSectionMinutes: vi.fn(),
    updateAction: vi.fn(),
    completeActions: vi.fn(),
    deleteAction: vi.fn(),
    updateNote: vi.fn(),
    deleteNote: vi.fn(),
    deleteAttachment: vi.fn(),
    // Formulas (section-scoped)
    listSectionFormulaResults: vi.fn().mockResolvedValue([]),
    getSectionFormulaResultText: vi.fn().mockResolvedValue(""),
    runSectionFormula: vi.fn(),
    updateSectionFormulaResult: vi.fn(),
    deleteSectionFormulaResult: vi.fn(),
    emailSectionFormulaResult: vi.fn(),
    downloadSectionFormulaResult: vi.fn(),
    // FormulaRunModal
    listFormulas: vi.fn().mockResolvedValue([]),
  },
  apiErrorMessage: (e: unknown, fallback?: string) => fallback ?? String(e),
}));

import { api } from "../lib/api";
import SectionDetail from "./SectionDetail";

const base: SectionDetailT = {
  id: "sec-1",
  name: "My Folder",
  parentId: null,
  roomId: "personal-1",
  stats: { transcriptCount: 2, totalDurationMs: 60_000, firstRecordingAt: null, lastRecordingAt: null },
  summary: null,
  minutes: null,
  meetingTypeId: null,
};

const result = (over: Partial<SectionFormulaResult> = {}): SectionFormulaResult => ({
  id: "sfr-1",
  sectionId: "sec-1",
  name: "Risk Register",
  status: "Ready",
  error: null,
  createdByUserId: "u1",
  createdAt: new Date("2026-06-26T12:00:00Z").toISOString(),
  updatedAt: new Date("2026-06-26T12:00:00Z").toISOString(),
  origin: { kind: "personal", personName: "You", personPictureUrl: null },
  ...over,
});

function renderPage(section: SectionDetailT = base) {
  (api.getSection as ReturnType<typeof vi.fn>).mockResolvedValue(section);
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter initialEntries={["/sections/sec-1"]}>
        <Routes>
          <Route path="/sections/:id" element={<SectionDetail />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

const loaded = () => screen.findByRole("tab", { name: /overview/i });
const openTab = (name: RegExp) => fireEvent.click(screen.getByRole("tab", { name }));

describe("SectionDetail formulas tab", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    (api.listSectionFormulaResults as ReturnType<typeof vi.fn>).mockResolvedValue([]);
    (api.getSectionFormulaResultText as ReturnType<typeof vi.fn>).mockResolvedValue("body");
    (api.listFormulas as ReturnType<typeof vi.fn>).mockResolvedValue([]);
  });

  it("has a Formulas tab that lists the section's formula results", async () => {
    (api.listSectionFormulaResults as ReturnType<typeof vi.fn>).mockResolvedValue([result()]);
    renderPage();
    await loaded();
    openTab(/formulas/i);

    expect(await screen.findByText("Risk Register")).toBeTruthy();
    expect(api.listSectionFormulaResults).toHaveBeenCalledWith("sec-1");
  });

  it("renders a Generating result as a non-openable status row (poll-in-flight state)", async () => {
    (api.listSectionFormulaResults as ReturnType<typeof vi.fn>).mockResolvedValue([
      result({ status: "Generating" }),
    ]);
    renderPage();
    await loaded();
    openTab(/formulas/i);

    // The generating row shows its name but is NOT a selectable button, so Open/Delete stay disabled.
    expect(await screen.findByText("Risk Register")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Risk Register" })).toBeNull();
    expect((screen.getByRole("button", { name: /^open$/i }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole("button", { name: /^delete$/i }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("opens the Run modal, runs a formula, then refreshes and selects the new result", async () => {
    (api.listFormulas as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "f1", name: "Risk Register", description: null, scope: "Personal", ownerUserId: "u1" },
    ]);
    const created = result({ id: "sfr-9", name: "Risk Register" });
    (api.runSectionFormula as ReturnType<typeof vi.fn>).mockResolvedValue(created);

    renderPage();
    await loaded();
    openTab(/formulas/i);

    // Open the picker and pick the formula.
    fireEvent.click(screen.getByRole("button", { name: /run formula/i }));
    const dialog = await screen.findByRole("dialog", { name: /run a formula/i });
    // After running, the refetch returns the new result so the panel/selection can resolve it.
    (api.listSectionFormulaResults as ReturnType<typeof vi.fn>).mockResolvedValue([created]);
    fireEvent.click(await within(dialog).findByRole("button", { name: /risk register/i }));

    await waitFor(() => expect(api.runSectionFormula).toHaveBeenCalledWith("sec-1", "f1"));
    // Selection wired through: the toolbar's Delete becomes enabled once a result is selected.
    await waitFor(() =>
      expect((screen.getByRole("button", { name: /^delete$/i }) as HTMLButtonElement).disabled).toBe(false),
    );
  });

  it("downloads the selected result from the toolbar", async () => {
    (api.listSectionFormulaResults as ReturnType<typeof vi.fn>).mockResolvedValue([result()]);
    (api.downloadSectionFormulaResult as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    renderPage();
    await loaded();
    openTab(/formulas/i);

    fireEvent.click(await screen.findByText("Risk Register"));
    fireEvent.click(screen.getByRole("button", { name: /^download$/i }));
    await waitFor(() => expect(api.downloadSectionFormulaResult).toHaveBeenCalledWith("sec-1", "sfr-1"));
  });

  it("deletes the selected result from the toolbar after confirming", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    (api.listSectionFormulaResults as ReturnType<typeof vi.fn>).mockResolvedValue([result()]);
    (api.deleteSectionFormulaResult as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    renderPage();
    await loaded();
    openTab(/formulas/i);

    fireEvent.click(await screen.findByText("Risk Register"));
    fireEvent.click(screen.getByRole("button", { name: /^delete$/i }));
    await waitFor(() => expect(api.deleteSectionFormulaResult).toHaveBeenCalledWith("sec-1", "sfr-1"));
  });
});

describe("SectionDetail formula-result mutation gating", () => {
  // Mirrors SectionFormulaResultsController.CanEditAsync: the result's creator OR a member with ManageContents
  // in the FOLDER'S OWN room (section.roomId) - resolved against the folder's real room, never useRoom()'s
  // URL-derived one (which falls back to the caller's personal room, holding every permission, on the
  // room-less legacy /sections/:id link - see the folder-attachment gating tests above for that trap).
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    (api.listSectionFormulaResults as ReturnType<typeof vi.fn>).mockResolvedValue([]);
    (api.getSectionFormulaResultText as ReturnType<typeof vi.fn>).mockResolvedValue("body");
    (api.listFormulas as ReturnType<typeof vi.fn>).mockResolvedValue([]);
  });

  it("enables Delete for the result's creator, even without ManageContents in the folder's room", async () => {
    roomsState.rooms = [
      { id: "personal-1", name: "You", kind: 0, icon: null, color: null, isPersonal: true, permissions: 63, sectionCount: 0, recordingCount: 0 },
      { id: "shared-1", name: "Eng", kind: 1, icon: null, color: null, isPersonal: false, permissions: RoomPermission.CreateRecording, sectionCount: 0, recordingCount: 0 },
    ];
    (api.listSectionFormulaResults as ReturnType<typeof vi.fn>).mockResolvedValue([
      result({ createdByUserId: "u1" }), // "u1" is the caller (useAuth mock)
    ]);
    renderPage({ ...base, roomId: "shared-1" });
    await loaded();
    openTab(/formulas/i);

    fireEvent.click(await screen.findByText("Risk Register"));
    expect((screen.getByRole("button", { name: /^open$/i }) as HTMLButtonElement).disabled).toBe(false);
    expect((screen.getByRole("button", { name: /^download$/i }) as HTMLButtonElement).disabled).toBe(false);
    expect((screen.getByRole("button", { name: /^email$/i }) as HTMLButtonElement).disabled).toBe(false);
    expect((screen.getByRole("button", { name: /^delete$/i }) as HTMLButtonElement).disabled).toBe(false);
  });

  it("disables Delete for a non-creator without ManageContents in the folder's room", async () => {
    roomsState.rooms = [
      { id: "personal-1", name: "You", kind: 0, icon: null, color: null, isPersonal: true, permissions: 63, sectionCount: 0, recordingCount: 0 },
      { id: "shared-1", name: "Eng", kind: 1, icon: null, color: null, isPersonal: false, permissions: RoomPermission.CreateRecording, sectionCount: 0, recordingCount: 0 },
    ];
    (api.listSectionFormulaResults as ReturnType<typeof vi.fn>).mockResolvedValue([
      result({ createdByUserId: "u-someone-else" }),
    ]);
    renderPage({ ...base, roomId: "shared-1" }); // note: room-less legacy URL, real room resolved from section.roomId
    await loaded();
    openTab(/formulas/i);

    fireEvent.click(await screen.findByText("Risk Register"));
    // Reads stay available.
    expect((screen.getByRole("button", { name: /^open$/i }) as HTMLButtonElement).disabled).toBe(false);
    expect((screen.getByRole("button", { name: /^download$/i }) as HTMLButtonElement).disabled).toBe(false);
    expect((screen.getByRole("button", { name: /^email$/i }) as HTMLButtonElement).disabled).toBe(false);
    // The mutation is not.
    expect((screen.getByRole("button", { name: /^delete$/i }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("enables Delete for a non-creator who holds ManageContents in the folder's room", async () => {
    roomsState.rooms = [
      { id: "personal-1", name: "You", kind: 0, icon: null, color: null, isPersonal: true, permissions: 63, sectionCount: 0, recordingCount: 0 },
      { id: "shared-1", name: "Eng", kind: 1, icon: null, color: null, isPersonal: false, permissions: RoomPermission.ManageContents, sectionCount: 0, recordingCount: 0 },
    ];
    (api.listSectionFormulaResults as ReturnType<typeof vi.fn>).mockResolvedValue([
      result({ createdByUserId: "u-someone-else" }),
    ]);
    renderPage({ ...base, roomId: "shared-1" });
    await loaded();
    openTab(/formulas/i);

    fireEvent.click(await screen.findByText("Risk Register"));
    expect((screen.getByRole("button", { name: /^delete$/i }) as HTMLButtonElement).disabled).toBe(false);
  });
});

describe("SectionDetail copy-link room prefix", () => {
  // section.roomId resolves the folder's OWN room from `rooms` (see the sectionRoom/roomBasePath comment in
  // SectionDetail.tsx) - the copied link must carry that room's prefix, not the URL's, and must carry none at
  // all for a personal folder even though the folder-attachments describe block above already covers the
  // room-less legacy URL for a DIFFERENT concern (write-control gating).
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    roomsState.rooms = [];
  });

  it("copies a shared folder's link with its room prefix", async () => {
    roomsState.rooms = [
      { id: "shared-1", name: "Eng", kind: 1, icon: null, color: null, isPersonal: false, permissions: RoomPermission.ManageContents, sectionCount: 0, recordingCount: 0 },
    ];
    renderPage({ ...base, roomId: "shared-1" });
    await loaded();

    fireEvent.click(screen.getByLabelText("Copy link"));

    await waitFor(() => expect(copyRichLink).toHaveBeenCalled());
    const [url] = copyRichLink.mock.calls[0];
    expect(url).toBe(`${window.location.origin}/rooms/shared-1/sections/sec-1`);
  });

  it("copies a personal folder's link with no room prefix", async () => {
    roomsState.rooms = [
      { id: "personal-1", name: "You", kind: 0, icon: null, color: null, isPersonal: true, permissions: 63, sectionCount: 0, recordingCount: 0 },
    ];
    renderPage({ ...base, roomId: "personal-1" });
    await loaded();

    fireEvent.click(screen.getByLabelText("Copy link"));

    await waitFor(() => expect(copyRichLink).toHaveBeenCalled());
    const [url] = copyRichLink.mock.calls[0];
    expect(url).toBe(`${window.location.origin}/sections/sec-1`);
  });
});

describe("SectionDetail folder-attachment room gating", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    roomsState.rooms = [];
  });

  it("hides folder-attachment write controls for a shared-room member without ManageContents, even at the room-less legacy URL", async () => {
    // The caller's personal room grants every permission - if the gate ever fell back to it (the historical
    // bug: useRoom() resolves the URL's room, which falls back to personal when the URL carries none), these
    // write controls would wrongly render. Gating on section.roomId instead must resolve the SHARED room here.
    roomsState.rooms = [
      { id: "personal-1", name: "You", kind: 0, icon: null, color: null, isPersonal: true, permissions: 63, sectionCount: 0, recordingCount: 0 },
      { id: "shared-1", name: "Eng", kind: 1, icon: null, color: null, isPersonal: false, permissions: RoomPermission.CreateRecording, sectionCount: 0, recordingCount: 0 },
    ];
    (api.listFolderAttachments as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "att-1", kind: "File", name: "spec.pdf", contentType: "application/pdf", sizeBytes: 100, url: null, ordinal: 0 },
    ]);

    renderPage({ ...base, roomId: "shared-1" }); // note: rendered at the room-less "/sections/sec-1" route
    await loaded();
    openTab(/attachments/i);

    expect(await screen.findByText("spec.pdf")).toBeTruthy();
    expect(screen.queryByText("Add file")).toBeNull();
    expect(screen.queryByText("Add URL")).toBeNull();
    expect(screen.queryByText("Remove")).toBeNull();
  });

  it("shows folder-attachment write controls for a shared-room member with ManageContents, even at the room-less legacy URL", async () => {
    roomsState.rooms = [
      { id: "personal-1", name: "You", kind: 0, icon: null, color: null, isPersonal: true, permissions: 63, sectionCount: 0, recordingCount: 0 },
      { id: "shared-1", name: "Eng", kind: 1, icon: null, color: null, isPersonal: false, permissions: RoomPermission.ManageContents, sectionCount: 0, recordingCount: 0 },
    ];
    (api.listFolderAttachments as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "att-1", kind: "File", name: "spec.pdf", contentType: "application/pdf", sizeBytes: 100, url: null, ordinal: 0 },
    ]);

    renderPage({ ...base, roomId: "shared-1" });
    await loaded();
    openTab(/attachments/i);

    // A manager gets a rename input (value, not text content) instead of a plain text name.
    expect(await screen.findByDisplayValue("spec.pdf")).toBeTruthy();
    expect(screen.getByText("Add file")).toBeTruthy();
    expect(screen.getByText("Add URL")).toBeTruthy();
    expect(screen.getByText("Remove")).toBeTruthy();
  });
});
