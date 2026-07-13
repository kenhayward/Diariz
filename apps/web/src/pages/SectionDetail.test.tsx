import { render, screen, fireEvent, waitFor, within } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter, Routes, Route } from "react-router-dom";
import { describe, it, expect, vi, beforeEach } from "vitest";
import type { SectionDetail as SectionDetailT, SectionFormulaResult } from "../lib/types";

// FolderRecordingList (rendered on the Overview tab) reads the current room + base path.
vi.mock("../lib/rooms", () => ({
  useRoom: () => ({ currentRoom: undefined, rooms: [] }),
  useRoomBasePath: () => "",
}));
// FormulaRunModal reads useAuth for the caller id (groups Personal vs Shared formulas).
vi.mock("../auth", () => ({ useAuth: () => ({ id: "u1", fullName: "Test User", email: "t@x.test" }) }));

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
