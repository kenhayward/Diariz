import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";

const authState = { isPlatformAdmin: false };
vi.mock("../auth", () => ({ useAuth: () => authState }));
vi.mock("../lib/api", () => ({
  api: {
    getUserSettings: vi.fn(),
    updateUserSettings: vi.fn(),
    getPlatformSettings: vi.fn().mockResolvedValue({ starterQuotaBytes: 5 * 1024 ** 3, maxQuotaBytes: 50 * 1024 ** 3 }),
    updatePlatformSettings: vi.fn().mockResolvedValue({ starterQuotaBytes: 0, maxQuotaBytes: 0 }),
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
    });
    (api.updateUserSettings as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
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
      }),
    );
  });

  it("shows the server context-window default as a placeholder and saves an override", async () => {
    renderModal();
    const field = await screen.findByPlaceholderText(/Default: 131,072/);
    expect((field as HTMLInputElement).value).toBe(""); // no per-user override set

    fireEvent.change(field, { target: { value: "8000" } });
    fireEvent.click(screen.getByRole("button", { name: /^ok$/i }));

    await waitFor(() =>
      expect(api.updateUserSettings).toHaveBeenCalledWith(expect.objectContaining({ contextWindow: 8000 })),
    );
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
