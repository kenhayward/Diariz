import { render, screen } from "@testing-library/react";
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
    setLlmAssignments: vi.fn(),
    createModelFromEnvironment: vi.fn(),
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
      { id: "a", name: "gpt-oss-20b", apiBase: "http://only/v1", hasApiKey: false, contextLength: 8192, parameters: {} },
    ]);
    renderPage();

    // Matched on the endpoint, which appears once: the model NAME also fills every assignment select's
    // options, so finding it by name would be ambiguous rather than wrong.
    expect(await screen.findByText("http://only/v1")).toBeTruthy();
    expect(screen.queryByRole("button", { name: /create from environment/i })).toBeNull();
  });
});
