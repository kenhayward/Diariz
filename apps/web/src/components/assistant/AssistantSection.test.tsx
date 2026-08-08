import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("../../lib/api", () => ({
  api: { getUserSettings: vi.fn(), updateUserSettings: vi.fn() },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../../lib/api";
import AssistantSection from "./AssistantSection";

const mock = (f: unknown) => f as ReturnType<typeof vi.fn>;

const settings = (over: Record<string, unknown> = {}) => ({
  apiBase: null, model: null, hasApiKey: false, defaultApiBase: null, defaultModel: null,
  serverHasApiKey: false, contextWindow: null, defaultContextWindow: 131072,
  toolsEnabled: false, defaultToolsEnabled: false,
  tools: [
    { name: "who_said_that", title: "Who said that", description: "Find who said a given phrase. Fuzzy-matches across transcripts.", enabled: true, defaultEnabled: true },
    { name: "list_recordings", title: "List recordings", description: "List recordings.", enabled: true, defaultEnabled: true },
  ],
  reasoningEnabled: false, reasoningEffort: "medium", defaultReasoningEnabled: false, defaultReasoningEffort: "medium",
  placementMode: "SelectedFolder", placementSectionId: null,
  ...over,
});

function renderSection() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <AssistantSection />
    </QueryClientProvider>,
  );
}

describe("AssistantSection", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mock(api.getUserSettings).mockResolvedValue(settings());
    mock(api.updateUserSettings).mockResolvedValue(undefined);
  });

  // ---- the model card ----

  /// The point of collapsing the model override to one line: the usual question is "what am I actually
  /// running?", and the answer is now on the card rather than behind a tab.
  it("states the model in effect without opening anything", async () => {
    mock(api.getUserSettings).mockResolvedValue(
      settings({ defaultModel: "openai/gpt-oss-20b", defaultApiBase: "http://192.168.1.129:1234/v1", reasoningEnabled: true, reasoningEffort: "low" }),
    );
    renderSection();

    expect(await screen.findByText(/openai\/gpt-oss-20b at 192\.168\.1\.129:1234/)).toBeTruthy();
    expect(screen.getByText(/reasoning on, low/)).toBeTruthy();
  });

  it("says whose model it is: the platform's, or an override", async () => {
    renderSection();
    expect(await screen.findByText("Platform default")).toBeTruthy();

    mock(api.getUserSettings).mockResolvedValue(settings({ model: "mine", defaultModel: "theirs" }));
    renderSection();
    expect(await screen.findAllByText("Overridden")).toBeTruthy();
  });

  it("opens the model dialog from Change", async () => {
    renderSection();
    fireEvent.click(await screen.findByRole("button", { name: /change/i }));
    expect(screen.getByRole("dialog", { name: /model settings/i })).toBeTruthy();
  });

  // ---- the tools card ----

  /// Ported from the Chat Tools tab. The write must still carry only the tool fields, or it would clobber
  /// the model override sitting on the very same tab now.
  it("saves the master switch and per-tool overrides, and nothing else", async () => {
    renderSection();
    await screen.findByText("Who said that");
    const master = screen.getByRole("checkbox", { name: /^enabled$/i });

    // Tool checkboxes are disabled until the master switch is on.
    expect((screen.getByRole("checkbox", { name: /who said that/i }) as HTMLInputElement).disabled).toBe(true);
    fireEvent.click(master);
    await waitFor(() => expect(api.updateUserSettings).toHaveBeenCalled());
    expect((screen.getByRole("checkbox", { name: /who said that/i }) as HTMLInputElement).disabled).toBe(false);

    fireEvent.click(screen.getByRole("checkbox", { name: /list recordings/i })); // turn one off

    await waitFor(() => expect(api.updateUserSettings).toHaveBeenCalledTimes(2));
    const arg = mock(api.updateUserSettings).mock.calls[1][0];
    expect(arg).toEqual({ toolsEnabled: true, toolOverrides: { who_said_that: true, list_recordings: false } });
    expect(arg).not.toHaveProperty("apiBase");
    expect(arg).not.toHaveProperty("placementMode");
  });

  // Two Save buttons on one tab was part of what made these read as two separate pages.
  it("saves a tool change on the click, with no Save button", async () => {
    renderSection();
    await screen.findByText("Who said that");
    expect(screen.queryByRole("button", { name: /^save$/i })).toBeNull();
  });

  /// The server's description is the instruction the model reads when choosing a tool - a paragraph, and
  /// not written for a person. The row shows a phrase and keeps the full text in the tooltip.
  it("describes each tool in a phrase, keeping the model-facing text in the tooltip", async () => {
    renderSection();
    await screen.findByText("Who said that");

    expect(screen.getByText("who said a phrase, and when")).toBeTruthy();
    expect(screen.queryByText(/Fuzzy-matches across transcripts/)).toBeNull();
    const row = screen.getByText("Who said that").closest("li");
    expect(row?.getAttribute("title")).toMatch(/Fuzzy-matches across transcripts/);
  });

  /// A tool added server-side with no phrase yet must still read as something.
  it("falls back to the server description for a tool it has no phrase for", async () => {
    mock(api.getUserSettings).mockResolvedValue(
      settings({ tools: [{ name: "brand_new_tool", title: "Brand new", description: "Does a new thing.", enabled: true, defaultEnabled: true }] }),
    );
    renderSection();
    expect(await screen.findByText("Does a new thing.")).toBeTruthy();
  });
});
