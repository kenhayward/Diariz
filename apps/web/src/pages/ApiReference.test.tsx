import { render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter } from "react-router-dom";
import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("../lib/api", () => ({ api: { getOpenApiDocument: vi.fn() } }));
const scalarConfigs = vi.hoisted(() => [] as Array<Record<string, unknown>>);
vi.mock("@scalar/api-reference-react", () => ({
  ApiReferenceReact: ({ configuration }: { configuration: { content: unknown } & Record<string, unknown> }) => {
    scalarConfigs.push(configuration);
    return <div data-testid="scalar">{configuration.content ? "HAS_SPEC" : "NO_SPEC"}</div>;
  },
}));
import { api } from "../lib/api";
import ApiReference from "./ApiReference";

function renderIt() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <MemoryRouter>
        <ApiReference />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe("ApiReference", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    scalarConfigs.length = 0;
  });

  it("fetches the OpenAPI document and renders Scalar with it", async () => {
    (api.getOpenApiDocument as ReturnType<typeof vi.fn>).mockResolvedValue({ openapi: "3.1.0", info: {} });
    renderIt();
    await waitFor(() => expect(api.getOpenApiDocument).toHaveBeenCalled());
    expect(await screen.findByText("HAS_SPEC")).toBeTruthy();
  });

  // Scalar ships an AI "agent" chat drawer that talks to Scalar's hosted service. Diariz does not use it,
  // and its AI SDK dependency carries an advisory pinned upstream (#798) - so it is switched off, which
  // also keeps that chunk from ever loading.
  it("disables Scalar's AI agent drawer", async () => {
    (api.getOpenApiDocument as ReturnType<typeof vi.fn>).mockResolvedValue({ openapi: "3.1.0", info: {} });
    renderIt();
    await screen.findByText("HAS_SPEC");
    expect(scalarConfigs.at(-1)?.agent).toEqual({ disabled: true });
  });

  it("shows an error with a retry instead of a blank page when the document fails to load", async () => {
    (api.getOpenApiDocument as ReturnType<typeof vi.fn>).mockRejectedValue(new Error("500"));
    renderIt();
    // No Scalar, but a clear error message + a Retry button (not a blank screen).
    expect(await screen.findByText(/couldn't be loaded/i)).toBeTruthy();
    expect(screen.getByRole("button", { name: /retry/i })).toBeTruthy();
    expect(screen.queryByTestId("scalar")).toBeNull();
  });
});
