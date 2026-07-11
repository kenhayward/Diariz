import { render, screen, fireEvent, waitFor } from "@testing-library/react";
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
    // The folder chooser only appears in "specific folder" mode.
    expect(screen.queryByLabelText(/^folder$/i)).toBeNull();
    fireEvent.click(screen.getByRole("radio", { name: /specific folder/i }));
    expect(screen.getByLabelText(/^folder$/i)).toBeTruthy();
  });

  it("saves a specific-folder placement with the chosen folder, and nothing else", async () => {
    (api.listSections as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "sec-1", name: "Projects", parentId: null, position: 0 },
    ]);
    renderSection();
    fireEvent.click(await screen.findByRole("radio", { name: /specific folder/i }));
    fireEvent.change(await screen.findByLabelText(/^folder$/i), { target: { value: "sec-1" } });
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await waitFor(() => expect(api.updateUserSettings).toHaveBeenCalled());
    const arg = (api.updateUserSettings as ReturnType<typeof vi.fn>).mock.calls[0][0];
    expect(arg).toEqual({ placementMode: "SpecificFolder", placementSectionId: "sec-1" });
    expect(arg).not.toHaveProperty("apiBase");
    expect(arg).not.toHaveProperty("toolsEnabled");
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
});
