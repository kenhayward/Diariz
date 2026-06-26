import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("../lib/api", () => ({
  api: { getUserSettings: vi.fn(), updateUserSettings: vi.fn() },
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
    (api.getUserSettings as ReturnType<typeof vi.fn>).mockResolvedValue({
      apiBase: "https://existing/v1",
      model: "gpt-x",
      hasApiKey: true,
      defaultApiBase: "https://server/v1",
      defaultModel: "server-model",
      serverHasApiKey: true,
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
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await waitFor(() =>
      expect(api.updateUserSettings).toHaveBeenCalledWith({
        apiBase: "https://new/v1",
        model: "gpt-x",
        apiKey: null,
      }),
    );
  });

  it("sends a newly typed key", async () => {
    renderModal();
    await screen.findByDisplayValue("https://existing/v1");
    fireEvent.change(screen.getByPlaceholderText(/leave blank to keep/i), {
      target: { value: "sk-new" },
    });
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

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
    });
    renderModal();

    // The server default appears as a placeholder, not a value.
    const endpoint = await screen.findByPlaceholderText(/Default: https:\/\/server\/v1/);
    expect((endpoint as HTMLInputElement).value).toBe("");

    // Submitting without changing anything must not persist the defaults as the user's own.
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));
    await waitFor(() =>
      expect(api.updateUserSettings).toHaveBeenCalledWith({ apiBase: null, model: null, apiKey: null }),
    );
  });

  it("clears the stored key", async () => {
    renderModal();
    await screen.findByDisplayValue("https://existing/v1");
    fireEvent.click(screen.getByRole("button", { name: /clear stored key/i }));
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await waitFor(() =>
      expect(api.updateUserSettings).toHaveBeenCalledWith(
        expect.objectContaining({ apiKey: "" }),
      ),
    );
  });
});
