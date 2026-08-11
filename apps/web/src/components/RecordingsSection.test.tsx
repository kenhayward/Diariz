import { render, screen, fireEvent, waitFor } from "@testing-library/react";
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
    // Adapted for the folder picker becoming its own dialog (Task 5): the picker (and its "Filter
    // folders" box) is no longer inline, so what "one fixed folder" mode reveals is now the path chip +
    // Change control, and the filter box only appears once Change opens the dialog.
    renderSection();
    const selected = (await screen.findByRole("radio", { name: /the folder I'm looking at/i })) as HTMLInputElement;
    expect(selected.checked).toBe(true);
    // The folder picker only appears in "one fixed folder" mode.
    expect(screen.queryByRole("button", { name: /^change/i })).toBeNull();
    fireEvent.click(screen.getByRole("radio", { name: /one fixed folder/i }));
    expect(screen.getByRole("button", { name: /^change/i })).toBeTruthy();
    expect(screen.queryByLabelText("Filter folders")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: /^change/i }));
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

  it("shows the chosen folder as a path chip with a Change control, only in fixed-folder mode", async () => {
    (api.getUserSettings as ReturnType<typeof vi.fn>).mockResolvedValue({
      ...settings, placementMode: "SpecificFolder", placementSectionId: "acme",
    });
    (api.listSections as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "customers", name: "Customers", parentId: null, position: 0 },
      { id: "acme", name: "Acme Corp", parentId: "customers", position: 0 },
    ]);
    renderSection();

    expect(await screen.findByText("Customers › Acme Corp")).toBeTruthy();
    // The picker is a dialog now - nothing of it is on the panel until asked for.
    expect(screen.queryByLabelText("Filter folders")).toBeNull();

    fireEvent.click(screen.getByRole("radio", { name: /always ungrouped/i }));
    expect(screen.queryByRole("button", { name: /^change/i })).toBeNull();
  });

  it("opens the picker from Change, and applies the choice when it closes", async () => {
    (api.listSections as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "sec-1", name: "Projects", parentId: null, position: 0 },
    ]);
    renderSection();
    fireEvent.click(await screen.findByRole("radio", { name: /one fixed folder/i }));
    fireEvent.click(screen.getByRole("button", { name: /^change/i }));

    expect(screen.getByRole("dialog", { name: "Choose a folder" })).toBeTruthy();
    fireEvent.click(screen.getByLabelText("Select Projects"));
    fireEvent.click(screen.getByRole("button", { name: "Done" }));

    expect(screen.queryByRole("dialog", { name: "Choose a folder" })).toBeNull();
    expect(screen.getByText("Projects")).toBeTruthy();
    fireEvent.click(saveButton());
    await waitFor(() =>
      expect(api.updateUserSettings).toHaveBeenCalledWith({
        placementMode: "SpecificFolder", placementSectionId: "sec-1",
        calendarAutoStopEnabled: false, calendarAutoStopAfterMinutes: 3, calendarSilenceStopSeconds: 30,
      }),
    );
  });

  it("returns focus to Change when the picker closes", async () => {
    renderSection();
    fireEvent.click(await screen.findByRole("radio", { name: /one fixed folder/i }));
    const change = screen.getByRole("button", { name: /^change/i });
    fireEvent.click(change);
    fireEvent.click(screen.getByRole("button", { name: "Done" }));
    expect(document.activeElement).toBe(change);
  });

  it("saves a specific-folder placement with the folder chosen from the picker, and nothing else", async () => {
    (api.listSections as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "sec-1", name: "Projects", parentId: null, position: 0 },
    ]);
    renderSection();
    fireEvent.click(await screen.findByRole("radio", { name: /one fixed folder/i }));
    fireEvent.click(screen.getByRole("button", { name: /^change/i }));
    fireEvent.click(await screen.findByLabelText("Select Projects"));
    fireEvent.click(screen.getByRole("button", { name: "Done" }));
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
    fireEvent.click(screen.getByRole("button", { name: /^change/i }));
    // Move the value off its initial `null` first, so a root row whose `onChoose` never fired at all could
    // not accidentally pass this test by leaving the untouched initial value in place.
    fireEvent.click(await screen.findByLabelText("Select Projects"));
    fireEvent.click(screen.getByLabelText("Select Ungrouped"));
    fireEvent.click(screen.getByRole("button", { name: "Done" }));
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
    // Narrower than a plain /ungrouped/i: the SpecificFolder card's own reveal row now sits inside its
    // <label> too (Task 5), so its chosen-path chip can read "Ungrouped" and fold that word into the
    // SpecificFolder radio's accessible name - a bare /ungrouped/i would then match two radios.
    fireEvent.click(await screen.findByRole("radio", { name: /always ungrouped/i }));
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
    fireEvent.click(await screen.findByRole("button", { name: /^change/i }));
    expect((await screen.findByLabelText("Select Projects")).getAttribute("aria-current")).toBe("true");
    expect(screen.getByLabelText("Select Ungrouped").getAttribute("aria-current")).toBeNull();
  });

  // ---- Recording from a calendar event ----

  describe("recording from a calendar event", () => {
    const autoStop = () => screen.getByRole("switch", { name: /let a calendar meeting end its own recording/i });
    const afterMinutes = () => screen.getByLabelText(/minutes after the meeting ends/i) as HTMLInputElement;
    const silenceSeconds = () => screen.getByLabelText(/seconds of silence/i) as HTMLInputElement;

    it("shows the card with the switch off and no duration fields at all", async () => {
      renderSection();
      await screen.findByText("Let a calendar meeting end its own recording");

      expect(autoStop().getAttribute("aria-checked")).toBe("false");
      // Absent, not disabled: a field you cannot use should not be on screen looking editable.
      expect(screen.queryByLabelText(/minutes after the meeting ends/i)).toBeNull();
      expect(screen.queryByLabelText(/seconds of silence/i)).toBeNull();
    });

    it("says the option applies only to a recording started from the calendar", async () => {
      renderSection();
      expect(
        await screen.findByText(
          /Only when you join the meeting from your calendar.*not cut short by this option\./,
        ),
      ).toBeTruthy();
    });

    it("reveals both durations at their defaults when the switch is turned on", async () => {
      renderSection();
      await screen.findByText("Let a calendar meeting end its own recording");

      fireEvent.click(autoStop());
      expect(autoStop().getAttribute("aria-checked")).toBe("true");
      expect(afterMinutes().value).toBe("3");
      expect(silenceSeconds().value).toBe("30");
    });

    it("works the example through from the two values, and recomputes it live", async () => {
      renderSection();
      await screen.findByText("Let a calendar meeting end its own recording");
      fireEvent.click(autoStop());

      expect(
        screen.getByText(
          "A 10:00-11:00 meeting keeps recording until 11:03 - or stops sooner, once 30 seconds pass with nobody speaking.",
        ),
      ).toBeTruthy();

      fireEvent.change(afterMinutes(), { target: { value: "90" } });
      fireEvent.change(silenceSeconds(), { target: { value: "45" } });
      expect(
        screen.getByText(
          "A 10:00-11:00 meeting keeps recording until 12:30 - or stops sooner, once 45 seconds pass with nobody speaking.",
        ),
      ).toBeTruthy();
    });

    it("announces the example when a value changes", async () => {
      renderSection();
      await screen.findByText("Let a calendar meeting end its own recording");
      fireEvent.click(autoStop());
      expect(screen.getByText(/keeps recording until/).getAttribute("aria-live")).toBe("polite");
    });

    it("saves the three settings alongside the placement", async () => {
      renderSection();
      await screen.findByText("Let a calendar meeting end its own recording");

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
      await screen.findByText("Let a calendar meeting end its own recording");

      expect(autoStop().getAttribute("aria-checked")).toBe("true");
      expect(afterMinutes().value).toBe("7");
      expect(silenceSeconds().value).toBe("45");
    });

    it("sends the defaults rather than a blanked or zero duration", async () => {
      // Clearing a number input yields "" - saving that as 0 would stop a recording the instant it began.
      renderSection();
      await screen.findByText("Let a calendar meeting end its own recording");

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
