import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("../lib/api", () => ({
  api: { getUserSettings: vi.fn(), updateUserSettings: vi.fn(), listSections: vi.fn() },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../lib/api";
import RecordingsSection from "./RecordingsSection";

const settings = {
  apiBase: null, model: null, hasApiKey: false, defaultApiBase: null, defaultModel: null,
  serverHasApiKey: false, contextWindow: null, defaultContextWindow: 131072,
  toolsEnabled: false, defaultToolsEnabled: false, tools: [],
  reasoningEnabled: false, reasoningEffort: "medium", defaultReasoningEnabled: false, defaultReasoningEffort: "medium",
  placementMode: "SelectedFolder", placementSectionId: null,
};

function renderSection() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <RecordingsSection />
    </QueryClientProvider>,
  );
}

describe("RecordingsSection", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    (api.getUserSettings as ReturnType<typeof vi.fn>).mockResolvedValue(settings);
    (api.updateUserSettings as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    (api.listSections as ReturnType<typeof vi.fn>).mockResolvedValue([]);
  });

  it("offers the three placement modes, defaulting to the selected folder", async () => {
    renderSection();
    const selected = (await screen.findByRole("radio", { name: /currently selected folder/i })) as HTMLInputElement;
    expect(selected.checked).toBe(true);
    // The folder picker only appears in "specific folder" mode.
    expect(screen.queryByLabelText("Filter folders")).toBeNull();
    fireEvent.click(screen.getByRole("radio", { name: /specific folder/i }));
    expect(screen.getByLabelText("Filter folders")).toBeTruthy();
  });

  it("labels the folder picker for assistive tech, associated with the visible 'Folder' heading", async () => {
    renderSection();
    fireEvent.click(await screen.findByRole("radio", { name: /specific folder/i }));
    // A grouping name, not just the picker's own internal control labels (Filter folders / Folders list) -
    // this is what replaces the old <select>'s aria-label="Folder".
    expect(screen.getByRole("group", { name: "Folder" })).toBeTruthy();
  });

  it("saves a specific-folder placement with the folder chosen from the picker, and nothing else", async () => {
    (api.listSections as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "sec-1", name: "Projects", parentId: null, position: 0 },
    ]);
    renderSection();
    fireEvent.click(await screen.findByRole("radio", { name: /specific folder/i }));
    fireEvent.click(await screen.findByLabelText("Select Projects"));
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await waitFor(() => expect(api.updateUserSettings).toHaveBeenCalled());
    const arg = (api.updateUserSettings as ReturnType<typeof vi.fn>).mock.calls[0][0];
    expect(arg).toEqual({ placementMode: "SpecificFolder", placementSectionId: "sec-1" });
    expect(arg).not.toHaveProperty("apiBase");
    expect(arg).not.toHaveProperty("toolsEnabled");
  });

  it("sets the fixed folder to Ungrouped when the picker's root row is chosen", async () => {
    (api.listSections as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "sec-1", name: "Projects", parentId: null, position: 0 },
    ]);
    renderSection();
    fireEvent.click(await screen.findByRole("radio", { name: /specific folder/i }));
    // Move the value off its initial `null` first, so a root row whose `onChoose` never fired at all could
    // not accidentally pass this test by leaving the untouched initial value in place.
    fireEvent.click(await screen.findByLabelText("Select Projects"));
    fireEvent.click(screen.getByLabelText("Select Ungrouped"));
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await waitFor(() =>
      expect(api.updateUserSettings).toHaveBeenCalledWith({ placementMode: "SpecificFolder", placementSectionId: null }),
    );
  });

  it("clears the fixed folder when a non-specific mode is chosen", async () => {
    (api.getUserSettings as ReturnType<typeof vi.fn>).mockResolvedValue({
      ...settings, placementMode: "SpecificFolder", placementSectionId: "sec-1",
    });
    renderSection();
    fireEvent.click(await screen.findByRole("radio", { name: /ungrouped/i }));
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await waitFor(() =>
      expect(api.updateUserSettings).toHaveBeenCalledWith({ placementMode: "Ungrouped", placementSectionId: null }),
    );
  });

  it("marks the previously saved folder as selected in the picker (round-trip)", async () => {
    (api.getUserSettings as ReturnType<typeof vi.fn>).mockResolvedValue({
      ...settings, placementMode: "SpecificFolder", placementSectionId: "sec-1",
    });
    (api.listSections as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "sec-1", name: "Projects", parentId: null, position: 0 },
    ]);
    renderSection();
    expect((await screen.findByLabelText("Select Projects")).getAttribute("aria-current")).toBe("true");
    expect(screen.getByLabelText("Select Ungrouped").getAttribute("aria-current")).toBeNull();
  });

  it("shows the previously saved folder's full path when it is nested too deep to appear at the picker's root (regression: a <select> always shows its value)", async () => {
    (api.getUserSettings as ReturnType<typeof vi.fn>).mockResolvedValue({
      ...settings, placementMode: "SpecificFolder", placementSectionId: "phase2",
    });
    (api.listSections as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "customers", name: "Customers", parentId: null, position: 0 },
      { id: "acme", name: "Acme Corp", parentId: "customers", position: 0 },
      { id: "falcon", name: "Project Falcon", parentId: "acme", position: 0 },
      { id: "phase2", name: "Phase 2", parentId: "falcon", position: 0 },
    ]);
    renderSection();
    fireEvent.click(await screen.findByRole("radio", { name: /specific folder/i }));

    // Opening Settings lands on the root drill position, four levels above the saved folder - its row is
    // not rendered there at all, so the old bug showed nothing marked as current.
    expect(screen.getByText("Selected: Customers › Acme Corp › Project Falcon › Phase 2")).toBeTruthy();
  });

  it("is keyboard operable: Tab alone reaches a folder row, and Enter chooses it", async () => {
    (api.listSections as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "sec-1", name: "Projects", parentId: null, position: 0 },
    ]);
    const user = userEvent.setup();
    renderSection();
    await user.click(await screen.findByRole("radio", { name: /specific folder/i }));

    // Real Tab presses only, no `.focus()` shortcut - proves the whole chain (radio group -> picker filter
    // box -> picker rows) is reachable by keyboard alone, not just that the target element is focusable.
    await user.tab(); // filter input
    expect(document.activeElement).toBe(screen.getByLabelText("Filter folders"));
    await user.tab(); // the root "Ungrouped" row (no back/breadcrumb control at the top drill level)
    expect(document.activeElement).toBe(screen.getByLabelText("Select Ungrouped"));
    await user.tab(); // "Projects" row body (drills, does not choose)
    expect(document.activeElement).toBe(screen.getByLabelText("Open Projects"));
    await user.tab(); // "Projects" row's separate select control
    expect(document.activeElement).toBe(screen.getByLabelText("Select Projects"));

    await user.keyboard("{Enter}");
    await user.click(screen.getByRole("button", { name: /^save$/i }));

    await waitFor(() =>
      expect(api.updateUserSettings).toHaveBeenCalledWith({ placementMode: "SpecificFolder", placementSectionId: "sec-1" }),
    );
  });
});
