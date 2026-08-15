import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("../../lib/api", () => ({
  api: { getUserSettings: vi.fn(), updateUserSettings: vi.fn() },
  apiErrorMessage: (e: unknown) => String(e),
}));

import { api } from "../../lib/api";
import ModelDialog from "./ModelDialog";
import type { UserSettings } from "../../lib/types";

const settings = {
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
  placementMode: "SelectedFolder",
  placementSectionId: null,
  llmTimeoutSeconds: null,
  defaultLlmTimeoutSeconds: 120,
};

const onClose = vi.fn();

function renderSection() {
  return renderWith(settings as unknown as UserSettings);
}

function renderWith(data: UserSettings) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <ModelDialog data={data} onClose={onClose} />
    </QueryClientProvider>,
  );
}

/// The model override used to be a whole Preferences tab; it is a dialog behind "Change..." now. The
/// tri-state key handling below is carried across unchanged - it is the part that quietly destroys a
/// stored key if it is ever collapsed into a plain controlled input.
describe("ModelDialog", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    (api.getUserSettings as ReturnType<typeof vi.fn>).mockResolvedValue(settings);
    (api.updateUserSettings as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);
  });

  it("loads the existing endpoint/model and indicates a key is set", async () => {
    renderSection();
    expect(screen.getByDisplayValue("https://existing/v1")).toBeTruthy();
    expect(screen.getByDisplayValue("gpt-x")).toBeTruthy();
    expect(screen.getByText(/· set/)).toBeTruthy();
  });

  it("saves only the model fields (no tool/placement fields) with the key left unchanged", async () => {
    renderSection();
    const endpoint = screen.getByDisplayValue("https://existing/v1");
    fireEvent.change(endpoint, { target: { value: "https://new/v1" } });
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await waitFor(() => expect(api.updateUserSettings).toHaveBeenCalled());
    const arg = (api.updateUserSettings as ReturnType<typeof vi.fn>).mock.calls[0][0];
    expect(arg).toEqual({
      apiBase: "https://new/v1",
      model: "gpt-x",
      apiKey: null,
      reasoningEnabled: false,
      reasoningEffort: "medium",
      llmTimeoutSeconds: 0,
    });
    // Independence: it must not touch the Chat Tools or Recordings preferences.
    expect(arg).not.toHaveProperty("toolsEnabled");
    expect(arg).not.toHaveProperty("placementMode");
  });

  it("sends an empty string to clear the endpoint override (not null)", async () => {
    renderSection();
    const endpoint = screen.getByDisplayValue("https://existing/v1");
    fireEvent.change(endpoint, { target: { value: "" } });
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await waitFor(() =>
      expect(api.updateUserSettings).toHaveBeenCalledWith(expect.objectContaining({ apiBase: "" })),
    );
  });

  it("sends a newly typed key, then clears the stored key", async () => {
    renderSection();
    fireEvent.change(screen.getByPlaceholderText(/leave blank to keep/i), { target: { value: "sk-new" } });
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));
    await waitFor(() =>
      expect(api.updateUserSettings).toHaveBeenCalledWith(expect.objectContaining({ apiKey: "sk-new" })),
    );

    fireEvent.click(screen.getByRole("button", { name: /clear stored key/i }));
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));
    await waitFor(() =>
      expect(api.updateUserSettings).toHaveBeenLastCalledWith(expect.objectContaining({ apiKey: "" })),
    );
  });

  it("reveals the reasoning level when reasoning is enabled and saves both", async () => {
    renderSection();
    expect(screen.queryByRole("radiogroup", { name: /reasoning level/i })).toBeNull();

    fireEvent.click(screen.getByLabelText(/enable reasoning/i));
    // Three options do not need a dropdown, so the level is three buttons - all three visible at rest.
    fireEvent.click(await screen.findByRole("radio", { name: /high/i }));
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await waitFor(() =>
      expect(api.updateUserSettings).toHaveBeenCalledWith(
        expect.objectContaining({ reasoningEnabled: true, reasoningEffort: "high" }),
      ),
    );
  });

  it("shows the inherited timeout as a placeholder when the user has no override", () => {
    renderWith({ ...settings, llmTimeoutSeconds: null } as unknown as UserSettings);

    const field = screen.getByRole("spinbutton", { name: /timeout/i });
    expect((field as HTMLInputElement).value).toBe("");
    expect(field.getAttribute("placeholder")).toBe("120");
  });

  it("sends the timeout on save", async () => {
    renderWith(settings as unknown as UserSettings);

    fireEvent.change(screen.getByRole("spinbutton", { name: /timeout/i }), { target: { value: "600" } });
    fireEvent.click(screen.getByRole("button", { name: /save/i }));

    await waitFor(() =>
      expect(vi.mocked(api.updateUserSettings).mock.calls[0][0].llmTimeoutSeconds).toBe(600),
    );
  });

  it("sends 0 to clear the override when the field is emptied", async () => {
    renderWith({ ...settings, llmTimeoutSeconds: 600 } as unknown as UserSettings);

    fireEvent.change(screen.getByRole("spinbutton", { name: /timeout/i }), { target: { value: "" } });
    fireEvent.click(screen.getByRole("button", { name: /save/i }));

    // 0 is the API's "clear" signal - an empty box must not send NaN or leave the old value in place.
    await waitFor(() =>
      expect(vi.mocked(api.updateUserSettings).mock.calls[0][0].llmTimeoutSeconds).toBe(0),
    );
  });
});
