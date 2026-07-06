import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";

const authState = { isPlatformAdmin: false, logout: vi.fn() };
vi.mock("../auth", () => ({ useAuth: () => authState }));
vi.mock("../lib/api", () => ({
  api: {
    getUserSettings: vi.fn(),
    updateUserSettings: vi.fn(),
    getPlatformSettings: vi.fn().mockResolvedValue({ starterQuotaBytes: 5 * 1024 ** 3, maxQuotaBytes: 50 * 1024 ** 3 }),
    updatePlatformSettings: vi.fn().mockResolvedValue({ starterQuotaBytes: 0, maxQuotaBytes: 0 }),
    backupUrl: () => "/api/maintenance/backup?access_token=t",
    restoreBackup: vi.fn().mockResolvedValue(undefined),
  },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../lib/api";
import SettingsModal from "./SettingsModal";

function renderModal() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <SettingsModal onClose={() => {}} />
    </QueryClientProvider>,
  );
}

describe("SettingsModal", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    authState.isPlatformAdmin = false;
    (api.getUserSettings as ReturnType<typeof vi.fn>).mockResolvedValue({
      apiBase: "https://existing/v1",
      model: "gpt-x",
      hasApiKey: true,
      defaultApiBase: "https://server/v1",
      defaultModel: "server-model",
      serverHasApiKey: true,
      contextWindow: null,
      defaultContextWindow: 131072,
      toolsEnabled: false,
      defaultToolsEnabled: false,
      tools: [],
      reasoningEnabled: false,
      reasoningEffort: "medium",
      defaultReasoningEnabled: false,
      defaultReasoningEffort: "medium",
    });
    (api.updateUserSettings as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    (api.getPlatformSettings as ReturnType<typeof vi.fn>).mockResolvedValue({
      starterQuotaBytes: 5 * 1024 ** 3,
      maxQuotaBytes: 50 * 1024 ** 3,
    });
  });

  it("loads existing endpoint/model and indicates a key is set", async () => {
    renderModal();
    expect(await screen.findByDisplayValue("https://existing/v1")).toBeTruthy();
    expect(screen.getByDisplayValue("gpt-x")).toBeTruthy();
    expect(screen.getByText(/· set/)).toBeTruthy();
  });

  it("saves edited endpoint with apiKey left unchanged (null)", async () => {
    renderModal();
    const endpoint = await screen.findByDisplayValue("https://existing/v1");
    fireEvent.change(endpoint, { target: { value: "https://new/v1" } });
    fireEvent.click(screen.getByRole("button", { name: /^ok$/i }));

    await waitFor(() =>
      expect(api.updateUserSettings).toHaveBeenCalledWith({
        apiBase: "https://new/v1",
        model: "gpt-x",
        apiKey: null,
        contextWindow: null,
        toolsEnabled: false,
        toolOverrides: {},
        reasoningEnabled: false,
        reasoningEffort: "medium",
      }),
    );
  });

  it("sends a newly typed key", async () => {
    renderModal();
    await screen.findByDisplayValue("https://existing/v1");
    fireEvent.change(screen.getByPlaceholderText(/leave blank to keep/i), {
      target: { value: "sk-new" },
    });
    fireEvent.click(screen.getByRole("button", { name: /^ok$/i }));

    await waitFor(() =>
      expect(api.updateUserSettings).toHaveBeenCalledWith(
        expect.objectContaining({ apiKey: "sk-new" }),
      ),
    );
  });

  it("shows server defaults as placeholders and does not persist them when left blank", async () => {
    (api.getUserSettings as ReturnType<typeof vi.fn>).mockResolvedValue({
      apiBase: null,
      model: null,
      hasApiKey: false,
      defaultApiBase: "https://server/v1",
      defaultModel: "server-model",
      serverHasApiKey: true,
      contextWindow: null,
      defaultContextWindow: 131072,
      toolsEnabled: false,
      defaultToolsEnabled: false,
      tools: [],
      reasoningEnabled: false,
      reasoningEffort: "medium",
      defaultReasoningEnabled: false,
      defaultReasoningEffort: "medium",
    });
    renderModal();

    // The server default appears as a placeholder, not a value.
    const endpoint = await screen.findByPlaceholderText(/Default: https:\/\/server\/v1/);
    expect((endpoint as HTMLInputElement).value).toBe("");

    // Submitting without changing anything must not persist the defaults as the user's own.
    fireEvent.click(screen.getByRole("button", { name: /^ok$/i }));
    await waitFor(() =>
      expect(api.updateUserSettings).toHaveBeenCalledWith({
        apiBase: null,
        model: null,
        apiKey: null,
        contextWindow: null,
        toolsEnabled: false,
        toolOverrides: {},
        reasoningEnabled: false,
        reasoningEffort: "medium",
      }),
    );
  });

  it("no longer offers a chat context-window field and always sends null (server default)", async () => {
    renderModal();
    await screen.findByDisplayValue("https://existing/v1");
    expect(screen.queryByText(/context window/i)).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: /^ok$/i }));
    await waitFor(() =>
      expect(api.updateUserSettings).toHaveBeenCalledWith(expect.objectContaining({ contextWindow: null })),
    );
  });

  it("does not close when the backdrop is clicked (OK/Cancel only)", async () => {
    const onClose = vi.fn();
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const { container } = render(
      <QueryClientProvider client={qc}>
        <SettingsModal onClose={onClose} />
      </QueryClientProvider>,
    );
    await screen.findByDisplayValue("https://existing/v1");
    fireEvent.click(container.firstChild as Element); // the backdrop overlay
    expect(onClose).not.toHaveBeenCalled();
  });

  it("places the reasoning controls above the chat-tools list", async () => {
    renderModal();
    const reasoning = await screen.findByLabelText(/enable reasoning/i);
    const tools = screen.getByLabelText(/enable chat tools/i);
    // reasoning appears before the tools master switch in document order
    expect(reasoning.compareDocumentPosition(tools) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it("clears the stored key", async () => {
    renderModal();
    await screen.findByDisplayValue("https://existing/v1");
    fireEvent.click(screen.getByRole("button", { name: /clear stored key/i }));
    fireEvent.click(screen.getByRole("button", { name: /^ok$/i }));

    await waitFor(() =>
      expect(api.updateUserSettings).toHaveBeenCalledWith(
        expect.objectContaining({ apiKey: "" }),
      ),
    );
  });

  it("hides the Storage Quotas tab for non-platform admins", async () => {
    renderModal();
    await screen.findByDisplayValue("https://existing/v1");
    expect(screen.queryByRole("tab", { name: /storage quotas/i })).toBeNull();
  });

  it("hides the Maintenance tab for non-platform admins", async () => {
    renderModal();
    await screen.findByDisplayValue("https://existing/v1");
    expect(screen.queryByRole("tab", { name: /maintenance/i })).toBeNull();
  });

  it("shows backup + gated restore on the Maintenance tab for the Platform Administrator", async () => {
    authState.isPlatformAdmin = true;
    renderModal();
    fireEvent.click(await screen.findByRole("tab", { name: /maintenance/i }));

    // Backup is a self-authenticating download link.
    const link = screen.getByRole("link", { name: /download backup/i });
    expect(link.getAttribute("href")).toContain("/api/maintenance/backup?access_token=");

    // Restore is disabled until a file is chosen AND the confirmation is checked.
    const restore = screen.getByRole("button", { name: /^restore$/i });
    expect((restore as HTMLButtonElement).disabled).toBe(true);

    const file = new File(["zip"], "backup.zip", { type: "application/zip" });
    fireEvent.change(screen.getByLabelText(/choose a backup file/i), { target: { files: [file] } });
    expect((restore as HTMLButtonElement).disabled).toBe(true); // file alone isn't enough

    fireEvent.click(screen.getByRole("checkbox", { name: /permanently replace all current data/i }));
    expect((restore as HTMLButtonElement).disabled).toBe(false); // enabled once both confirmed
  });

  it("renders chat tools and saves the master switch + per-tool overrides", async () => {
    (api.getUserSettings as ReturnType<typeof vi.fn>).mockResolvedValue({
      apiBase: null,
      model: null,
      hasApiKey: false,
      defaultApiBase: null,
      defaultModel: null,
      serverHasApiKey: false,
      contextWindow: null,
      defaultContextWindow: 131072,
      toolsEnabled: false,
      defaultToolsEnabled: false,
      tools: [
        { name: "who_said_that", title: "Who said that", description: "Find who said a phrase.", enabled: true, defaultEnabled: true },
        { name: "list_recordings", title: "List recordings", description: "List recordings.", enabled: true, defaultEnabled: true },
      ],
    });

    renderModal();
    await screen.findByText("Who said that"); // tools table rendered once settings loaded
    const master = screen.getByLabelText(/enable chat tools/i);

    // Tool checkboxes are disabled until the master switch is on.
    expect((screen.getByRole("checkbox", { name: /who said that/i }) as HTMLInputElement).disabled).toBe(true);

    fireEvent.click(master); // turn tools on
    expect((screen.getByRole("checkbox", { name: /who said that/i }) as HTMLInputElement).disabled).toBe(false);
    // Turn one tool off (target by name so other checkboxes — e.g. reasoning — don't shift the index).
    fireEvent.click(screen.getByRole("checkbox", { name: /list recordings/i }));
    fireEvent.click(screen.getByRole("button", { name: /^ok$/i }));

    await waitFor(() =>
      expect(api.updateUserSettings).toHaveBeenCalledWith(
        expect.objectContaining({
          toolsEnabled: true,
          toolOverrides: { who_said_that: true, list_recordings: false },
        }),
      ),
    );
  });

  it("reveals the reasoning level when reasoning is enabled and saves both", async () => {
    renderModal();
    await screen.findByDisplayValue("https://existing/v1");

    // Level select is hidden until reasoning is turned on.
    expect(screen.queryByLabelText(/reasoning level/i)).toBeNull();

    fireEvent.click(screen.getByLabelText(/enable reasoning/i));
    const level = await screen.findByLabelText(/reasoning level/i);
    fireEvent.change(level, { target: { value: "high" } });
    fireEvent.click(screen.getByRole("button", { name: /^ok$/i }));

    await waitFor(() =>
      expect(api.updateUserSettings).toHaveBeenCalledWith(
        expect.objectContaining({ reasoningEnabled: true, reasoningEffort: "high" }),
      ),
    );
  });

  it("reflects the persisted minutes-generation mode (enum arrives as a string name)", async () => {
    authState.isPlatformAdmin = true;
    // The API serialises enums by name (JsonStringEnumConverter), so this arrives as "PerSection", not 1.
    (api.getPlatformSettings as ReturnType<typeof vi.fn>).mockResolvedValue({
      starterQuotaBytes: 5 * 1024 ** 3,
      maxQuotaBytes: 50 * 1024 ** 3,
      minutesGenerationMode: "PerSection",
    });
    renderModal();

    const select = (await screen.findByLabelText(/minutes generation/i)) as HTMLSelectElement;
    await waitFor(() => expect(select.value).toBe("PerSection"));
  });

  it("lets a Platform Administrator edit the quota defaults (GB → bytes) and saves on OK", async () => {
    authState.isPlatformAdmin = true;
    renderModal();

    // Switch to the Storage Quotas tab; starter pre-filled at 5 GB once the query resolves.
    fireEvent.click(await screen.findByRole("tab", { name: /storage quotas/i }));
    const starter = await screen.findByLabelText(/starter quota \(GB\)/i);
    await waitFor(() => expect((starter as HTMLInputElement).value).toBe("5"));
    fireEvent.change(starter, { target: { value: "2" } });
    fireEvent.change(screen.getByLabelText(/maximum quota \(GB\)/i), { target: { value: "20" } });

    fireEvent.click(screen.getByRole("button", { name: /^ok$/i }));

    await waitFor(() =>
      expect(api.updatePlatformSettings).toHaveBeenCalledWith({
        starterQuotaBytes: 2 * 1024 ** 3,
        maxQuotaBytes: 20 * 1024 ** 3,
      }),
    );
    // OK also saves the AI settings in the same action.
    expect(api.updateUserSettings).toHaveBeenCalled();
  });
});
