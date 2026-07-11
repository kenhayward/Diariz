import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("../lib/api", () => ({
  api: { getUserSettings: vi.fn(), updateUserSettings: vi.fn() },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../lib/api";
import ChatToolsSection from "./ChatToolsSection";

function renderSection() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <ChatToolsSection />
    </QueryClientProvider>,
  );
}

describe("ChatToolsSection", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    (api.getUserSettings as ReturnType<typeof vi.fn>).mockResolvedValue({
      apiBase: null, model: null, hasApiKey: false, defaultApiBase: null, defaultModel: null,
      serverHasApiKey: false, contextWindow: null, defaultContextWindow: 131072,
      toolsEnabled: false, defaultToolsEnabled: false,
      tools: [
        { name: "who_said_that", title: "Who said that", description: "Find who said a phrase.", enabled: true, defaultEnabled: true },
        { name: "list_recordings", title: "List recordings", description: "List recordings.", enabled: true, defaultEnabled: true },
      ],
      reasoningEnabled: false, reasoningEffort: "medium", defaultReasoningEnabled: false, defaultReasoningEffort: "medium",
      placementMode: "SelectedFolder", placementSectionId: null,
    });
    (api.updateUserSettings as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
  });

  it("saves the master switch + per-tool overrides, and nothing else", async () => {
    renderSection();
    await screen.findByText("Who said that"); // table rendered once settings loaded
    const master = screen.getByLabelText(/enable chat tools/i);

    // Tool checkboxes are disabled until the master switch is on.
    expect((screen.getByRole("checkbox", { name: /who said that/i }) as HTMLInputElement).disabled).toBe(true);
    fireEvent.click(master);
    expect((screen.getByRole("checkbox", { name: /who said that/i }) as HTMLInputElement).disabled).toBe(false);
    fireEvent.click(screen.getByRole("checkbox", { name: /list recordings/i })); // turn one off
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await waitFor(() => expect(api.updateUserSettings).toHaveBeenCalled());
    const arg = (api.updateUserSettings as ReturnType<typeof vi.fn>).mock.calls[0][0];
    expect(arg).toEqual({ toolsEnabled: true, toolOverrides: { who_said_that: true, list_recordings: false } });
    expect(arg).not.toHaveProperty("apiBase");
    expect(arg).not.toHaveProperty("placementMode");
  });
});
