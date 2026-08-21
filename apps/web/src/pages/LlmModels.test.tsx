import { render, screen, fireEvent, act, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { QueryClient, QueryClientProvider, useQuery } from "@tanstack/react-query";
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
    setModelChatEnabled: vi.fn(),
    createModelFromEnvironment: vi.fn(),
    testModel: vi.fn(),
    deleteModel: vi.fn(),
  },
}));
vi.mock("../lib/api", () => ({ api, apiErrorMessage: (e: unknown) => String(e) }));

import LlmModels from "./LlmModels";
import { CHAT_MODELS_KEY } from "../lib/modelQueryKeys";

/// Stands in for the mounted ChatPanel, holding the picker's query in the SAME QueryClient. Without an
/// active observer an invalidation only marks the entry stale, so a passive cache check would not prove the
/// picker actually reloads.
function PickerProbe({ queryFn }: { queryFn: () => Promise<unknown> }) {
  // The SHARED key, not a copy of it - a hardcoded key here would keep passing after ChatPanel moved to a
  // different one, which is precisely the drift that caused the bug.
  useQuery({ queryKey: CHAT_MODELS_KEY, queryFn });
  return null;
}

function renderPageWithPicker(queryFn: () => Promise<unknown>) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <MemoryRouter>
        <LlmModels />
        <PickerProbe queryFn={queryFn} />
      </MemoryRouter>
    </QueryClientProvider>,
  );
  return qc;
}

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
      { id: "a", name: "gpt-oss-20b", displayName: null, description: null, apiBase: "http://only/v1", hasApiKey: false, chatEnabled: false, contextLength: 8192, parameters: {} },
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
      { id: "a", name: "one", displayName: null, description: null, apiBase: "http://a/v1", hasApiKey: false, chatEnabled: false, contextLength: 8192, parameters: {} },
      { id: "b", name: "two", displayName: null, description: null, apiBase: "http://b/v1", hasApiKey: false, chatEnabled: false, contextLength: 8192, parameters: {} },
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

  // ---- Keeping the chat picker in step with the model list ----

  const MODEL = {
    id: "a", name: "gpt-oss-20b", displayName: null, description: null, apiBase: "http://a/v1", hasApiKey: false,
    chatEnabled: false, contextLength: 8192, parameters: {},
  };

  it("reloads the chat model picker when a model is offered for chat", async () => {
    // The picker reads its own query key. Nothing on this page knew about that key, so ticking In chat
    // updated the grid and left the picker showing the previous set until the whole app was reloaded.
    api.listModels.mockResolvedValue([MODEL]);
    api.setModelChatEnabled.mockResolvedValue(undefined);
    const listChatModels = vi.fn().mockResolvedValue([]);

    renderPageWithPicker(listChatModels);
    await waitFor(() => expect(listChatModels).toHaveBeenCalledTimes(1));

    fireEvent.click(await screen.findByRole("checkbox", { name: /gpt-oss-20b/i }));

    await waitFor(() => expect(listChatModels).toHaveBeenCalledTimes(2));
  });

  it("reloads the chat model picker when the routing changes", async () => {
    // Moving the Chat dot changes which model the picker marks as the default, and which one it offers
    // implicitly - so a routing write has to reach it too, even though it writes a different resource.
    api.listModels.mockResolvedValue([MODEL]);
    api.setLlmAssignments.mockResolvedValue(undefined);
    const listChatModels = vi.fn().mockResolvedValue([]);

    renderPageWithPicker(listChatModels);
    await waitFor(() => expect(listChatModels).toHaveBeenCalledTimes(1));

    fireEvent.click(await screen.findByRole("radio", { name: /chat on gpt-oss-20b/i }));

    await waitFor(() => expect(listChatModels).toHaveBeenCalledTimes(2));
  });
});
