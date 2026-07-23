import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter } from "react-router-dom";
import { describe, it, expect, vi, beforeEach } from "vitest";

// Settings is now a Platform-Administrator-only modal (personal settings moved to Preferences), so every test
// runs as a platform admin.
const authState = { isPlatformAdmin: true, logout: vi.fn() };
vi.mock("../auth", () => ({ useAuth: () => authState }));
vi.mock("../lib/api", () => ({
  api: {
    getPlatformSettings: vi.fn(),
    updatePlatformSettings: vi.fn().mockResolvedValue(undefined),
    runAudioRetention: vi.fn().mockResolvedValue({ deleted: 0 }),
    runTagBackfill: vi.fn().mockResolvedValue({ enqueued: 3 }),
    backupUrl: () => "/api/maintenance/backup?access_token=t",
    restoreBackup: vi.fn().mockResolvedValue(undefined),
    listAllWorkflowSignals: vi.fn().mockResolvedValue([]),
    createWorkflowSignal: vi.fn(),
    updateWorkflowSignal: vi.fn(),
    deleteWorkflowSignal: vi.fn(),
    listPlatformWebhooks: vi.fn().mockResolvedValue([]),
    createPlatformWebhook: vi.fn(),
    deletePlatformWebhook: vi.fn(),
  },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../lib/api";
import SettingsModal from "./SettingsModal";

const platformDefaults = {
  starterQuotaBytes: 5 * 1024 ** 3,
  maxQuotaBytes: 50 * 1024 ** 3,
  minutesGenerationMode: "SingleCall",
  autoDeleteAudioEnabled: false,
  audioRetentionDays: 30,
  audioDeletionTimeOfDay: "03:00:00",
  apiAccessEnabled: false,
  mcpAccessEnabled: true,
  webhooksEnabled: false,
  llmTimeoutSeconds: 120,
};

function renderModal(onClose: () => void = () => {}) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter>
        <SettingsModal onClose={onClose} />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe("SettingsModal", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    authState.isPlatformAdmin = true;
    (api.getPlatformSettings as ReturnType<typeof vi.fn>).mockResolvedValue({ ...platformDefaults });
    (api.updatePlatformSettings as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    (api.runAudioRetention as ReturnType<typeof vi.fn>).mockResolvedValue({ deleted: 0 });
    (api.listAllWorkflowSignals as ReturnType<typeof vi.fn>).mockResolvedValue([]);
    (api.createWorkflowSignal as ReturnType<typeof vi.fn>).mockResolvedValue({
      id: "s-new", key: "new_key", label: "New label", description: null, isActive: true,
    });
    (api.updateWorkflowSignal as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    (api.deleteWorkflowSignal as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
    (api.listPlatformWebhooks as ReturnType<typeof vi.fn>).mockResolvedValue([]);
    (api.createPlatformWebhook as ReturnType<typeof vi.fn>).mockResolvedValue({
      id: "pw-new", name: "New automation", url: "https://example.com/hook", eventTypes: ["recording.created"], secret: "dz_whsec_platformsecret",
    });
    (api.deletePlatformWebhook as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
  });

  it("only carries platform-admin tabs (no personal Model Settings / Chat Tools / Recordings)", async () => {
    renderModal();
    for (const name of [/^ai$/i, /storage quotas/i, /maintenance/i, /integration/i])
      expect(await screen.findByRole("tab", { name })).toBeTruthy();
    expect(screen.queryByRole("tab", { name: /model settings/i })).toBeNull();
    expect(screen.queryByRole("tab", { name: /chat tools/i })).toBeNull();
    expect(screen.queryByRole("tab", { name: /^recordings$/i })).toBeNull();
  });

  it("does not close when the backdrop is clicked (OK/Cancel only)", async () => {
    const onClose = vi.fn();
    const { container } = renderModal(onClose);
    await screen.findByRole("tab", { name: /storage quotas/i });
    fireEvent.click(container.firstChild as Element); // the backdrop overlay
    expect(onClose).not.toHaveBeenCalled();
  });

  it("holds the modal at a fixed height so it doesn't resize between tabs", async () => {
    renderModal();
    await screen.findByRole("tab", { name: /storage quotas/i });
    const dialog = screen.getByRole("dialog", { name: /settings/i });
    expect(dialog.className).toContain("h-[85vh]");
    expect(dialog.className).not.toContain("max-h-[85vh]");
  });

  it("shows the AI tab first, with the minutes mode and the global LLM timeout loaded from the API", async () => {
    (api.getPlatformSettings as ReturnType<typeof vi.fn>).mockResolvedValue({
      ...platformDefaults, minutesGenerationMode: "PerSection", llmTimeoutSeconds: 90,
    });
    renderModal();

    const mode = (await screen.findByLabelText(/minutes generation/i)) as HTMLSelectElement;
    await waitFor(() => expect(mode.value).toBe("PerSection"));
    const timeout = screen.getByRole("spinbutton", { name: /llm timeout/i });
    await waitFor(() => expect((timeout as HTMLInputElement).value).toBe("90"));
  });

  it("saves the LLM timeout on OK", async () => {
    (api.getPlatformSettings as ReturnType<typeof vi.fn>).mockResolvedValue({ ...platformDefaults, llmTimeoutSeconds: 90 });
    renderModal();

    const timeout = await screen.findByRole("spinbutton", { name: /llm timeout/i });
    await waitFor(() => expect((timeout as HTMLInputElement).value).toBe("90"));
    fireEvent.change(timeout, { target: { value: "300" } });
    fireEvent.click(screen.getByRole("button", { name: /^ok$/i }));

    await waitFor(() =>
      expect(api.updatePlatformSettings).toHaveBeenCalledWith(expect.objectContaining({ llmTimeoutSeconds: 300 })),
    );
  });

  it("rejects a sub-5-second LLM timeout", async () => {
    (api.getPlatformSettings as ReturnType<typeof vi.fn>).mockResolvedValue({ ...platformDefaults, llmTimeoutSeconds: 90 });
    renderModal();

    const timeout = await screen.findByRole("spinbutton", { name: /llm timeout/i });
    await waitFor(() => expect((timeout as HTMLInputElement).value).toBe("90"));
    fireEvent.change(timeout, { target: { value: "3" } });
    fireEvent.click(screen.getByRole("button", { name: /^ok$/i }));

    expect(await screen.findByText(/at least 5/i)).toBeTruthy();
    expect(api.updatePlatformSettings).not.toHaveBeenCalled();
  });

  it("edits the quota defaults (GB → bytes) and saves on OK without touching user settings", async () => {
    renderModal();

    fireEvent.click(await screen.findByRole("tab", { name: /storage quotas/i }));
    const starter = await screen.findByLabelText(/starter quota \(GB\)/i);
    await waitFor(() => expect((starter as HTMLInputElement).value).toBe("5"));
    fireEvent.change(starter, { target: { value: "2" } });
    fireEvent.change(screen.getByLabelText(/maximum quota \(GB\)/i), { target: { value: "20" } });
    fireEvent.click(screen.getByRole("button", { name: /^ok$/i }));

    await waitFor(() =>
      expect(api.updatePlatformSettings).toHaveBeenCalledWith(
        expect.objectContaining({ starterQuotaBytes: 2 * 1024 ** 3, maxQuotaBytes: 20 * 1024 ** 3 }),
      ),
    );
  });

  it("enables user API access from the Integration tab and saves it", async () => {
    renderModal();

    fireEvent.click(await screen.findByRole("tab", { name: /integration/i }));
    const link = screen.getByRole("link", { name: /view api reference/i });
    expect(link.getAttribute("href")).toBe("/developers/api");
    expect(link.getAttribute("target")).toBe("_blank");
    const toggle = await screen.findByLabelText(/enable user api access/i);
    await waitFor(() => expect((toggle as HTMLInputElement).checked).toBe(false));
    fireEvent.click(toggle);
    fireEvent.click(screen.getByRole("button", { name: /^ok$/i }));

    await waitFor(() =>
      expect(api.updatePlatformSettings).toHaveBeenCalledWith(expect.objectContaining({ apiAccessEnabled: true })),
    );
  });

  it("saves the MCP and Webhooks toggles", async () => {
    const update = vi.mocked(api.updatePlatformSettings).mockResolvedValue(undefined);
    renderModal();

    fireEvent.click(await screen.findByRole("tab", { name: /integration/i }));
    await screen.findByLabelText(/enable user api access/i);
    expect((screen.getByLabelText(/mcp/i) as HTMLInputElement).checked).toBe(true);
    expect((screen.getByLabelText(/webhooks/i) as HTMLInputElement).checked).toBe(false);
    fireEvent.click(screen.getByLabelText(/webhooks/i));
    fireEvent.click(screen.getByRole("button", { name: /^ok$/i }));

    await waitFor(() =>
      expect(update).toHaveBeenCalledWith(expect.objectContaining({ webhooksEnabled: true })),
    );
  });

  it("lists workflow signals when webhooks are enabled", async () => {
    (api.getPlatformSettings as ReturnType<typeof vi.fn>).mockResolvedValue({ ...platformDefaults, webhooksEnabled: true });
    (api.listAllWorkflowSignals as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "s1", key: "action_item_created", label: "Action item created", description: null, isActive: true },
    ]);
    renderModal();

    fireEvent.click(await screen.findByRole("tab", { name: /integration/i }));

    expect(await screen.findByText("action_item_created")).toBeTruthy();
    expect(screen.getByText("Action item created")).toBeTruthy();
  });

  it("does not show the workflow signals section when webhooks are disabled", async () => {
    renderModal();
    fireEvent.click(await screen.findByRole("tab", { name: /integration/i }));
    await screen.findByLabelText(/enable user api access/i);
    expect(screen.queryByText(/workflow signals/i)).toBeNull();
  });

  it("adds a workflow signal from the Integration tab", async () => {
    (api.getPlatformSettings as ReturnType<typeof vi.fn>).mockResolvedValue({ ...platformDefaults, webhooksEnabled: true });
    renderModal();

    fireEvent.click(await screen.findByRole("tab", { name: /integration/i }));
    await screen.findByText(/workflow signals/i);

    fireEvent.change(screen.getByLabelText(/^signal key$/i), { target: { value: "new_key" } });
    fireEvent.change(screen.getByLabelText(/^signal label$/i), { target: { value: "New label" } });
    fireEvent.click(screen.getByRole("button", { name: /add signal/i }));

    await waitFor(() =>
      expect(api.createWorkflowSignal).toHaveBeenCalledWith(
        expect.objectContaining({ key: "new_key", label: "New label" }),
      ),
    );
  });

  it("surfaces an error when adding a workflow signal fails", async () => {
    (api.getPlatformSettings as ReturnType<typeof vi.fn>).mockResolvedValue({ ...platformDefaults, webhooksEnabled: true });
    (api.createWorkflowSignal as ReturnType<typeof vi.fn>).mockRejectedValue(new Error("Key already exists."));
    renderModal();

    fireEvent.click(await screen.findByRole("tab", { name: /integration/i }));
    await screen.findByText(/workflow signals/i);

    fireEvent.change(screen.getByLabelText(/^signal key$/i), { target: { value: "dup_key" } });
    fireEvent.change(screen.getByLabelText(/^signal label$/i), { target: { value: "Dup" } });
    fireEvent.click(screen.getByRole("button", { name: /add signal/i }));

    expect(await screen.findByText(/key already exists/i)).toBeTruthy();
  });

  it("toggles a workflow signal's active state", async () => {
    (api.getPlatformSettings as ReturnType<typeof vi.fn>).mockResolvedValue({ ...platformDefaults, webhooksEnabled: true });
    (api.listAllWorkflowSignals as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "s1", key: "action_item_created", label: "Action item created", description: null, isActive: true },
    ]);
    renderModal();

    fireEvent.click(await screen.findByRole("tab", { name: /integration/i }));
    const toggle = await screen.findByLabelText(/action item created: active/i);
    expect((toggle as HTMLInputElement).checked).toBe(true);
    fireEvent.click(toggle);

    await waitFor(() =>
      expect(api.updateWorkflowSignal).toHaveBeenCalledWith(
        "s1",
        expect.objectContaining({ label: "Action item created", isActive: false }),
      ),
    );
  });

  it("deletes a workflow signal", async () => {
    (api.getPlatformSettings as ReturnType<typeof vi.fn>).mockResolvedValue({ ...platformDefaults, webhooksEnabled: true });
    (api.listAllWorkflowSignals as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "s1", key: "action_item_created", label: "Action item created", description: null, isActive: true },
    ]);
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
    renderModal();

    fireEvent.click(await screen.findByRole("tab", { name: /integration/i }));
    await screen.findByText("action_item_created");
    fireEvent.click(screen.getByRole("button", { name: /delete signal/i }));

    await waitFor(() => expect(api.deleteWorkflowSignal).toHaveBeenCalledWith("s1"));
    confirm.mockRestore();
  });

  it("lists platform automations when webhooks are enabled", async () => {
    (api.getPlatformSettings as ReturnType<typeof vi.fn>).mockResolvedValue({ ...platformDefaults, webhooksEnabled: true });
    (api.listPlatformWebhooks as ReturnType<typeof vi.fn>).mockResolvedValue([
      {
        id: "pw1", name: "Ops relay", url: "https://example.com/ops", eventTypes: ["recording.created"],
        isActive: true, consecutiveFailures: 0, disabledReason: null, lastDeliveryAt: null, lastStatus: null,
        createdAt: "2026-01-01T00:00:00Z", signalFilter: ["action_item_created"], scope: "Platform",
      },
    ]);
    renderModal();

    fireEvent.click(await screen.findByRole("tab", { name: /integration/i }));

    expect(await screen.findByText("Ops relay")).toBeTruthy();
    expect(screen.getByText("https://example.com/ops")).toBeTruthy();
  });

  it("does not show the platform automations section when webhooks are disabled", async () => {
    renderModal();
    fireEvent.click(await screen.findByRole("tab", { name: /integration/i }));
    await screen.findByLabelText(/enable user api access/i);
    expect(screen.queryByText(/platform automations/i)).toBeNull();
  });

  it("creates a platform automation with the chosen events and signals, and shows the secret once", async () => {
    (api.getPlatformSettings as ReturnType<typeof vi.fn>).mockResolvedValue({ ...platformDefaults, webhooksEnabled: true });
    (api.listAllWorkflowSignals as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "s1", key: "action_item_created", label: "Action item created", description: null, isActive: true },
    ]);
    renderModal();

    fireEvent.click(await screen.findByRole("tab", { name: /integration/i }));
    await screen.findByText(/platform automations/i);

    fireEvent.change(screen.getByLabelText(/destination url/i), { target: { value: "https://example.com/hook" } });
    fireEvent.click(screen.getByLabelText(/a recording is created/i));
    fireEvent.click(screen.getByLabelText(/^action item created$/i));
    fireEvent.click(screen.getByRole("button", { name: /create platform automation/i }));

    await waitFor(() =>
      expect(api.createPlatformWebhook).toHaveBeenCalledWith(
        expect.objectContaining({
          url: "https://example.com/hook",
          eventTypes: ["recording.created"],
          signalFilter: ["action_item_created"],
        }),
      ),
    );
    expect(await screen.findByText("dz_whsec_platformsecret")).toBeTruthy();
  });

  it("blocks creating a platform automation when no signal is chosen", async () => {
    (api.getPlatformSettings as ReturnType<typeof vi.fn>).mockResolvedValue({ ...platformDefaults, webhooksEnabled: true });
    (api.listAllWorkflowSignals as ReturnType<typeof vi.fn>).mockResolvedValue([
      { id: "s1", key: "action_item_created", label: "Action item created", description: null, isActive: true },
    ]);
    renderModal();

    fireEvent.click(await screen.findByRole("tab", { name: /integration/i }));
    await screen.findByText(/platform automations/i);

    fireEvent.change(screen.getByLabelText(/destination url/i), { target: { value: "https://example.com/hook" } });
    fireEvent.click(screen.getByLabelText(/a recording is created/i));
    fireEvent.click(screen.getByRole("button", { name: /create platform automation/i }));

    expect(await screen.findByText(/choose at least one signal/i)).toBeTruthy();
    expect(api.createPlatformWebhook).not.toHaveBeenCalled();
  });

  it("deletes a platform automation", async () => {
    (api.getPlatformSettings as ReturnType<typeof vi.fn>).mockResolvedValue({ ...platformDefaults, webhooksEnabled: true });
    (api.listPlatformWebhooks as ReturnType<typeof vi.fn>).mockResolvedValue([
      {
        id: "pw1", name: "Ops relay", url: "https://example.com/ops", eventTypes: ["recording.created"],
        isActive: true, consecutiveFailures: 0, disabledReason: null, lastDeliveryAt: null, lastStatus: null,
        createdAt: "2026-01-01T00:00:00Z", signalFilter: ["action_item_created"], scope: "Platform",
      },
    ]);
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
    renderModal();

    fireEvent.click(await screen.findByRole("tab", { name: /integration/i }));
    await screen.findByText("Ops relay");
    fireEvent.click(screen.getByRole("button", { name: /^delete$/i }));

    await waitFor(() => expect(api.deletePlatformWebhook).toHaveBeenCalledWith("pw1"));
    confirm.mockRestore();
  });

  it("enables audio auto-delete and saves the retention policy", async () => {
    renderModal();

    fireEvent.click(await screen.findByRole("tab", { name: /storage quotas/i }));
    const days = await screen.findByLabelText(/delete audio after/i);
    await waitFor(() => expect((days as HTMLInputElement).value).toBe("30"));
    expect((screen.getByLabelText(/deletion time/i) as HTMLInputElement).value).toBe("03:00");

    fireEvent.click(screen.getByLabelText(/automatically delete audio/i));
    fireEvent.change(days, { target: { value: "7" } });
    fireEvent.change(screen.getByLabelText(/deletion time/i), { target: { value: "02:15" } });
    fireEvent.click(screen.getByRole("button", { name: /^ok$/i }));

    await waitFor(() =>
      expect(api.updatePlatformSettings).toHaveBeenCalledWith(
        expect.objectContaining({
          autoDeleteAudioEnabled: true,
          audioRetentionDays: 7,
          audioDeletionTimeOfDay: "02:15:00",
        }),
      ),
    );
  });

  it("runs the audio-deletion pass now and shows the result", async () => {
    (api.runAudioRetention as ReturnType<typeof vi.fn>).mockResolvedValue({ deleted: 3 });
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
    renderModal();

    fireEvent.click(await screen.findByRole("tab", { name: /storage quotas/i }));
    fireEvent.click(await screen.findByRole("button", { name: /run now/i }));

    await waitFor(() => expect(api.runAudioRetention).toHaveBeenCalled());
    expect(await screen.findByText(/deleted audio for 3/i)).toBeTruthy();
    confirm.mockRestore();
  });

  it("does not run the deletion pass when the confirmation is cancelled", async () => {
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(false);
    renderModal();

    fireEvent.click(await screen.findByRole("tab", { name: /storage quotas/i }));
    fireEvent.click(await screen.findByRole("button", { name: /run now/i }));

    expect(api.runAudioRetention).not.toHaveBeenCalled();
    confirm.mockRestore();
  });

  it("shows backup + gated restore on the Maintenance tab", async () => {
    renderModal();
    fireEvent.click(await screen.findByRole("tab", { name: /maintenance/i }));

    const link = screen.getByRole("link", { name: /download backup/i });
    expect(link.getAttribute("href")).toContain("/api/maintenance/backup?access_token=");

    const restore = screen.getByRole("button", { name: /^restore$/i });
    expect((restore as HTMLButtonElement).disabled).toBe(true);

    const file = new File(["zip"], "backup.zip", { type: "application/zip" });
    fireEvent.change(screen.getByLabelText(/choose a backup file/i), { target: { files: [file] } });
    expect((restore as HTMLButtonElement).disabled).toBe(true); // file alone isn't enough

    fireEvent.click(screen.getByRole("checkbox", { name: /permanently replace all current data/i }));
    expect((restore as HTMLButtonElement).disabled).toBe(false);
  });

  it("Maintenance tab offers a confirmed tag backfill and reports the queued count", async () => {
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
    renderModal();
    fireEvent.click(await screen.findByRole("tab", { name: /maintenance/i }));

    fireEvent.click(screen.getByRole("button", { name: /backfill tags/i }));
    expect(confirm).toHaveBeenCalled();
    await waitFor(() => expect(api.runTagBackfill).toHaveBeenCalled());
    expect(await screen.findByText(/3/)).toBeTruthy();
    confirm.mockRestore();
  });
});
