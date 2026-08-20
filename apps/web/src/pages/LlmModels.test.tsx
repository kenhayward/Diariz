import { render, screen, fireEvent, act } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, it, expect, vi, beforeEach } from "vitest";

const authState = { isPlatformAdmin: true };
vi.mock("../auth", () => ({ useAuth: () => authState }));

// vi.mock's factory is hoisted above top-level const declarations, so the mock is created via vi.hoisted.
const { api } = vi.hoisted(() => ({
  api: {
    listModels: vi.fn().mockResolvedValue([]),
    getLlmAssignments: vi.fn().mockResolvedValue({ defaultModelId: null, assignments: {} }),
    getLlmModelDefaults: vi.fn().mockResolvedValue({}),
    setLlmAssignments: vi.fn(),
    createModelFromEnvironment: vi.fn(),
    testModel: vi.fn(),
    deleteModel: vi.fn(),
  },
}));
vi.mock("../lib/api", () => ({ api, apiErrorMessage: (e: unknown) => String(e) }));

import LlmModels from "./LlmModels";

function renderPage() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter>
        <LlmModels />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe("LlmModels", () => {
  beforeEach(() => {
    authState.isPlatformAdmin = true;
    api.listModels.mockResolvedValue([]);
  });

  it("refuses to render the models for anyone who is not a platform administrator", () => {
    authState.isPlatformAdmin = false;
    renderPage();
    // The route only proves someone is signed in; the gate lives here, as it does in LlmUsage.
    expect(screen.queryByRole("button", { name: /add model/i })).toBeNull();
  });

  it("does not even fetch the models for a non-administrator", () => {
    // A refusal that still called the endpoint would leak endpoint names into the network log of someone
    // who is not allowed to see them.
    authState.isPlatformAdmin = false;
    renderPage();
    expect(api.listModels).not.toHaveBeenCalled();
  });

  it("offers Create from environment when no models exist", async () => {
    renderPage();
    expect(await screen.findByRole("button", { name: /create from environment/i })).toBeTruthy();
  });

  it("hides Create from environment once a model exists", async () => {
    // It is a one-time migration aid; the API refuses a second call, so leaving the button on screen would
    // offer an action that can only fail.
    api.listModels.mockResolvedValue([
      { id: "a", name: "gpt-oss-20b", displayName: null, apiBase: "http://only/v1", hasApiKey: false, chatEnabled: false, contextLength: 8192, parameters: {} },
    ]);
    renderPage();

    // Matched on the endpoint rather than the name: the name is also the accessible name of all seven
    // routing cells on that row, so finding it by name would be ambiguous rather than wrong. The sub-line
    // carries the context length alongside it, hence the substring match.
    expect(await screen.findByText(/http:\/\/only\/v1/)).toBeTruthy();
    expect(screen.queryByRole("button", { name: /create from environment/i })).toBeNull();
  });

  it("tests every model one at a time rather than all at once", async () => {
    // These are real calls to real endpoints, and several models commonly point at the SAME server -
    // firing them together would measure that server's queue instead of the models.
    api.listModels.mockResolvedValue([
      { id: "a", name: "one", displayName: null, apiBase: "http://a/v1", hasApiKey: false, chatEnabled: false, contextLength: 8192, parameters: {} },
      { id: "b", name: "two", displayName: null, apiBase: "http://b/v1", hasApiKey: false, chatEnabled: false, contextLength: 8192, parameters: {} },
    ]);
    let release: (v: unknown) => void = () => {};
    api.testModel.mockImplementation(() => new Promise((r) => { release = r; }));

    renderPage();
    fireEvent.click(await screen.findByRole("button", { name: /test all/i }));

    await vi.waitFor(() => expect(api.testModel).toHaveBeenCalledTimes(1));
    expect(api.testModel).toHaveBeenCalledTimes(1);

    await act(async () => {
      release({
        ok: true, httpStatus: 200, ttftMs: 1, durationMs: 2, promptTokens: null, completionTokens: null,
        reasoningTokens: null, totalTokens: null, finishReason: null, response: "", requestBodyJson: "{}",
        errorKind: null, message: null, offendingParameter: null,
      });
    });

    await vi.waitFor(() => expect(api.testModel).toHaveBeenCalledTimes(2));
  });
});
