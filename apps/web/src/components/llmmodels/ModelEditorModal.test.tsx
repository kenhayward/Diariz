import { render, screen, fireEvent } from "@testing-library/react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import type { LlmModel } from "../../lib/types";

// vi.mock's factory is hoisted above top-level const declarations, so the mock is created via vi.hoisted.
const { api } = vi.hoisted(() => ({
  api: { listModels: vi.fn(), updateModel: vi.fn(), createModel: vi.fn() },
}));
vi.mock("../../lib/api", () => ({ api, apiErrorMessage: (e: unknown) => String(e) }));

import ModelEditorModal from "./ModelEditorModal";

const MODELS: LlmModel[] = [
  {
    id: "a", name: "gpt-oss-20b", apiBase: "http://a/v1", hasApiKey: true, contextLength: 8192,
    parameters: { ModelBase: '{"temperature":0.5}' },
  },
  {
    id: "b", name: "qwen3-27b", apiBase: "http://b/v1", hasApiKey: false, contextLength: 32768,
    parameters: { ModelBase: '{"temperature":0.9}', Translation: '{"temperature":0.1}' },
  },
];

describe("ModelEditorModal", () => {
  beforeEach(() => vi.clearAllMocks());

  it("renders a Defaults panel plus one panel per call group", () => {
    render(<ModelEditorModal model={MODELS[0]} allModels={MODELS} onClose={vi.fn()} onSaved={vi.fn()} />);
    for (const label of ["Defaults", "Tags", "Actions", "Summaries", "Minutes and formulas", "Translation", "Chat"])
      expect(screen.getByText(label)).toBeTruthy();
  });

  it("copies another model's parameters into the editor without saving", () => {
    render(<ModelEditorModal model={MODELS[0]} allModels={MODELS} onClose={vi.fn()} onSaved={vi.fn()} />);
    fireEvent.change(screen.getByLabelText(/copy from/i), { target: { value: "b" } });

    // Loaded into the open editor for review...
    expect((screen.getByTestId("param-ModelBase-temperature") as HTMLInputElement).value).toBe("0.9");
    // ...but nothing is persisted until the admin saves.
    expect(api.updateModel).not.toHaveBeenCalled();
  });

  it("copies a group override the target model does not have", () => {
    render(<ModelEditorModal model={MODELS[0]} allModels={MODELS} onClose={vi.fn()} onSaved={vi.fn()} />);
    fireEvent.change(screen.getByLabelText(/copy from/i), { target: { value: "b" } });

    expect((screen.getByTestId("param-Translation-temperature") as HTMLInputElement).value).toBe("0.1");
  });

  it("never copies name, endpoint or key", () => {
    render(<ModelEditorModal model={MODELS[0]} allModels={MODELS} onClose={vi.fn()} onSaved={vi.fn()} />);
    fireEvent.change(screen.getByLabelText(/copy from/i), { target: { value: "b" } });

    // These are what make an entry distinct; copying them would produce a duplicate pointing elsewhere.
    expect((screen.getByLabelText(/^name/i) as HTMLInputElement).value).toBe("gpt-oss-20b");
    expect((screen.getByLabelText(/endpoint/i) as HTMLInputElement).value).toBe("http://a/v1");
  });

  it("does not offer the model being edited as a copy source", () => {
    render(<ModelEditorModal model={MODELS[0]} allModels={MODELS} onClose={vi.fn()} onSaved={vi.fn()} />);
    const options = Array.from(
      (screen.getByLabelText(/copy from/i) as HTMLSelectElement).options,
    ).map((o) => o.value);

    expect(options).not.toContain("a");
    expect(options).toContain("b");
  });

  it("sends no apiKey field when the key was left untouched", async () => {
    // The modal is never given the stored key, so it must omit it rather than send an empty string -
    // empty means "clear the key" to the API.
    api.updateModel.mockResolvedValue(MODELS[0]);
    render(<ModelEditorModal model={MODELS[0]} allModels={MODELS} onClose={vi.fn()} onSaved={vi.fn()} />);

    fireEvent.click(screen.getByRole("button", { name: /save/i }));

    await vi.waitFor(() => expect(api.updateModel).toHaveBeenCalled());
    expect(api.updateModel.mock.calls[0][1].apiKey).toBeUndefined();
  });
});
