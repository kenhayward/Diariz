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
import { PreferencesFooterProvider, PreferencesFooterBar } from "./PreferencesFooter";

const settings = {
  apiBase: null, model: null, hasApiKey: false, defaultApiBase: null, defaultModel: null,
  serverHasApiKey: false, contextWindow: null, defaultContextWindow: 131072,
  toolsEnabled: false, defaultToolsEnabled: false, tools: [],
  reasoningEnabled: false, reasoningEffort: "medium", defaultReasoningEnabled: false, defaultReasoningEffort: "medium",
  placementMode: "SelectedFolder", placementSectionId: null,
  calendarAutoStopEnabled: false, calendarAutoStopAfterMinutes: 3, calendarSilenceStopSeconds: 30,
};

function renderSection() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <PreferencesFooterProvider>
        <RecordingsSection />
        <PreferencesFooterBar onClose={() => {}} />
      </PreferencesFooterProvider>
    </QueryClientProvider>,
  );
}

/// The panel has no Save of its own any more - it registers one with the modal footer.
const saveButton = () => screen.getByRole("button", { name: /save changes/i });

describe("RecordingsSection", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    (api.getUserSettings as ReturnType<typeof vi.fn>).mockResolvedValue(settings);
    (api.updateUserSettings as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    (api.listSections as ReturnType<typeof vi.fn>).mockResolvedValue([]);
  });

  it("offers the three placement modes, defaulting to the selected folder", async () => {
    renderSection();
    const selected = (await screen.findByRole("radio", { name: /the folder I'm looking at/i })) as HTMLInputElement;
    expect(selected.checked).toBe(true);
    // The folder picker only appears in "one fixed folder" mode.
    expect(screen.queryByLabelText("Filter folders")).toBeNull();
    fireEvent.click(screen.getByRole("radio", { name: /one fixed folder/i }));
    expect(screen.getByLabelText("Filter folders")).toBeTruthy();
  });

  it("heads the placement group and says what each choice does", async () => {
    renderSection();
    await screen.findByText("Where a new recording is filed");
    expect(screen.getByText("in your personal space")).toBeTruthy();

    expect(screen.getByText("Files into whichever folder is open in the list when you start recording.")).toBeTruthy();
    expect(screen.getByText("Everything lands in one place; file it into a folder afterwards.")).toBeTruthy();
    expect(screen.getByText("Always the same folder, wherever you happen to be.")).toBeTruthy();
  });

  it("marks the open-folder choice as the default", async () => {
    renderSection();
    const card = (await screen.findByRole("radio", { name: /the folder I'm looking at/i })).closest("label");
    expect(card?.textContent).toContain("Default");
  });

  it("keeps the three choices in one radio group so arrow keys still work", async () => {
    renderSection();
    await screen.findByText("Where a new recording is filed");
    const names = screen.getAllByRole("radio").map((r) => (r as HTMLInputElement).name);
    expect(new Set(names)).toEqual(new Set(["placement-mode"]));
  });

  it("labels the folder picker for assistive tech, associated with the visible 'Folder' heading", async () => {
    renderSection();
    fireEvent.click(await screen.findByRole("radio", { name: /one fixed folder/i }));
    // A grouping name, not just the picker's own internal control labels (Filter folders / Folders list) -
    // this is what replaces the old <select>'s aria-label="Folder".
    expect(screen.getByRole("group", { name: "Folder" })).toBeTruthy();
  });

  it("saves a specific-folder placement with the folder chosen from the picker, and nothing else", async () => {
    (api.listSections as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "sec-1", name: "Projects", parentId: null, position: 0 },
    ]);
    renderSection();
    fireEvent.click(await screen.findByRole("radio", { name: /one fixed folder/i }));
    fireEvent.click(await screen.findByLabelText("Select Projects"));
    fireEvent.click(saveButton());

    await waitFor(() => expect(api.updateUserSettings).toHaveBeenCalled());
    const arg = (api.updateUserSettings as ReturnType<typeof vi.fn>).mock.calls[0][0];
    expect(arg).toEqual({
      placementMode: "SpecificFolder", placementSectionId: "sec-1",
      calendarAutoStopEnabled: false, calendarAutoStopAfterMinutes: 3, calendarSilenceStopSeconds: 30,
    });
    expect(arg).not.toHaveProperty("apiBase");
    expect(arg).not.toHaveProperty("toolsEnabled");
  });

  it("sets the fixed folder to Ungrouped when the picker's root row is chosen", async () => {
    (api.listSections as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "sec-1", name: "Projects", parentId: null, position: 0 },
    ]);
    renderSection();
    fireEvent.click(await screen.findByRole("radio", { name: /one fixed folder/i }));
    // Move the value off its initial `null` first, so a root row whose `onChoose` never fired at all could
    // not accidentally pass this test by leaving the untouched initial value in place.
    fireEvent.click(await screen.findByLabelText("Select Projects"));
    fireEvent.click(screen.getByLabelText("Select Ungrouped"));
    fireEvent.click(saveButton());

    await waitFor(() =>
      expect(api.updateUserSettings).toHaveBeenCalledWith({ placementMode: "SpecificFolder", placementSectionId: null, calendarAutoStopEnabled: false, calendarAutoStopAfterMinutes: 3, calendarSilenceStopSeconds: 30 }),
    );
  });

  it("clears the fixed folder when a non-specific mode is chosen", async () => {
    (api.getUserSettings as ReturnType<typeof vi.fn>).mockResolvedValue({
      ...settings, placementMode: "SpecificFolder", placementSectionId: "sec-1",
    });
    renderSection();
    fireEvent.click(await screen.findByRole("radio", { name: /ungrouped/i }));
    fireEvent.click(saveButton());

    await waitFor(() =>
      expect(api.updateUserSettings).toHaveBeenCalledWith({ placementMode: "Ungrouped", placementSectionId: null, calendarAutoStopEnabled: false, calendarAutoStopAfterMinutes: 3, calendarSilenceStopSeconds: 30 }),
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
    fireEvent.click(await screen.findByRole("radio", { name: /one fixed folder/i }));

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
    await user.click(await screen.findByRole("radio", { name: /one fixed folder/i }));

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
    await user.click(saveButton());

    await waitFor(() =>
      expect(api.updateUserSettings).toHaveBeenCalledWith({ placementMode: "SpecificFolder", placementSectionId: "sec-1", calendarAutoStopEnabled: false, calendarAutoStopAfterMinutes: 3, calendarSilenceStopSeconds: 30 }),
    );
  });

  // ---- Recording from a calendar event ----

  describe("recording from a calendar event", () => {
    const autoStop = () => screen.getByRole("checkbox", { name: /end recording automatically/i });
    const afterMinutes = () => screen.getByLabelText(/minutes after the meeting ends/i) as HTMLInputElement;
    const silenceSeconds = () => screen.getByLabelText(/seconds of silence/i) as HTMLInputElement;

    it("shows the section with auto-stop off and both conditions at their defaults", async () => {
      renderSection();
      await screen.findByText("Recording from a Calendar Event");

      expect((autoStop() as HTMLInputElement).checked).toBe(false);
      expect(afterMinutes().value).toBe("3");
      expect(silenceSeconds().value).toBe("30");
    });

    it("disables both conditions until auto-stop is turned on", async () => {
      renderSection();
      await screen.findByText("Recording from a Calendar Event");

      // The conditions are meaningless on their own - they say HOW it stops, not WHETHER.
      expect(afterMinutes().disabled).toBe(true);
      expect(silenceSeconds().disabled).toBe(true);

      fireEvent.click(autoStop());
      expect(afterMinutes().disabled).toBe(false);
      expect(silenceSeconds().disabled).toBe(false);
    });

    it("saves the three settings alongside the placement", async () => {
      renderSection();
      await screen.findByText("Recording from a Calendar Event");

      fireEvent.click(autoStop());
      fireEvent.change(afterMinutes(), { target: { value: "10" } });
      fireEvent.change(silenceSeconds(), { target: { value: "90" } });
      fireEvent.click(saveButton());

      await waitFor(() => expect(api.updateUserSettings).toHaveBeenCalled());
      expect((api.updateUserSettings as ReturnType<typeof vi.fn>).mock.calls[0][0]).toEqual({
        placementMode: "SelectedFolder", placementSectionId: null,
        calendarAutoStopEnabled: true, calendarAutoStopAfterMinutes: 10, calendarSilenceStopSeconds: 90,
      });
    });

    it("seeds the controls from saved settings (round-trip)", async () => {
      (api.getUserSettings as ReturnType<typeof vi.fn>).mockResolvedValue({
        ...settings, calendarAutoStopEnabled: true, calendarAutoStopAfterMinutes: 7,
        calendarSilenceStopSeconds: 45,
      });
      renderSection();
      await screen.findByText("Recording from a Calendar Event");

      expect((autoStop() as HTMLInputElement).checked).toBe(true);
      expect(afterMinutes().value).toBe("7");
      expect(silenceSeconds().value).toBe("45");
      expect(afterMinutes().disabled).toBe(false);
    });

    it("sends the defaults rather than a blanked or zero duration", async () => {
      // Clearing a number input yields "" - saving that as 0 would stop a recording the instant it began.
      renderSection();
      await screen.findByText("Recording from a Calendar Event");

      fireEvent.click(autoStop());
      fireEvent.change(afterMinutes(), { target: { value: "" } });
      fireEvent.change(silenceSeconds(), { target: { value: "0" } });
      fireEvent.click(saveButton());

      await waitFor(() => expect(api.updateUserSettings).toHaveBeenCalled());
      const arg = (api.updateUserSettings as ReturnType<typeof vi.fn>).mock.calls[0][0];
      expect(arg.calendarAutoStopAfterMinutes).toBe(3);
      expect(arg.calendarSilenceStopSeconds).toBe(30);
    });
  });

  it("has no Save of its own - it registers one with the modal footer", async () => {
    renderSection();
    await screen.findByRole("radio", { name: /the folder I'm looking at/i });
    expect(screen.queryByRole("button", { name: /^save$/i })).toBeNull();
    expect(saveButton()).toBeTruthy();
  });

  it("reports unsaved changes to the footer, and clears them on a successful save", async () => {
    renderSection();
    fireEvent.click(await screen.findByRole("radio", { name: /ungrouped/i }));
    expect(screen.getByText("Unsaved changes")).toBeTruthy();

    fireEvent.click(saveButton());
    await waitFor(() => expect(screen.getByText("Saved")).toBeTruthy());
    expect(screen.queryByText("Unsaved changes")).toBeNull();
  });

  it("clears the unsaved indicator when an edit is undone by hand", async () => {
    renderSection();
    fireEvent.click(await screen.findByRole("radio", { name: /ungrouped/i }));
    expect(screen.getByText("Unsaved changes")).toBeTruthy();

    // Back to the value that was loaded - there is nothing to save, so the footer must say so.
    fireEvent.click(screen.getByRole("radio", { name: /the folder I'm looking at/i }));
    expect(screen.queryByText("Unsaved changes")).toBeNull();
  });

  it("surfaces a save failure in the footer", async () => {
    (api.updateUserSettings as ReturnType<typeof vi.fn>).mockRejectedValue(new Error("boom"));
    renderSection();
    fireEvent.click(await screen.findByRole("radio", { name: /ungrouped/i }));
    fireEvent.click(saveButton());
    await waitFor(() => expect(screen.getByText(/boom/)).toBeTruthy());
  });

  it("registers no Save with the footer while settings are still loading, so it cannot overwrite real settings with defaults", async () => {
    // Never resolves - pins the panel in its pre-data state, where a click on a live Save button would
    // PUT the component's hardcoded initial state (not the user's real settings) before they even arrive.
    (api.getUserSettings as ReturnType<typeof vi.fn>).mockReturnValue(new Promise(() => {}));
    renderSection();

    // Give any in-flight microtasks a turn, then confirm nothing that looks like Save is reachable.
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(screen.queryByRole("button", { name: /save changes/i })).toBeNull();
    expect(api.updateUserSettings).not.toHaveBeenCalled();
  });

  it("ignores a stale saved folder id when the loaded mode isn't Specific folder, so opening the panel isn't already 'Unsaved'", async () => {
    // A row a server could plausibly have (a leftover section id from before the mode was switched away
    // from SpecificFolder). If the baseline seed didn't mode-gate this the same way the save payload does,
    // the panel would open reading "Unsaved changes" before the user touched anything.
    (api.getUserSettings as ReturnType<typeof vi.fn>).mockResolvedValue({
      ...settings, placementMode: "Ungrouped", placementSectionId: "stale-sec",
    });
    renderSection();
    await screen.findByRole("radio", { name: /the folder I'm looking at/i });
    expect(screen.queryByText("Unsaved changes")).toBeNull();
  });
});
