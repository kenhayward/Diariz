import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import type { LlmModel } from "../../lib/types";

// vi.mock's factory is hoisted above top-level const declarations, so the mock is created via vi.hoisted.
const { api } = vi.hoisted(() => ({
  api: { listModels: vi.fn(), updateModel: vi.fn(), createModel: vi.fn(), deleteModel: vi.fn() },
}));
vi.mock("../../lib/api", () => ({ api, apiErrorMessage: (e: unknown) => String(e) }));

import ModelEditorDrawer from "./ModelEditorDrawer";

const MODELS: LlmModel[] = [
  {
    id: "a", name: "gpt-oss-20b", apiBase: "http://a/v1", hasApiKey: true, contextLength: 8192,
    parameters: { ModelBase: '{"temperature":0.5}' },
  },
  {
    id: "b", name: "qwen3-27b", apiBase: "http://b/v1", hasApiKey: false, contextLength: 32768,
    parameters: { ModelBase: '{"temperature":0.9,"top_k":40}', Translation: '{"temperature":0.1}' },
  },
];

/// The application defaults, as the page hands them over - the bottom of the layer stack.
const DEFAULTS = { ModelBase: '{"temperature":0.3,"timeout_seconds":120}' };

function open(model: LlmModel | null = MODELS[0], props: Record<string, unknown> = {}) {
  return render(
    <ModelEditorDrawer
      model={model}
      allModels={MODELS}
      defaults={DEFAULTS}
      isDefaultModel={false}
      onClose={vi.fn()}
      onSaved={vi.fn()}
      onDeleted={vi.fn()}
      {...props}
    />,
  );
}

describe("ModelEditorDrawer", () => {
  beforeEach(() => vi.clearAllMocks());

  it("offers one tab per parameter group, starting on Defaults", () => {
    open();

    const tabs = screen.getAllByRole("tab").map((t) => t.textContent);
    expect(tabs).toHaveLength(7);
    expect(tabs[0]).toMatch(/Defaults/);
    expect(screen.getByRole("tab", { selected: true }).textContent).toMatch(/Defaults/);
  });

  it("counts the overrides on each tab, and says nothing when there are none", () => {
    open(MODELS[1]);

    // ModelBase carries temperature and top_k; Translation carries temperature; the rest carry nothing.
    expect(screen.getByRole("tab", { name: /Defaults/ }).textContent).toMatch(/2/);
    expect(screen.getByRole("tab", { name: /Translation/ }).textContent).toMatch(/1/);
    expect(screen.getByRole("tab", { name: /Tags/ }).textContent).not.toMatch(/\d/);
  });

  it("scopes the parameter list to the open tab", () => {
    open(MODELS[1]);

    expect((screen.getByTestId("param-ModelBase-temperature") as HTMLInputElement).value).toBe("0.9");

    fireEvent.click(screen.getByRole("tab", { name: /Translation/ }));

    expect((screen.getByTestId("param-Translation-temperature") as HTMLInputElement).value).toBe("0.1");
  });

  it("shows what a group inherits from the model's own Defaults rather than leaving it blank", () => {
    open(MODELS[1]);
    fireEvent.click(screen.getByRole("tab", { name: /Summaries/ }));

    // top_k is set on the model's Defaults and nowhere else, so Summaries inherits 40.
    expect(screen.getByText(/from Defaults · 40/)).toBeTruthy();
  });

  it("shows the application default on the Defaults tab, which has no model layer below it", () => {
    open(MODELS[0]);

    // temperature is overridden on this tab, so the row that proves the point is one the model leaves
    // alone: the timeout comes from the application defaults and nowhere else.
    expect(screen.getByText(/app default · 120/)).toBeTruthy();
  });

  it("previews the request body for the open tab, and follows what is typed", () => {
    open(MODELS[1]);
    fireEvent.click(screen.getByRole("tab", { name: /Summaries/ }));

    const preview = () => screen.getByTestId("request-preview").textContent ?? "";
    expect(preview()).toMatch(/"temperature": 0\.9/);

    fireEvent.change(screen.getByTestId("param-Summaries-temperature"), { target: { value: "0.2" } });

    expect(preview()).toMatch(/"temperature": 0\.2/);
  });

  it("keeps the behaviour flags out of the previewed body", () => {
    // timeout_seconds governs the client, not the request. A preview that lists it sends the admin
    // hunting through their server logs for a field no endpoint ever received.
    open(MODELS[1]);

    expect(screen.getByTestId("request-preview").textContent).not.toMatch(/timeout_seconds/);
  });

  it("saves only the groups that carry an override", async () => {
    // An empty layer would create a row that decides nothing while looking, in the database, exactly like
    // a deliberate set of overrides.
    api.updateModel.mockResolvedValue(MODELS[1]);
    open(MODELS[1]);

    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await vi.waitFor(() => expect(api.updateModel).toHaveBeenCalled());
    expect(Object.keys(api.updateModel.mock.calls[0][1].parameters).sort()).toEqual(["ModelBase", "Translation"]);
  });

  it("drops a group from the payload once its last override is returned to inherit", async () => {
    api.updateModel.mockResolvedValue(MODELS[1]);
    open(MODELS[1]);
    fireEvent.click(screen.getByRole("tab", { name: /Translation/ }));

    fireEvent.click(screen.getAllByRole("button", { name: /back to inherited/i })[0]);
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await vi.waitFor(() => expect(api.updateModel).toHaveBeenCalled());
    expect(Object.keys(api.updateModel.mock.calls[0][1].parameters)).toEqual(["ModelBase"]);
  });

  it("sends no apiKey field when the key was left untouched", async () => {
    // The drawer is never given the stored key, so it must omit it rather than send an empty string -
    // empty means "clear the key" to the API.
    api.updateModel.mockResolvedValue(MODELS[0]);
    open(MODELS[0]);

    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await vi.waitFor(() => expect(api.updateModel).toHaveBeenCalled());
    expect(api.updateModel.mock.calls[0][1].apiKey).toBeUndefined();
  });

  it("copies another model's parameters into the open drawer without saving", () => {
    open(MODELS[0]);

    fireEvent.change(screen.getByLabelText(/copy parameters from/i), { target: { value: "b" } });

    expect((screen.getByTestId("param-ModelBase-temperature") as HTMLInputElement).value).toBe("0.9");
    expect(api.updateModel).not.toHaveBeenCalled();
  });

  it("copies a group override the target model does not have", () => {
    open(MODELS[0]);

    fireEvent.change(screen.getByLabelText(/copy parameters from/i), { target: { value: "b" } });
    fireEvent.click(screen.getByRole("tab", { name: /Translation/ }));

    expect((screen.getByTestId("param-Translation-temperature") as HTMLInputElement).value).toBe("0.1");
  });

  it("never copies name, endpoint or key", () => {
    open(MODELS[0]);
    fireEvent.change(screen.getByLabelText(/copy parameters from/i), { target: { value: "b" } });

    fireEvent.click(screen.getByRole("button", { name: /connection/i }));

    // These are what make an entry distinct; copying them would produce a duplicate pointing elsewhere.
    expect((screen.getByLabelText(/^name/i) as HTMLInputElement).value).toBe("gpt-oss-20b");
    expect((screen.getByLabelText(/endpoint/i) as HTMLInputElement).value).toBe("http://a/v1");
  });

  it("does not offer the model being edited as a copy source", () => {
    open(MODELS[0]);
    const options = Array.from(
      (screen.getByLabelText(/copy parameters from/i) as HTMLSelectElement).options,
    ).map((o) => o.value);

    expect(options).not.toContain("a");
    expect(options).toContain("b");
  });

  it("returns every override on the open tab to inherit at once", () => {
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
    open(MODELS[1]);

    fireEvent.click(screen.getByRole("button", { name: /reset all to inherit/i }));
    confirm.mockRestore();

    expect(screen.getByRole("tab", { name: /Defaults/ }).textContent).not.toMatch(/\d/);
    // ...and only that tab.
    expect(screen.getByRole("tab", { name: /Translation/ }).textContent).toMatch(/1/);
  });

  it("warns before discarding unsaved overrides", () => {
    const onClose = vi.fn();
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(false);
    open(MODELS[0], { onClose });

    fireEvent.change(screen.getByTestId("param-ModelBase-temperature"), { target: { value: "0.8" } });
    fireEvent.click(screen.getByRole("button", { name: /close/i }));

    expect(confirm).toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();
    confirm.mockRestore();
  });

  it("closes without a prompt when nothing was changed", () => {
    const onClose = vi.fn();
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
    open(MODELS[0], { onClose });

    fireEvent.click(screen.getByRole("button", { name: /close/i }));

    expect(confirm).not.toHaveBeenCalled();
    expect(onClose).toHaveBeenCalled();
    confirm.mockRestore();
  });
});
